using Microsoft.EntityFrameworkCore;
using OpenMetaverse;
using RadegastWeb.Data;
using RadegastWeb.Models;
using System.Diagnostics;
using System.Text.Json;

namespace RadegastWeb.Services
{
    public interface IRegionTrackingService
    {
        Task CheckRegionsAsync();
        Task<List<RegionStatus>> GetRegionHistoryAsync(string regionName, DateTime? since = null);
        Task<List<RegionStatus>> GetLatestStatusesAsync();
        Task<RegionTrackingConfig?> GetConfigAsync();
        Task UpdateConfigAsync(RegionTrackingConfig config);
        Task<int> CleanupOldRecordsAsync(int keepDays = 32);
    }

    public class RegionTrackingService : IRegionTrackingService, IDisposable
    {
        private readonly IDbContextFactory<RadegastDbContext> _contextFactory;
        private readonly ILogger<RegionTrackingService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _configPath;
        private readonly SemaphoreSlim _parallelCheckLimiter = new SemaphoreSlim(10, 10); // Max 10 concurrent region checks
        private readonly SemaphoreSlim _agentCountLimiter = new SemaphoreSlim(5, 5); // Max 5 concurrent agent count queries
        private bool _disposed = false;

        public RegionTrackingService(
            IDbContextFactory<RadegastDbContext> contextFactory,
            ILogger<RegionTrackingService> logger,
            IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _contextFactory = contextFactory;
            _logger = logger;
            _serviceProvider = serviceProvider;
            
            // Get the data folder path from configuration or use default
            var dataFolder = configuration["DataFolder"] ?? "data";
            _configPath = Path.Combine(dataFolder, "tracking.json");
        }

        public async Task<RegionTrackingConfig?> GetConfigAsync()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _logger.LogWarning("Tracking config file not found at {Path}", _configPath);
                    return null;
                }

                var json = await File.ReadAllTextAsync(_configPath);
                return JsonSerializer.Deserialize<RegionTrackingConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading tracking config");
                return null;
            }
        }

        public async Task UpdateConfigAsync(RegionTrackingConfig config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                await File.WriteAllTextAsync(_configPath, json);
                _logger.LogInformation("Updated tracking config with {Count} regions", config.Regions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tracking config");
                throw;
            }
        }

        public async Task CheckRegionsAsync()
        {
            var config = await GetConfigAsync();
            if (config == null)
            {
                _logger.LogWarning("Region tracking config not found - cannot perform checks");
                return;
            }
            
            if (!config.Enabled)
            {
                _logger.LogDebug("Region tracking is DISABLED (enabled: false) - skipping all region checks");
                return;
            }

            // Filter to only enabled regions (skip regions with enabled: false)
            var enabledRegions = config.Regions.Where(r => r.Enabled).ToList();
            var disabledRegions = config.Regions.Where(r => !r.Enabled).ToList();
            
            if (!enabledRegions.Any())
            {
                if (disabledRegions.Any())
                {
                    _logger.LogInformation("All {Count} configured regions are DISABLED (enabled: false) - no regions to check", disabledRegions.Count);
                }
                else
                {
                    _logger.LogDebug("No regions configured in tracking.json - nothing to track");
                }
                return;
            }

            if (disabledRegions.Any())
            {
                _logger.LogInformation("Starting region tracking for {Enabled} enabled regions (skipping {Disabled} disabled regions)", 
                    enabledRegions.Count, disabledRegions.Count);
            }
            else
            {
                _logger.LogInformation("Starting region tracking check for {Count} regions", enabledRegions.Count);
            }

            // Verify we have at least one connected account before starting the checks
            var accountService = _serviceProvider.GetService<IAccountService>();
            if (accountService == null)
            {
                _logger.LogError("AccountService not available - cannot perform region tracking");
                return;
            }

            var testInstance = GetFirstConnectedInstance(accountService);
            if (testInstance == null)
            {
                _logger.LogWarning("No connected accounts available at start of region check cycle. All region checks will be marked as unavailable. Login at least one account to enable region tracking.");
                // Continue anyway to record the failure status in the database
            }

            // All regions are checked in parallel
            // Each CheckRegionAsync call will get a connected account dynamically
            // If the initially connected account disconnects mid-check, other checks may find a different account
            // Throttle to max 10 concurrent checks to prevent resource exhaustion
            var tasks = enabledRegions.Select(region => CheckRegionWithThrottleAsync(region));

            await Task.WhenAll(tasks);
        }

        private async Task CheckRegionWithThrottleAsync(TrackedRegion trackedRegion)
        {
            // Throttle parallel execution to prevent too many concurrent database connections
            await _parallelCheckLimiter.WaitAsync();
            try
            {
                await CheckRegionAsync(trackedRegion);
            }
            finally
            {
                _parallelCheckLimiter.Release();
            }
        }

        private async Task CheckRegionAsync(TrackedRegion trackedRegion)
        {
            var status = new RegionStatus
            {
                RegionName = trackedRegion.RegionName,
                CheckedAt = DateTime.UtcNow
            };

            try
            {
                // Get a connected account instance
                var accountService = _serviceProvider.GetService<IAccountService>();
                if (accountService == null)
                {
                    status.IsOnline = false;
                    status.AgentCount = null;
                    status.ErrorMessage = "AccountService not available";
                    _logger.LogWarning("AccountService not available for region tracking");
                    return;
                }

                // PRIORITY 1: Check if any account is currently IN this region
                // If so, we can get EVERYTHING from the simulator data (most accurate)
                var accountInRegion = GetAccountInRegion(accountService, trackedRegion.RegionName);
                
                GridClient client;
                bool useSimulatorData = false;
                
                if (accountInRegion?.Client?.Network?.Connected == true && 
                    accountInRegion.Client.Network.CurrentSim != null)
                {
                    // Use the account that's IN the region - best accuracy
                    client = accountInRegion.Client;
                    useSimulatorData = true;
                    _logger.LogDebug("Using account IN region {RegionName} for tracking (will use accurate simulator data)", 
                        trackedRegion.RegionName);
                }
                else
                {
                    // PRIORITY 2: No account in region, get any connected account for remote monitoring
                    var connectedInstance = GetFirstConnectedInstance(accountService);
                    if (connectedInstance?.Client?.Network.Connected != true)
                    {
                        status.IsOnline = false;
                        status.AgentCount = null;
                        status.ErrorMessage = "No connected accounts available";
                        _logger.LogWarning("No connected accounts available to check region {RegionName}. Ensure at least one account is logged in.", trackedRegion.RegionName);
                        await SaveRegionStatusAsync(status);
                        return;
                    }
                    client = connectedInstance.Client;
                    _logger.LogDebug("Using remote account for region {RegionName} tracking (will use map API)", 
                        trackedRegion.RegionName);
                }
                
                // If we have simulator data available, use it directly (skip map API query)
                if (useSimulatorData)
                {
                    await CheckRegionUsingSimulatorDataAsync(trackedRegion, status, client);
                }
                else
                {
                    // Query the region using GridClient map API
                    await CheckRegionUsingMapApiAsync(trackedRegion, status, client, accountService);
                }
            }
            catch (Exception ex)
            {
                status.IsOnline = false;
                status.AgentCount = null;
                status.ErrorMessage = ex.Message;
                
                _logger.LogError(ex, "Error checking region {RegionName}", trackedRegion.RegionName);
            }

            await SaveRegionStatusAsync(status);
        }

        /// <summary>
        /// Check region using direct simulator data (when we have an account IN the region)
        /// </summary>
        private async Task CheckRegionUsingSimulatorDataAsync(TrackedRegion trackedRegion, RegionStatus status, GridClient client)
        {
            try
            {
                var sim = client.Network.CurrentSim;
                if (sim == null)
                {
                    _logger.LogWarning("CurrentSim is null for region {RegionName}", trackedRegion.RegionName);
                    status.IsOnline = false;
                    status.ErrorMessage = "CurrentSim is null";
                    return;
                }
                
                status.IsOnline = true;
                status.RegionHandle = sim.Handle;
                
                // Get accurate avatar count from simulator
                status.AgentCount = sim.AvatarPositions?.Count ?? 0;
                
                // Extract grid coordinates from handle (divide by 256 to convert meters to grid units)
                status.LocationX = (uint)((sim.Handle >> 32) / 256);
                status.LocationY = (uint)((sim.Handle & 0xFFFFFFFF) / 256);
                
                // Get region properties
                status.SizeX = 256; // Default, could be var region
                status.SizeY = 256;
                
                // Get access level
                var accessValue = sim.Access;
                status.AccessLevel = ConvertAccessLevel(accessValue);
                
                _logger.LogInformation("Region {RegionName}: Online with {AgentCount} avatars at ({GridX}, {GridY}) [SIMULATOR DATA - highly accurate]", 
                    trackedRegion.RegionName, status.AgentCount, status.LocationX, status.LocationY);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting simulator data for region {RegionName}", trackedRegion.RegionName);
                status.IsOnline = false;
                status.ErrorMessage = ex.Message;
            }
            
            await Task.CompletedTask;
        }

        /// <summary>
        /// Check region using map API (remote monitoring when no account is in region)
        /// </summary>
        private async Task CheckRegionUsingMapApiAsync(TrackedRegion trackedRegion, RegionStatus status, GridClient client, IAccountService accountService)
        {
            var regionReceived = new TaskCompletionSource<GridRegion?>();
            
            EventHandler<GridRegionEventArgs> handler = (s, e) =>
            {
                if (e.Region.Name.Equals(trackedRegion.RegionName, StringComparison.OrdinalIgnoreCase))
                {
                    status.IsOnline = true;
                    
                    try
                    {
                        var regionType = e.Region.GetType();
                        
                        // Get RegionHandle from field
                        var handleField = regionType.GetField("RegionHandle");
                        if (handleField != null)
                        {
                            status.RegionHandle = (ulong?)handleField.GetValue(e.Region);
                        }
                        
                        // Get location coordinates (fields return int, convert to uint)
                        var xField = regionType.GetField("X");
                        var yField = regionType.GetField("Y");
                        if (xField != null)
                        {
                            var xValue = xField.GetValue(e.Region);
                            status.LocationX = xValue != null ? Convert.ToUInt32(xValue) : (uint?)null;
                        }
                        if (yField != null)
                        {
                            var yValue = yField.GetValue(e.Region);
                            status.LocationY = yValue != null ? Convert.ToUInt32(yValue) : (uint?)null;
                        }
                        
                        // Get access level (maturity rating)
                        var accessField = regionType.GetField("Access");
                        var accessProp = regionType.GetProperty("Access");
                        
                        if (accessProp != null)
                        {
                            var accessValue = accessProp.GetValue(e.Region);
                            if (accessValue != null)
                            {
                                // Access is typically a SimAccess enum: PG=13, Mature=21, Adult=42
                                status.AccessLevel = ConvertAccessLevel(accessValue);
                            }
                        }
                        else if (accessField != null)
                        {
                            var accessValue = accessField.GetValue(e.Region);
                            if (accessValue != null)
                            {
                                status.AccessLevel = ConvertAccessLevel(accessValue);
                            }
                        }
                        
                        status.SizeX = 256; // Standard region size
                        status.SizeY = 256;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not get region properties for {RegionName}: {Error}", trackedRegion.RegionName, ex.Message);
                    }
                    
                    regionReceived.TrySetResult(e.Region);
                }
            };

            try
            {
                client.Grid.GridRegion += handler;
                client.Grid.RequestMapRegion(trackedRegion.RegionName, GridLayerType.Objects);
                
                // Wait for response or timeout
                var completedTask = await Task.WhenAny(regionReceived.Task, Task.Delay(10000)); // 10 second timeout
                
                // If timeout occurred, cancel the TaskCompletionSource to prevent memory leaks
                if (completedTask != regionReceived.Task)
                {
                    regionReceived.TrySetCanceled();
                }
                
                // Get agent count if region was found
                if (regionReceived.Task.IsCompleted && regionReceived.Task.Result != null)
                {
                    var region = regionReceived.Task.Result;
                    
                    // Get region handle - we already stored it in status, or get from field
                    ulong? regionHandle = status.RegionHandle;
                    
                    if (!regionHandle.HasValue)
                    {
                        try
                        {
                            var regionType = region.GetType();
                            var handleField = regionType.GetField("RegionHandle");
                            if (handleField != null)
                            {
                                regionHandle = (ulong?)handleField.GetValue(region);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Could not get RegionHandle for {RegionName}: {Error}", trackedRegion.RegionName, ex.Message);
                        }
                    }
                    
                    // Use map API to get agent count (less accurate than simulator data)
                    if (regionHandle.HasValue)
                    {
                                // Limit concurrent agent count queries to 5 at a time for performance
                                // Now safe to parallelize because we filter items by RegionHandle
                                await _agentCountLimiter.WaitAsync();
                                try
                                {
                                    var currentRegionHandle = regionHandle.Value;
                                    var agentItemsReceived = new TaskCompletionSource<int>();

                                    EventHandler<GridItemsEventArgs> itemHandler = (s, e) =>
                                    {
                                        if (e.Type == GridItemType.AgentLocations)
                                        {
                                            // Use counter instead of List to reduce memory allocations
                                            int matchingAgentCount = 0;
                                            int itemsForOurRegion = 0;
                                            
                                            foreach (var item in e.Items)
                                            {
                                                try
                                                {
                                                    var itemType = item.GetType();
                                                    
                                                    // Get RegionHandle from the item
                                                    ulong? itemRegionHandle = null;
                                                    var handleProp = itemType.GetProperty("RegionHandle");
                                                    if (handleProp != null)
                                                    {
                                                        itemRegionHandle = (ulong?)handleProp.GetValue(item);
                                                    }
                                                    else
                                                    {
                                                        var handleField = itemType.GetField("RegionHandle");
                                                        if (handleField != null)
                                                        {
                                                            itemRegionHandle = (ulong?)handleField.GetValue(item);
                                                        }
                                                    }
                                                    
                                                    // Only process items for the region we're querying
                                                    if (itemRegionHandle.HasValue && itemRegionHandle.Value == currentRegionHandle)
                                                    {
                                                        itemsForOurRegion++;
                                                        
                                                        // Get the agent count from the item
                                                        // In LibreMetaverse, the type is MapAgentLocation with an AvatarCount field
                                                        int agentCount = 1; // Default: one agent per item
                                                        bool foundCount = false;
                                                        
                                                        // Try AvatarCount field/property first (this is what LibreMetaverse uses)
                                                        var avatarCountField = itemType.GetField("AvatarCount");
                                                        if (avatarCountField != null)
                                                        {
                                                            var avatarCountValue = avatarCountField.GetValue(item);
                                                            if (avatarCountValue != null)
                                                            {
                                                                agentCount = Convert.ToInt32(avatarCountValue);
                                                                foundCount = true;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            var avatarCountProp = itemType.GetProperty("AvatarCount");
                                                            if (avatarCountProp != null)
                                                            {
                                                                var avatarCountValue = avatarCountProp.GetValue(item);
                                                                if (avatarCountValue != null)
                                                                {
                                                                    agentCount = Convert.ToInt32(avatarCountValue);
                                                                    foundCount = true;
                                                                }
                                                            }
                                                        }
                                                        
                                                        // Fallback: try Extra field/property (might be used in older versions)
                                                        if (!foundCount)
                                                        {
                                                            var extraProp = itemType.GetProperty("Extra");
                                                            if (extraProp != null)
                                                            {
                                                                var extraValue = extraProp.GetValue(item);
                                                                if (extraValue != null)
                                                                {
                                                                    agentCount = Convert.ToInt32(extraValue);
                                                                    foundCount = true;
                                                                }
                                                            }
                                                            else
                                                            {
                                                                var extraField = itemType.GetField("Extra");
                                                                if (extraField != null)
                                                                {
                                                                    var extraValue = extraField.GetValue(item);
                                                                    if (extraValue != null)
                                                                    {
                                                                        agentCount = Convert.ToInt32(extraValue);
                                                                        foundCount = true;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        
                                                        // Fallback: try Count property/field
                                                        if (!foundCount)
                                                        {
                                                            var countProp = itemType.GetProperty("Count");
                                                            if (countProp != null)
                                                            {
                                                                var countValue = countProp.GetValue(item);
                                                                if (countValue != null)
                                                                {
                                                                    agentCount = Convert.ToInt32(countValue);
                                                                    foundCount = true;
                                                                }
                                                            }
                                                            else
                                                            {
                                                                var countField = itemType.GetField("Count");
                                                                if (countField != null)
                                                                {
                                                                    var countValue = countField.GetValue(item);
                                                                    if (countValue != null)
                                                                    {
                                                                        agentCount = Convert.ToInt32(countValue);
                                                                        foundCount = true;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        
                                                        // Sum the count from each item
                                                        // Unlike the old code, we DON'T filter (0,0) - those are valid positions
                                                        matchingAgentCount += agentCount;
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger.LogWarning("Error filtering agent item: {Error}", ex.Message);
                                                }
                                            }
                                            
                                            // Only complete if this event contains items for OUR region, or if the response is empty
                                            if (itemsForOurRegion > 0 || e.Items.Count == 0)
                                            {
                                                _logger.LogDebug("Region {RegionName}: Counted {AgentCount} agents from {RegionItems} map items (handle: {Handle}) [REMOTE MONITORING - less accurate than being IN region]", 
                                                    trackedRegion.RegionName, matchingAgentCount, itemsForOurRegion, currentRegionHandle);
                                                agentItemsReceived.TrySetResult(matchingAgentCount);
                                            }
                                            else
                                            {
                                                _logger.LogDebug("Ignoring GridItems event - no items for region {RegionName} (handle: {Handle}), total items: {Total}", 
                                                    trackedRegion.RegionName, currentRegionHandle, e.Items.Count);
                                            }
                                        }
                                    };

                                    try
                                    {
                                        client.Grid.GridItems += itemHandler;
                                        
                                        _logger.LogDebug("Requesting agent count for {RegionName} (handle: {Handle})", 
                                            trackedRegion.RegionName, currentRegionHandle);
                                        
                                        client.Grid.RequestMapItems(
                                            currentRegionHandle,
                                            GridItemType.AgentLocations,
                                            GridLayerType.Objects);
                                        
                                        // Wait for response or timeout (3s should be enough)
                                        var agentCompletedTask = await Task.WhenAny(agentItemsReceived.Task, Task.Delay(3000));
                                        
                                        if (agentItemsReceived.Task.IsCompleted)
                                        {
                                            status.AgentCount = agentItemsReceived.Task.Result;
                                        }
                                        else
                                        {
                                            status.AgentCount = 0; // Timeout, assume 0 agents
                                            // Cancel the TaskCompletionSource to prevent memory leak
                                            agentItemsReceived.TrySetCanceled();
                                            _logger.LogWarning("Timeout getting agent count for {RegionName}", trackedRegion.RegionName);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        status.AgentCount = 0;
                                        _logger.LogWarning("Error getting agent count for {RegionName}: {Error}", trackedRegion.RegionName, ex.Message);
                                    }
                                    finally
                                    {
                                        // Ensure event handler is removed to prevent memory leaks
                                        try
                                        {
                                            client.Grid.GridItems -= itemHandler;
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning("Error removing GridItems event handler: {Error}", ex.Message);
                                        }
                                    }
                                }
                                finally
                                {
                                    _agentCountLimiter.Release();
                                }
                            
                            // If still no agent count, set to 0
                            if (status.AgentCount == null)
                            {
                                status.AgentCount = 0;
                                _logger.LogWarning("Failed to get agent count for {RegionName} via map API", trackedRegion.RegionName);
                            }
                        }
                        else
                        {
                            status.AgentCount = 0;
                            _logger.LogDebug("Could not get RegionHandle for {RegionName}, cannot query agent count via map API", trackedRegion.RegionName);
                        }
                }
                else
                {
                    status.IsOnline = false;
                    status.AgentCount = null;
                    status.ErrorMessage = "Region not found or timeout";
                    _logger.LogDebug("Region {RegionName} not found or request timed out", trackedRegion.RegionName);
                }
            }
            finally
            {
                // Ensure event handler is removed to prevent memory leaks
                try
                {
                    client.Grid.GridRegion -= handler;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error removing GridRegion event handler: {Error}", ex.Message);
                }
            }
        }

        /// <summary>
        /// Save region status to database
        /// </summary>
        private async Task SaveRegionStatusAsync(RegionStatus status)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                context.RegionStatuses.Add(status);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving region status for {RegionName}", status.RegionName);
            }
        }

        /// <summary>
        /// Convert SimAccess enum value to human-readable string
        /// </summary>
        private string ConvertAccessLevel(object accessValue)
        {
            try
            {
                // SimAccess enum values: PG=13, Mature=21, Adult=42
                // The value could be the enum itself or the integer value
                string? accessStr = accessValue.ToString();
                
                if (string.IsNullOrEmpty(accessStr))
                {
                    return "Unknown";
                }
                
                // First try to parse as integer
                if (int.TryParse(accessStr, out int accessInt))
                {
                    return accessInt switch
                    {
                        13 => "General (PG)",
                        21 => "Moderate",
                        42 => "Adult",
                        _ => $"Unknown ({accessInt})"
                    };
                }
                
                // Try enum name
                return accessStr switch
                {
                    "PG" => "General (PG)",
                    "Mature" => "Moderate",
                    "Adult" => "Adult",
                    _ => accessStr ?? "Unknown"
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error converting access level: {Error}", ex.Message);
                return "Unknown";
            }
        }

        /// <summary>
        /// Get an account that is currently IN the specified region.
        /// This allows us to use the accurate CurrentSim.AvatarPositions.Count
        /// instead of the less accurate map items API.
        /// </summary>
        private Core.WebRadegastInstance? GetAccountInRegion(IAccountService accountService, string regionName)
        {
            var instancesField = accountService.GetType().GetField("_instances",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (instancesField == null)
            {
                return null;
            }
            
            var instances = instancesField.GetValue(accountService) as System.Collections.Concurrent.ConcurrentDictionary<Guid, Core.WebRadegastInstance>;
            if (instances == null || instances.Count == 0)
            {
                return null;
            }
            
            // Find an account that is in this specific region
            foreach (var kvp in instances)
            {
                var instance = kvp.Value;
                try
                {
                    if (instance?.Client?.Network?.Connected == true &&
                        instance.Client.Network.CurrentSim != null &&
                        instance.Client.Network.CurrentSim.Name.Equals(regionName, StringComparison.OrdinalIgnoreCase))
                    {
                        string accountName = instance.AccountInfo != null ? $"{instance.AccountInfo.FirstName} {instance.AccountInfo.LastName}" : "unknown";
                        _logger.LogDebug("Found account {AccountId} ({Name}) currently IN region {RegionName}", 
                            kvp.Key, accountName, regionName);
                        return instance;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Error checking if account {AccountId} is in region: {Error}", kvp.Key, ex.Message);
                }
            }
            
            return null;
        }

        /// <summary>
        /// Get the first connected account instance for region checking.
        /// Automatically uses whichever account is logged in and fully functional.
        /// This method performs thorough validation to ensure the account can perform region queries.
        /// </summary>
        private Core.WebRadegastInstance? GetFirstConnectedInstance(IAccountService accountService)
        {
            // This will need to iterate through all accounts and find the first connected one
            // For now, we need to add a method to AccountService to expose this
            // Using reflection to access the private _instances field as a temporary solution
            var instancesField = accountService.GetType().GetField("_instances",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (instancesField == null)
            {
                _logger.LogError("Could not find _instances field via reflection in AccountService");
                return null;
            }
            
            var instances = instancesField.GetValue(accountService) as System.Collections.Concurrent.ConcurrentDictionary<Guid, Core.WebRadegastInstance>;
            if (instances == null)
            {
                _logger.LogError("_instances field exists but could not be cast to ConcurrentDictionary<Guid, WebRadegastInstance>");
                return null;
            }
            
            if (instances.Count == 0)
            {
                _logger.LogWarning("No account instances in dictionary to check");
            }
            
            // Try all instances to find a properly connected one
            foreach (var kvp in instances)
            {
                var instance = kvp.Value;
                if (instance == null)
                {
                    continue;
                }
                
                // Check multiple indicators of a healthy connection:
                // 1. Network.Connected must be true
                // 2. Client must be initialized
                // 3. Status should not indicate offline or login in progress
                // 4. Client should have a valid session
                
                try
                {
                    bool isNetworkConnected = instance.Client?.Network?.Connected == true;
                    bool hasValidClient = instance.Client != null;
                    string status = instance.Status ?? "Unknown";
                    bool statusIndicatesOnline = !status.Contains("Offline", StringComparison.OrdinalIgnoreCase) 
                                               && !status.Contains("Disconnected", StringComparison.OrdinalIgnoreCase)
                                               && !status.StartsWith("Login:", StringComparison.OrdinalIgnoreCase);
                    
                    // Additional check: verify the account info shows connected state
                    // Note: This is advisory only - Network.Connected is the source of truth
                    bool accountInfoConnected = instance.AccountInfo?.IsConnected == true;
                    
                    // Primary check: Network must be connected AND status must indicate online
                    // AccountInfo.IsConnected is advisory (may be out of sync due to being a copy)
                    if (isNetworkConnected && hasValidClient && statusIndicatesOnline)
                    {
                        string accountName = instance.AccountInfo != null ? $"{instance.AccountInfo.FirstName} {instance.AccountInfo.LastName}" : "unknown";
                        
                        if (!accountInfoConnected)
                        {
                            _logger.LogWarning("Using account {AccountId} ({Name}) for region tracking despite AccountInfo.IsConnected=false. Network reports connected. Status: {Status}", 
                                kvp.Key, accountName, status);
                        }
                        else
                        {
                            _logger.LogDebug("Using account {AccountId} ({Name}) for region tracking - Status: {Status}", 
                                kvp.Key, accountName, status);
                        }
                        
                        return instance;
                    }
                    else
                    {
                        string accountName = instance.AccountInfo != null ? $"{instance.AccountInfo.FirstName} {instance.AccountInfo.LastName}" : "unknown";
                        _logger.LogInformation("Skipping account {AccountId} ({Name}) - NetworkConnected: {Network}, ValidClient: {Client}, StatusOnline: {Status}, AccountConnected: {Account}, CurrentStatus: '{CurrentStatus}'", 
                            kvp.Key,
                            accountName,
                            isNetworkConnected,
                            hasValidClient,
                            statusIndicatesOnline,
                            accountInfoConnected,
                            status);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error checking connection status for account {AccountId}: {Error}", kvp.Key, ex.Message);
                }
            }
            
            _logger.LogWarning("No connected accounts found for region tracking. Check that at least one account is logged in.");
            return null;
        }

        public async Task<List<RegionStatus>> GetRegionHistoryAsync(string regionName, DateTime? since = null)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            var query = context.RegionStatuses
                .Where(r => r.RegionName == regionName);
            
            if (since.HasValue)
            {
                query = query.Where(r => r.CheckedAt >= since.Value);
            }
            
            return await query
                .OrderByDescending(r => r.CheckedAt)
                .Take(1000) // Limit to 1000 most recent
                .ToListAsync();
        }

        public async Task<List<RegionStatus>> GetLatestStatusesAsync()
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            // Check if there are any records first (handles empty database case)
            if (!await context.RegionStatuses.AnyAsync())
            {
                return new List<RegionStatus>();
            }
            
            // Get all statuses (limited by the 32-day cleanup, so not too many records)
            // Then group in memory to avoid EF Core translation issues
            var allStatuses = await context.RegionStatuses.ToListAsync();
            
            // Group by region and get the most recent status for each
            var latestStatuses = allStatuses
                .GroupBy(r => r.RegionName)
                .Select(g => g.OrderByDescending(r => r.CheckedAt).First())
                .ToList();
            
            return latestStatuses;
        }

        /// <summary>
        /// Cleanup old region tracking records to prevent database bloat.
        /// Deletes records older than the specified number of days.
        /// </summary>
        /// <param name="keepDays">Number of days of history to keep (default: 32 days)</param>
        /// <returns>Number of records deleted</returns>
        public async Task<int> CleanupOldRecordsAsync(int keepDays = 32)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-keepDays);
                
                await using var context = await _contextFactory.CreateDbContextAsync();
                
                // First, count how many records will be deleted (for logging)
                var recordCount = await context.RegionStatuses
                    .Where(r => r.CheckedAt < cutoffDate)
                    .CountAsync();
                
                if (recordCount == 0)
                {
                    _logger.LogDebug("No old region tracking records to cleanup (older than {Days} days)", keepDays);
                    return 0;
                }
                
                // Use ExecuteDeleteAsync for efficient bulk delete without loading into memory
                // This is much more memory-efficient for large deletions
                var deletedCount = await context.RegionStatuses
                    .Where(r => r.CheckedAt < cutoffDate)
                    .ExecuteDeleteAsync();
                
                _logger.LogInformation(
                    "Cleaned up {Count} region tracking records older than {Days} days (before {CutoffDate})",
                    deletedCount,
                    keepDays,
                    cutoffDate);
                
                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old region tracking records");
                throw;
            }
        }

        /// <summary>
        /// Dispose of unmanaged resources and release any waiting threads
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    // Release any threads waiting on the semaphores before disposing
                    // This prevents potential deadlocks if Dispose is called during operation
                    while (_parallelCheckLimiter.CurrentCount < 10)
                    {
                        try { _parallelCheckLimiter.Release(); } catch { break; }
                    }
                    
                    while (_agentCountLimiter.CurrentCount < 5)
                    {
                        try { _agentCountLimiter.Release(); } catch { break; }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error releasing semaphores during dispose: {Error}", ex.Message);
                }
                finally
                {
                    _parallelCheckLimiter?.Dispose();
                    _agentCountLimiter?.Dispose();
                    _disposed = true;
                }
            }
        }
    }
}
