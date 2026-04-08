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

            // All regions are checked in parallel using the same account (obtained inside each CheckRegionAsync)
            // The first call to CheckRegionAsync will get the first connected account
            // All subsequent calls in this batch will reuse that same account
            // Throttle to max 10 concurrent checks to prevent resource exhaustion
            var tasks = enabledRegions.Select(region => CheckRegionWithThrottleAsync(region));

            await Task.WhenAll(tasks);

            _logger.LogInformation("Completed region tracking check");
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
                _logger.LogInformation("Checking region: {RegionName} for agent count", trackedRegion.RegionName);
                
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
                    _logger.LogDebug("No connected accounts available to check region {RegionName}", trackedRegion.RegionName);
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
                        await Task.WhenAny(regionReceived.Task, Task.Delay(10000)); // 10 second timeout
                        
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
                                // Retrieve agent count using RequestMapItems
                                var agentItemsReceived = new TaskCompletionSource<int>();
                                int agentCount = 0;
                                var expectedHandle = regionHandle.Value;

                                EventHandler<GridItemsEventArgs> itemHandler = (s, e) =>
                                {
                                    // Check if this event is for OUR region by comparing RegionHandle
                                    bool isCorrectRegion = false;
                                    try
                                    {
                                        var eventType = e.GetType();
                                        var regionHandleField = eventType.GetField("RegionHandle");
                                        if (regionHandleField != null)
                                        {
                                            var handleValue = regionHandleField.GetValue(e);
                                            if (handleValue != null)
                                            {
                                                var eventHandle = (ulong)handleValue;
                                                isCorrectRegion = (eventHandle == expectedHandle);
                                                if (!isCorrectRegion)
                                                {
                                                    _logger.LogDebug("Ignoring GridItems for handle {EventHandle}, waiting for {ExpectedHandle}", 
                                                        eventHandle, expectedHandle);
                                                    return;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            // No RegionHandle field found - log this once
                                            _logger.LogWarning("GridItemsEventArgs has no RegionHandle field - cannot filter events by region");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug("Error checking RegionHandle in GridItemsEventArgs: {Error}", ex.Message);
                                    }
                                    
                                    if (e.Type == GridItemType.AgentLocations)
                                    {
                                        agentCount = e.Items.Count;
                                        _logger.LogDebug("Received {Count} agent locations for region handle {Handle}", 
                                            agentCount, expectedHandle);
                                        agentItemsReceived.TrySetResult(agentCount);
                                    }
                                };

                                try
                                {
                                    client.Grid.GridItems += itemHandler;
                                    
                                    client.Grid.RequestMapItems(
                                        regionHandle.Value,
                                        GridItemType.AgentLocations,
                                        GridLayerType.Objects);
                                    
                                    // Wait for agent count or timeout
                                    await Task.WhenAny(agentItemsReceived.Task, Task.Delay(10000)); // 10 second timeout
                                    
                                    if (agentItemsReceived.Task.IsCompleted)
                                    {
                                        status.AgentCount = agentItemsReceived.Task.Result;
                                    }
                                    else
                                    {
                                        status.AgentCount = 0; // Timeout, assume 0 agents
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
                                    client.Grid.GridItems -= itemHandler;
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
                        client.Grid.GridRegion -= handler;
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
                
                _logger.LogInformation(
                    "Region {RegionName} status: {Status}, Agents: {AgentCount}",
                    trackedRegion.RegionName,
                    status.IsOnline ? "Online" : "Offline",
                    status.AgentCount?.ToString() ?? "unknown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving region status for {RegionName}", trackedRegion.RegionName);
            }
        }

        /// <summary>
        /// Get the first connected account instance for region checking.
        /// Automatically uses whichever account is logged in.
        /// </summary>
        private Core.WebRadegastInstance? GetFirstConnectedInstance(IAccountService accountService)
        {
            // This will need to iterate through all accounts and find the first connected one
            // For now, we need to add a method to AccountService to expose this
            // Using reflection to access the private _instances field as a temporary solution
            var instancesField = accountService.GetType().GetField("_instances",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (instancesField != null)
            {
                var instances = instancesField.GetValue(accountService) as System.Collections.Concurrent.ConcurrentDictionary<Guid, Core.WebRadegastInstance>;
                if (instances != null)
                {
                    foreach (var kvp in instances)
                    {
                        if (kvp.Value?.Client?.Network?.Connected == true)
                        {
                            _logger.LogDebug("Using account {AccountId} for region tracking", kvp.Key);
                            return kvp.Value;
                        }
                    }
                }
            }
            
            _logger.LogDebug("No connected accounts found for region tracking");
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
        /// Dispose of unmanaged resources
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _parallelCheckLimiter?.Dispose();
                _disposed = true;
            }
        }
    }
}
