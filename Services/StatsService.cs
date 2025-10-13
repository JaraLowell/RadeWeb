using Microsoft.EntityFrameworkCore;
using RadegastWeb.Data;
using RadegastWeb.Models;
using OpenMetaverse;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Interface for visitor statistics service
    /// </summary>
    public interface IStatsService
    {
        /// <summary>
        /// Record a visitor sighting in a region
        /// </summary>
        Task RecordVisitorAsync(string avatarId, string regionName, ulong simHandle, 
            string? avatarName = null, string? displayName = null);
        
        /// <summary>
        /// Get daily visitor statistics for a region over a date range
        /// </summary>
        Task<VisitorStatsSummaryDto> GetRegionStatsAsync(string regionName, DateTime startDate, DateTime endDate);
        
        /// <summary>
        /// Get daily visitor statistics for all regions over a date range
        /// </summary>
        Task<List<VisitorStatsSummaryDto>> GetAllRegionStatsAsync(DateTime startDate, DateTime endDate);
        
        /// <summary>
        /// Get unique visitors across all regions for a date range
        /// </summary>
        Task<List<UniqueVisitorDto>> GetUniqueVisitorsAsync(DateTime startDate, DateTime endDate, string? regionName = null);
        
        /// <summary>
        /// Get regions currently being monitored by connected accounts
        /// </summary>
        Task<List<string>> GetMonitoredRegionsAsync();
        
        /// <summary>
        /// Clean up old visitor records older than specified days
        /// </summary>
        Task CleanupOldRecordsAsync(int keepDays = 90);
        
        /// <summary>
        /// Set the current region for an account (for deduplication)
        /// </summary>
        void SetAccountRegion(Guid accountId, string regionName, ulong simHandle);
        
        /// <summary>
        /// Remove account region tracking when account disconnects
        /// </summary>
        void RemoveAccountRegion(Guid accountId);
        
        /// <summary>
        /// Check if any other account is already monitoring this region
        /// </summary>
        bool IsRegionAlreadyMonitored(string regionName, Guid excludeAccountId);
    }
    
    /// <summary>
    /// Service to track and manage visitor statistics
    /// </summary>
    public class StatsService : IStatsService
    {
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        private readonly ILogger<StatsService> _logger;
        
        // Thread-safe dictionary to track which accounts are monitoring which regions
        // This prevents duplicate recording when multiple accounts are in the same region
        private readonly ConcurrentDictionary<Guid, (string RegionName, ulong SimHandle)> _accountRegions = new();
        
        // Cache for recent visitor recordings to prevent too frequent database writes
        private readonly ConcurrentDictionary<string, DateTime> _recentRecordings = new();
        private readonly TimeSpan _recordingCooldown = TimeSpan.FromMinutes(5); // Don't record same avatar twice within 5 minutes
        
        public StatsService(IDbContextFactory<RadegastDbContext> dbContextFactory, ILogger<StatsService> logger)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }
        
        public void SetAccountRegion(Guid accountId, string regionName, ulong simHandle)
        {
            _accountRegions.AddOrUpdate(accountId, (regionName, simHandle), (key, oldValue) => (regionName, simHandle));
            _logger.LogDebug("Account {AccountId} is now monitoring region {RegionName}", accountId, regionName);
        }
        
        public void RemoveAccountRegion(Guid accountId)
        {
            if (_accountRegions.TryRemove(accountId, out var region))
            {
                _logger.LogDebug("Account {AccountId} stopped monitoring region {RegionName}", accountId, region.RegionName);
            }
        }
        
        public bool IsRegionAlreadyMonitored(string regionName, Guid excludeAccountId)
        {
            return _accountRegions.Any(kvp => kvp.Key != excludeAccountId && 
                kvp.Value.RegionName.Equals(regionName, StringComparison.OrdinalIgnoreCase));
        }
        
        public async Task RecordVisitorAsync(string avatarId, string regionName, ulong simHandle, 
            string? avatarName = null, string? displayName = null)
        {
            try
            {
                // Check cooldown to prevent too frequent recordings
                var cacheKey = $"{avatarId}:{regionName}:{DateTime.UtcNow:yyyy-MM-dd:HH}"; // Per avatar per region per hour
                if (_recentRecordings.TryGetValue(cacheKey, out var lastRecording))
                {
                    if (DateTime.UtcNow - lastRecording < _recordingCooldown)
                    {
                        return; // Skip recording, too recent
                    }
                }
                
                var today = DateTime.UtcNow.Date;
                var now = DateTime.UtcNow;
                
                // Extract region coordinates from sim handle
                var regionX = (uint)(simHandle >> 32);
                var regionY = (uint)(simHandle & 0xFFFFFFFF);
                
                using var context = _dbContextFactory.CreateDbContext();
                
                // Try to find existing record for this avatar/region/date
                var existingRecord = await context.VisitorStats
                    .FirstOrDefaultAsync(vs => vs.AvatarId == avatarId && 
                        vs.RegionName == regionName && 
                        vs.VisitDate == today);
                
                if (existingRecord != null)
                {
                    // Update last seen time and names if provided
                    existingRecord.LastSeenAt = now;
                    if (!string.IsNullOrEmpty(avatarName))
                        existingRecord.AvatarName = avatarName;
                    if (!string.IsNullOrEmpty(displayName))
                        existingRecord.DisplayName = displayName;
                }
                else
                {
                    // Create new record
                    var visitorStats = new VisitorStats
                    {
                        AvatarId = avatarId,
                        RegionName = regionName,
                        SimHandle = simHandle,
                        VisitDate = today,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        AvatarName = avatarName,
                        DisplayName = displayName,
                        RegionX = regionX,
                        RegionY = regionY
                    };
                    
                    context.VisitorStats.Add(visitorStats);
                }
                
                await context.SaveChangesAsync();
                
                // Update cache
                _recentRecordings.AddOrUpdate(cacheKey, now, (key, oldValue) => now);
                
                _logger.LogDebug("Recorded visitor {AvatarId} in {RegionName}", avatarId, regionName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording visitor {AvatarId} in {RegionName}", avatarId, regionName);
            }
        }
        
        public async Task<VisitorStatsSummaryDto> GetRegionStatsAsync(string regionName, DateTime startDate, DateTime endDate)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                // Get all data and process in memory to avoid SQLite limitations
                var rawStats = await context.VisitorStats
                    .Where(vs => vs.RegionName == regionName && 
                        vs.VisitDate >= startDate.Date && 
                        vs.VisitDate <= endDate.Date)
                    .ToListAsync();
                
                var stats = rawStats
                    .GroupBy(vs => vs.VisitDate)
                    .Select(g => new DailyVisitorStatsDto
                    {
                        Date = g.Key,
                        RegionName = regionName,
                        UniqueVisitors = g.Select(vs => vs.AvatarId).Distinct().Count(),
                        TotalVisits = g.Count()
                    })
                    .OrderBy(d => d.Date)
                    .ToList();
                
                // Fill in missing dates with zero counts
                var allDates = Enumerable.Range(0, (int)(endDate.Date - startDate.Date).TotalDays + 1)
                    .Select(offset => startDate.Date.AddDays(offset))
                    .ToList();
                
                var completeStats = allDates.Select(date => 
                    stats.FirstOrDefault(s => s.Date == date) ?? 
                    new DailyVisitorStatsDto 
                    { 
                        Date = date, 
                        RegionName = regionName, 
                        UniqueVisitors = 0, 
                        TotalVisits = 0 
                    }).ToList();
                
                // Calculate totals in memory to avoid SQLite limitations
                var allRecords = await context.VisitorStats
                    .Where(vs => vs.RegionName == regionName && 
                        vs.VisitDate >= startDate.Date && 
                        vs.VisitDate <= endDate.Date)
                    .ToListAsync();
                
                var totalUniqueVisitors = allRecords.Select(vs => vs.AvatarId).Distinct().Count();
                var totalVisits = allRecords.Count;
                
                return new VisitorStatsSummaryDto
                {
                    RegionName = regionName,
                    DailyStats = completeStats,
                    TotalUniqueVisitors = totalUniqueVisitors,
                    TotalVisits = totalVisits,
                    StartDate = startDate,
                    EndDate = endDate
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stats for region {RegionName}", regionName);
                return new VisitorStatsSummaryDto { RegionName = regionName, StartDate = startDate, EndDate = endDate };
            }
        }
        
        public async Task<List<VisitorStatsSummaryDto>> GetAllRegionStatsAsync(DateTime startDate, DateTime endDate)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                var regions = await context.VisitorStats
                    .Where(vs => vs.VisitDate >= startDate.Date && vs.VisitDate <= endDate.Date)
                    .Select(vs => vs.RegionName)
                    .Distinct()
                    .ToListAsync();
                
                var results = new List<VisitorStatsSummaryDto>();
                foreach (var region in regions)
                {
                    var regionStats = await GetRegionStatsAsync(region, startDate, endDate);
                    results.Add(regionStats);
                }
                
                return results.OrderBy(r => r.RegionName).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all region stats");
                return new List<VisitorStatsSummaryDto>();
            }
        }
        
        public async Task<List<UniqueVisitorDto>> GetUniqueVisitorsAsync(DateTime startDate, DateTime endDate, string? regionName = null)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                var query = context.VisitorStats
                    .Where(vs => vs.VisitDate >= startDate.Date && vs.VisitDate <= endDate.Date);
                
                if (!string.IsNullOrEmpty(regionName))
                {
                    query = query.Where(vs => vs.RegionName == regionName);
                }
                
                // Get all visitor stats and process in memory to avoid SQLite limitations
                var allVisitorStats = await query.ToListAsync();
                
                // Group by avatar ID and process in memory
                var visitors = allVisitorStats
                    .GroupBy(vs => vs.AvatarId)
                    .Select(g =>
                    {
                        var latestRecord = g.OrderByDescending(vs => vs.LastSeenAt).First();
                        return new UniqueVisitorDto
                        {
                            AvatarId = g.Key,
                            AvatarName = latestRecord.AvatarName,
                            DisplayName = latestRecord.DisplayName,
                            FirstSeen = g.Min(vs => vs.FirstSeenAt),
                            LastSeen = g.Max(vs => vs.LastSeenAt),
                            VisitCount = g.Count(),
                            RegionsVisited = g.Select(vs => vs.RegionName).Distinct().ToList()
                        };
                    })
                    .OrderByDescending(v => v.LastSeen)
                    .ToList();
                
                return visitors;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting unique visitors for region {RegionName}", regionName);
                return new List<UniqueVisitorDto>();
            }
        }
        
        public async Task<List<string>> GetMonitoredRegionsAsync()
        {
            var regions = _accountRegions.Values.Select(v => v.RegionName).Distinct().ToList();
            return await Task.FromResult(regions);
        }
        
        public async Task CleanupOldRecordsAsync(int keepDays = 90)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.Date.AddDays(-keepDays);
                
                using var context = _dbContextFactory.CreateDbContext();
                
                var oldRecords = await context.VisitorStats
                    .Where(vs => vs.VisitDate < cutoffDate)
                    .ToListAsync();
                
                if (oldRecords.Any())
                {
                    context.VisitorStats.RemoveRange(oldRecords);
                    await context.SaveChangesAsync();
                    
                    _logger.LogInformation("Cleaned up {Count} old visitor records older than {Days} days", 
                        oldRecords.Count, keepDays);
                }
                
                // Also clean up the recent recordings cache
                var expiredKeys = _recentRecordings
                    .Where(kvp => DateTime.UtcNow - kvp.Value > TimeSpan.FromHours(2))
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var key in expiredKeys)
                {
                    _recentRecordings.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old visitor records");
            }
        }
    }
}