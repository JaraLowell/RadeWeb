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
        
        /// <summary>
        /// Trigger recording of all currently present avatars across all connected accounts
        /// This is useful for day boundary transitions
        /// </summary>
        Task TriggerBulkRecordingAsync();
    }
    
    /// <summary>
    /// Service to track and manage visitor statistics
    /// </summary>
    public class StatsService : IStatsService
    {
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        private readonly ILogger<StatsService> _logger;
        private readonly IGlobalDisplayNameCache _globalDisplayNameCache;
        
        // Thread-safe dictionary to track which accounts are monitoring which regions
        // This prevents duplicate recording when multiple accounts are in the same region
        private readonly ConcurrentDictionary<Guid, (string RegionName, ulong SimHandle)> _accountRegions = new();
        
        // Cache for recent visitor recordings to prevent too frequent database writes
        private readonly ConcurrentDictionary<string, DateTime> _recentRecordings = new();
        private readonly TimeSpan _recordingCooldown = TimeSpan.FromMinutes(5); // Don't record same avatar twice within 5 minutes
        
        public StatsService(IDbContextFactory<RadegastDbContext> dbContextFactory, ILogger<StatsService> logger, IGlobalDisplayNameCache globalDisplayNameCache)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
            _globalDisplayNameCache = globalDisplayNameCache;
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
                var cacheKey = $"{avatarId}:{regionName}:{DateTime.UtcNow:yyyy-MM-dd}"; // Per avatar per region per day
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
                    // Update last seen time and names if provided and better than existing
                    existingRecord.LastSeenAt = now;
                    
                    // Update avatar name if we have a better one (not null/empty and not generic)
                    if (!string.IsNullOrEmpty(avatarName) && 
                        avatarName != "Unknown User" && 
                        avatarName != "Loading..." &&
                        (string.IsNullOrEmpty(existingRecord.AvatarName) || 
                         existingRecord.AvatarName == "Unknown User" ||
                         existingRecord.AvatarName == "Loading..."))
                    {
                        existingRecord.AvatarName = avatarName;
                    }
                    
                    // Update display name if we have a better one
                    if (!string.IsNullOrEmpty(displayName) && 
                        displayName != "Loading..." &&
                        displayName != "???" &&
                        (string.IsNullOrEmpty(existingRecord.DisplayName) || 
                         existingRecord.DisplayName == "Loading..." ||
                         existingRecord.DisplayName == "???"))
                    {
                        existingRecord.DisplayName = displayName;
                    }
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
                
                // Get all data for the requested period
                var rawStats = await context.VisitorStats
                    .Where(vs => vs.RegionName == regionName && 
                        vs.VisitDate >= startDate.Date && 
                        vs.VisitDate <= endDate.Date)
                    .ToListAsync();
                
                // Get historical data for the 60 days before the start date to determine true unique visitors
                var historicalCutoff = startDate.Date.AddDays(-60);
                var historicalVisitors = await context.VisitorStats
                    .Where(vs => vs.RegionName == regionName && 
                        vs.VisitDate >= historicalCutoff && 
                        vs.VisitDate < startDate.Date)
                    .Select(vs => vs.AvatarId)
                    .Distinct()
                    .ToListAsync();
                
                var historicalVisitorSet = new HashSet<string>(historicalVisitors);
                
                var stats = rawStats
                    .GroupBy(vs => vs.VisitDate)
                    .Select(g => new DailyVisitorStatsDto
                    {
                        Date = g.Key,
                        RegionName = regionName,
                        UniqueVisitors = g.Select(vs => vs.AvatarId).Distinct().Count(),
                        TrueUniqueVisitors = g.Select(vs => vs.AvatarId).Distinct()
                            .Count(avatarId => !historicalVisitorSet.Contains(avatarId)),
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
                        TrueUniqueVisitors = 0,
                        TotalVisits = 0 
                    }).ToList();
                
                // Calculate totals in memory to avoid SQLite limitations
                var totalUniqueVisitors = rawStats.Select(vs => vs.AvatarId).Distinct().Count();
                var totalTrueUniqueVisitors = rawStats.Select(vs => vs.AvatarId).Distinct()
                    .Count(avatarId => !historicalVisitorSet.Contains(avatarId));
                var totalVisits = rawStats.Count;
                
                return new VisitorStatsSummaryDto
                {
                    RegionName = regionName,
                    DailyStats = completeStats,
                    TotalUniqueVisitors = totalUniqueVisitors,
                    TrueUniqueVisitors = totalTrueUniqueVisitors,
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
                
                // Get historical data for the 60 days before the start date to determine true unique visitors
                var historicalCutoff = startDate.Date.AddDays(-60);
                var historicalVisitors = await context.VisitorStats
                    .Where(vs => (string.IsNullOrEmpty(regionName) || vs.RegionName == regionName) && 
                        vs.VisitDate >= historicalCutoff && 
                        vs.VisitDate < startDate.Date)
                    .Select(vs => vs.AvatarId)
                    .Distinct()
                    .ToListAsync();
                
                var historicalVisitorSet = new HashSet<string>(historicalVisitors);
                
                // Get all visitor stats and process in memory to avoid SQLite limitations
                var allVisitorStats = await query.ToListAsync();
                
                // Group by avatar ID and process in memory
                var visitors = allVisitorStats
                    .GroupBy(vs => vs.AvatarId)
                    .Select(g =>
                    {
                        var latestRecord = g.OrderByDescending(vs => vs.LastSeenAt).First();
                        var isTrueUnique = !historicalVisitorSet.Contains(g.Key);
                        
                        return new UniqueVisitorDto
                        {
                            AvatarId = g.Key,
                            AvatarName = latestRecord.AvatarName,
                            DisplayName = latestRecord.DisplayName,
                            FirstSeen = g.Min(vs => vs.FirstSeenAt),
                            LastSeen = g.Max(vs => vs.LastSeenAt),
                            VisitCount = g.Count(),
                            RegionsVisited = g.Select(vs => vs.RegionName).Distinct().ToList(),
                            IsTrueUnique = isTrueUnique
                        };
                    })
                    .ToList();

                // Enhance names using the global display name cache
                await EnhanceVisitorNamesAsync(visitors);
                
                return visitors.OrderByDescending(v => v.LastSeen).ToList();
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
        
        /// <summary>
        /// Enhances visitor names using the global display name cache
        /// This replaces poor quality names (null, "Loading...", "Unknown User") with better cached names
        /// </summary>
        private async Task EnhanceVisitorNamesAsync(List<UniqueVisitorDto> visitors)
        {
            // First, try to preload any missing display names
            var avatarIds = visitors.Select(v => v.AvatarId).ToList();
            await _globalDisplayNameCache.PreloadDisplayNamesAsync(avatarIds);
            
            foreach (var visitor in visitors)
            {
                try
                {
                    // Check if we have cached names that are better than what's stored
                    var cachedDisplayName = await _globalDisplayNameCache.GetCachedDisplayNameAsync(visitor.AvatarId);
                    
                    if (cachedDisplayName != null)
                    {
                        // Use cached display name if it's better than what we have
                        if (IsNameBetter(cachedDisplayName.DisplayNameValue, visitor.DisplayName))
                        {
                            visitor.DisplayName = cachedDisplayName.DisplayNameValue;
                            _logger.LogDebug("Enhanced display name for {AvatarId}: '{NewName}'", visitor.AvatarId, visitor.DisplayName);
                        }
                        
                        // Use cached legacy name if it's better than what we have
                        if (IsNameBetter(cachedDisplayName.LegacyFullName, visitor.AvatarName))
                        {
                            visitor.AvatarName = cachedDisplayName.LegacyFullName;
                            _logger.LogDebug("Enhanced avatar name for {AvatarId}: '{NewName}'", visitor.AvatarId, visitor.AvatarName);
                        }
                    }
                    else
                    {
                        // Try to get a smart display name (fallback to legacy if needed)
                        var smartDisplayName = await _globalDisplayNameCache.GetDisplayNameAsync(visitor.AvatarId, Models.NameDisplayMode.Smart);
                        if (IsNameBetter(smartDisplayName, visitor.DisplayName) && smartDisplayName != "Loading...")
                        {
                            // Split smart display name if it contains both display and legacy name
                            if (smartDisplayName.Contains('(') && smartDisplayName.Contains(')'))
                            {
                                // Format: "DisplayName (username)" - extract display name
                                var displayPart = smartDisplayName.Substring(0, smartDisplayName.IndexOf('(')).Trim();
                                if (IsNameBetter(displayPart, visitor.DisplayName))
                                {
                                    visitor.DisplayName = displayPart;
                                    _logger.LogDebug("Enhanced display name from smart mode for {AvatarId}: '{NewName}'", visitor.AvatarId, visitor.DisplayName);
                                }
                            }
                            else if (IsNameBetter(smartDisplayName, visitor.DisplayName))
                            {
                                visitor.DisplayName = smartDisplayName;
                                _logger.LogDebug("Enhanced display name from smart mode for {AvatarId}: '{NewName}'", visitor.AvatarId, visitor.DisplayName);
                            }
                        }
                        
                        // Try to get legacy name if we still don't have a good avatar name
                        if (IsPlaceholderName(visitor.AvatarName))
                        {
                            var legacyName = await _globalDisplayNameCache.GetLegacyNameAsync(visitor.AvatarId);
                            if (IsNameBetter(legacyName, visitor.AvatarName) && legacyName != "Loading...")
                            {
                                visitor.AvatarName = legacyName;
                                _logger.LogDebug("Enhanced avatar name from legacy for {AvatarId}: '{NewName}'", visitor.AvatarId, visitor.AvatarName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not enhance name for visitor {AvatarId}", visitor.AvatarId);
                    // Continue with existing names if enhancement fails
                }
            }
        }
        
        /// <summary>
        /// Determines if a new name is better than an existing name
        /// Better means: not null/empty, not a placeholder like "Loading..." or "Unknown User"
        /// </summary>
        private static bool IsNameBetter(string? newName, string? existingName)
        {
            // If new name is invalid, it's not better
            if (IsInvalidName(newName))
                return false;
                
            // If existing name is invalid, new name is better
            if (IsInvalidName(existingName))
                return true;
                
            // Both are valid, prefer the new one only if existing is also a placeholder
            return IsPlaceholderName(existingName);
        }
        
        /// <summary>
        /// Checks if a name is invalid (null, empty, or whitespace)
        /// </summary>
        private static bool IsInvalidName(string? name)
        {
            return string.IsNullOrWhiteSpace(name);
        }
        
        /// <summary>
        /// Checks if a name is a placeholder that should be replaced
        /// </summary>
        private static bool IsPlaceholderName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;
                
            var lowerName = name.ToLowerInvariant();
            return lowerName == "loading..." || 
                   lowerName == "unknown user" || 
                   lowerName == "???" ||
                   lowerName.StartsWith("loading") ||
                   lowerName.StartsWith("unknown");
        }
        
        public async Task TriggerBulkRecordingAsync()
        {
            try
            {
                // This method will be called by background service to signal connected accounts
                // to record all present avatars. The actual recording will be done by WebRadegastInstance
                // when it receives the signal through a hub or other mechanism.
                
                // For now, we just log that bulk recording was triggered
                // The actual implementation will depend on how we want to signal all connected accounts
                _logger.LogInformation("Bulk recording triggered for day transition at {Time}", DateTime.UtcNow);
                
                // In a future implementation, this could send a SignalR message to all connected accounts
                // or use another mechanism to trigger RecordAllPresentAvatarsAsync() on each account
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering bulk recording");
            }
        }
    }
}