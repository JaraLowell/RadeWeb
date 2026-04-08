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
            if (config == null || !config.Enabled)
            {
                _logger.LogDebug("Region tracking is disabled or config not found - no work performed");
                return;
            }

            // Filter to only enabled regions
            var enabledRegions = config.Regions.Where(r => r.Enabled).ToList();
            if (!enabledRegions.Any())
            {
                _logger.LogDebug("No enabled regions configured for tracking");
                return;
            }

            _logger.LogInformation("Starting region tracking check for {Count} regions", enabledRegions.Count);

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
                GridUrl = trackedRegion.GridUrl,
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

                // Get first connected instance from any account
                var connectedInstance = GetFirstConnectedInstance(accountService);
                if (connectedInstance?.Client?.Network.Connected != true)
                {
                    status.IsOnline = false;
                    status.AgentCount = null;
                    status.ErrorMessage = "No connected accounts available";
                    _logger.LogWarning("No connected accounts available to check region {RegionName}. Ensure at least one account is logged in.", trackedRegion.RegionName);
                }
                else
                {
                    // Query the region using GridClient
                    var client = connectedInstance.Client;
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
                                                        
                                                        // Get position to detect placeholder items
                                                        byte? localX = null;
                                                        byte? localY = null;
                                                        
                                                        var xProp = itemType.GetProperty("LocalX");
                                                        if (xProp != null)
                                                        {
                                                            var xValue = xProp.GetValue(item);
                                                            localX = xValue != null ? Convert.ToByte(xValue) : (byte?)null;
                                                        }
                                                        else
                                                        {
                                                            var xField = itemType.GetField("LocalX");
                                                            if (xField != null)
                                                            {
                                                                var xValue = xField.GetValue(item);
                                                                localX = xValue != null ? Convert.ToByte(xValue) : (byte?)null;
                                                            }
                                                        }
                                                        
                                                        var yProp = itemType.GetProperty("LocalY");
                                                        if (yProp != null)
                                                        {
                                                            var yValue = yProp.GetValue(item);
                                                            localY = yValue != null ? Convert.ToByte(yValue) : (byte?)null;
                                                        }
                                                        else
                                                        {
                                                            var yField = itemType.GetField("LocalY");
                                                            if (yField != null)
                                                            {
                                                                var yValue = yField.GetValue(item);
                                                                localY = yValue != null ? Convert.ToByte(yValue) : (byte?)null;
                                                            }
                                                        }
                                                        
                                                        // Filter out 0,0 placeholders - these indicate region online but empty
                                                        if (localX.HasValue && localY.HasValue && (localX.Value != 0 || localY.Value != 0))
                                                        {
                                                            matchingAgentCount++;
                                                        }
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
                                                _logger.LogDebug("Region {RegionName}: Filtered {Matching} real agents from {RegionItems} items for our region, {Total} total (handle: {Handle})", 
                                                    trackedRegion.RegionName, matchingAgentCount, itemsForOurRegion, e.Items.Count, currentRegionHandle);
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
                            }
                            else
                            {
                                status.AgentCount = 0;
                                _logger.LogDebug("Could not get RegionHandle for {RegionName}, cannot query agent count", trackedRegion.RegionName);
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
            }
            catch (Exception ex)
            {
                status.IsOnline = false;
                status.AgentCount = null;
                status.ErrorMessage = ex.Message;
                
                _logger.LogError(ex, "Error checking region {RegionName}", trackedRegion.RegionName);
            }

            // Save to database
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                context.RegionStatuses.Add(status);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving region status for {RegionName}", trackedRegion.RegionName);
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
