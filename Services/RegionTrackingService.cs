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
        private readonly string _configPath;
        private readonly SemaphoreSlim _parallelCheckLimiter = new SemaphoreSlim(10, 10); // Max 10 concurrent region checks
        private bool _disposed = false;

        public RegionTrackingService(
            IDbContextFactory<RadegastDbContext> contextFactory,
            ILogger<RegionTrackingService> logger,
            IConfiguration configuration)
        {
            _contextFactory = contextFactory;
            _logger = logger;
            
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
                
                // TODO: Integrate with AccountService to query region and agent count
                // Similar to how Firestorm's LLSimInfo::updateAgentCount() works
                //
                // IMPORTANT: GetFirstConnectedInstance() returns THE FIRST available logged-in account.
                // - If account "Alice" is logged in, it returns Alice
                // - If Alice logs off and "Bob" is logged in, next check returns Bob
                // - This happens automatically - no manual handover needed!
                //
                // Implementation approach:
                // 1. Get a connected account from AccountService (first available)
                // 2. Use client.Grid.RequestMapRegion() to query the region
                // 3. Use client.Grid.RequestMapItems() with MAP_ITEM_AGENT_LOCATIONS
                // 4. Count agents returned for this region
                //
                // Example:
                // var accountService = _serviceProvider.GetService<IAccountService>();
                // var instance = accountService.GetFirstConnectedInstance();
                // if (instance?.Client?.Network.Connected == true)
                // {
                //     var regionReceived = new TaskCompletionSource<GridRegion?>();
                //     EventHandler<GridRegionEventArgs> handler = (s, e) => {
                //         if (e.Region.Name.Equals(trackedRegion.RegionName, 
                //             StringComparison.OrdinalIgnoreCase))
                //         {
                //             status.IsOnline = true;
                //             status.RegionHandle = e.Region.RegionHandle;
                //             status.RegionUuid = e.Region.RegionID.ToString();
                //             status.LocationX = e.Region.X;
                //             status.LocationY = e.Region.Y;
                //             status.SizeX = e.Region.SizeX;
                //             status.SizeY = e.Region.SizeY;
                //             status.AccessLevel = e.Region.Access.ToString();
                //             regionReceived.TrySetResult(e.Region);
                //         }
                //     };
                //     instance.Client.Grid.GridRegion += handler;
                //     instance.Client.Grid.RequestMapRegion(trackedRegion.RegionName, GridLayerType.Objects);
                //     await Task.WhenAny(regionReceived.Task, Task.Delay(5000));
                //     instance.Client.Grid.GridRegion -= handler;
                //
                //     // Then get agent count
                //     if (regionReceived.Task.IsCompleted && regionReceived.Task.Result != null)
                //     {
                //         var agentItemsReceived = new TaskCompletionSource<int>();
                //         int agentCount = 0;
                //         EventHandler<GridItemsEventArgs> itemHandler = (s, e) => {
                //             if (e.Type == GridItemType.AgentLocations)
                //             {
                //                 agentCount = e.Items.Count;
                //                 agentItemsReceived.TrySetResult(agentCount);
                //             }
                //         };
                //         instance.Client.Grid.GridItems += itemHandler;
                //         instance.Client.Grid.RequestMapItems(
                //             regionReceived.Task.Result.RegionHandle,
                //             GridItemType.AgentLocations,
                //             GridLayerType.Objects);
                //         await Task.WhenAny(agentItemsReceived.Task, Task.Delay(5000));
                //         instance.Client.Grid.GridItems -= itemHandler;
                //         
                //         status.AgentCount = agentCount;
                //     }
                // }
                
                status.IsOnline = false;
                status.AgentCount = null;
                status.ErrorMessage = "Integration with AccountService required - see code comments for implementation";
                
                _logger.LogDebug(
                    "Region {RegionName} check completed - awaiting AccountService integration",
                    trackedRegion.RegionName);
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
