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
        /// Record multiple visitors in a single batch operation for improved performance
        /// </summary>
        Task RecordVisitorBatchAsync(IEnumerable<(string AvatarId, string RegionName, ulong SimHandle, string? AvatarName, string? DisplayName)> visitors);
        
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
        /// Check if any other account is already monitoring this region (for informational purposes)
        /// Note: This should not be used to skip visitor recording as multiple accounts in the same
        /// region should all contribute to visitor statistics for comprehensive coverage.
        /// </summary>
        bool IsRegionAlreadyMonitored(string regionName, Guid excludeAccountId);
        
        /// <summary>
        /// Trigger recording of all currently present avatars across all connected accounts
        /// This is useful for day boundary transitions
        /// </summary>
        Task TriggerBulkRecordingAsync();
        
        /// <summary>
        /// Get detailed visitor classification for a specific date range
        /// </summary>
        Task<VisitorClassificationDto> GetVisitorClassificationAsync(string regionName, DateTime startDate, DateTime endDate);
        
        /// <summary>
        /// Get hourly visitor activity for the past 24 hours (or specified days) in SLT time
        /// </summary>
        Task<HourlyActivitySummaryDto> GetHourlyActivityAsync(DateTime startDate, DateTime endDate, string? regionName = null);
    }
    
    /// <summary>
    /// Service to track and manage visitor statistics
    /// </summary>
    public class StatsService : IStatsService, IDisposable
    {
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        private readonly ILogger<StatsService> _logger;
        private readonly IGlobalDisplayNameCache _globalDisplayNameCache;
        private readonly ISLTimeService _sltTimeService;
        private readonly IStatsNameCache _statsNameCache;
        
        // Thread-safe dictionary to track which accounts are monitoring which regions
        // This prevents duplicate recording when multiple accounts are in the same region
        private readonly ConcurrentDictionary<Guid, (string RegionName, ulong SimHandle)> _accountRegions = new();
        
        // Cache for recent visitor recordings to prevent too frequent database writes
        private readonly ConcurrentDictionary<string, DateTime> _recentRecordings = new();
        private readonly TimeSpan _recordingCooldown = TimeSpan.FromSeconds(30); // Don't record same avatar twice within 30 seconds (much more responsive)
        private readonly Timer _recordingCleanupTimer;
        
        public StatsService(IDbContextFactory<RadegastDbContext> dbContextFactory, ILogger<StatsService> logger, IGlobalDisplayNameCache globalDisplayNameCache, ISLTimeService sltTimeService, IStatsNameCache statsNameCache)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
            _globalDisplayNameCache = globalDisplayNameCache;
            _sltTimeService = sltTimeService;
            _statsNameCache = statsNameCache;
            
            // Initialize cleanup timer to run every 5 minutes to prevent unbounded growth
            _recordingCleanupTimer = new Timer(CleanupRecentRecordings, null, (int)TimeSpan.FromMinutes(5).TotalMilliseconds, (int)TimeSpan.FromMinutes(5).TotalMilliseconds);
        }

        /// <summary>
        /// Configure database context for memory-efficient bulk operations
        /// </summary>
        private static void ConfigureContextForBulkOperations(RadegastDbContext context)
        {
            // Disable change tracking for better performance during bulk operations
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
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
            // NOTE: This method is for informational purposes only and should NOT be used to skip
            // visitor recording. Multiple accounts in the same region should all record visitor stats
            // to ensure comprehensive coverage. The deduplication is handled at the database level.
            return _accountRegions.Any(kvp => kvp.Key != excludeAccountId && 
                kvp.Value.RegionName.Equals(regionName, StringComparison.OrdinalIgnoreCase));
        }
        
        public async Task RecordVisitorAsync(string avatarId, string regionName, ulong simHandle, 
            string? avatarName = null, string? displayName = null)
        {
            try
            {
                // Use SLT date for consistent date grouping (Pacific Time date, not UTC date)
                var sltNow = _sltTimeService.GetCurrentSLT();
                var today = sltNow.Date;
                var now = DateTime.UtcNow; // Keep UTC for storage, but we'll use SLT for display
                
                // Check cooldown to prevent too frequent recordings, but still update LastSeenAt for existing records
                var cacheKey = $"{avatarId}:{regionName}:{sltNow:yyyy-MM-dd}"; // Per avatar per region per SLT day
                bool isWithinCooldown = false;
                if (_recentRecordings.TryGetValue(cacheKey, out var lastRecording))
                {
                    if (DateTime.UtcNow - lastRecording < _recordingCooldown)
                    {
                        isWithinCooldown = true;
                        _logger.LogDebug("Within cooldown period for {AvatarId} in {RegionName} - will only update LastSeenAt (last: {LastRecording})", 
                            avatarId, regionName, lastRecording.ToString("HH:mm:ss"));
                    }
                }
                
                // Extract region coordinates from sim handle
                var regionX = (uint)(simHandle >> 32);
                var regionY = (uint)(simHandle & 0xFFFFFFFF);
                
                // Retry mechanism for handling race conditions during bulk recording
                const int maxRetries = 3;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        using var context = _dbContextFactory.CreateDbContext();
                        ConfigureContextForBulkOperations(context);
                        
                        // Try to find existing record for this avatar/region/date
                        var existingRecord = await context.VisitorStats
                            .AsTracking() // Only track this specific record
                            .FirstOrDefaultAsync(vs => vs.AvatarId == avatarId && 
                                vs.RegionName == regionName && 
                                vs.VisitDate == today);
                        
                        if (existingRecord != null)
                        {
                            // Always update last seen time to keep it current
                            existingRecord.LastSeenAt = now;
                            
                            // During cooldown period, only update LastSeenAt and skip name updates and cache management
                            if (isWithinCooldown)
                            {
                                await context.SaveChangesAsync();
                                _logger.LogDebug("Updated LastSeenAt for {AvatarId} ({AvatarName}) in {RegionName} during cooldown", 
                                    avatarId, avatarName ?? "Unknown", regionName);
                                
                                // MEMORY FIX: Still update stats cache during cooldown if we have names
                                if (!string.IsNullOrEmpty(avatarName) || !string.IsNullOrEmpty(displayName))
                                {
                                    await _statsNameCache.StoreNameAsync(avatarId, displayName, avatarName);
                                }
                                
                                return; // Exit early during cooldown - we've updated LastSeenAt which was the main goal
                            }
                            
                            // Outside cooldown period - also update names if we have better ones
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
                            // During cooldown, don't create new records (this shouldn't happen for existing avatars but safety check)
                            if (isWithinCooldown)
                            {
                                _logger.LogWarning("Attempted to create new visitor record during cooldown for {AvatarId} in {RegionName} - skipping", 
                                    avatarId, regionName);
                                return;
                            }
                            
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
                        
                        // MEMORY FIX: Store names in persistent stats cache for historical display
                        // This prevents the need to resolve thousands of names on stats page load
                        if (!string.IsNullOrEmpty(avatarName) || !string.IsNullOrEmpty(displayName))
                        {
                            await _statsNameCache.StoreNameAsync(avatarId, displayName, avatarName);
                        }
                        
                        // Clear change tracker to free memory immediately
                        context.ChangeTracker.Clear();
                        
                        // Success - break out of retry loop
                        break;
                    }
                    catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message?.Contains("UNIQUE constraint failed: VisitorStats.AvatarId, VisitorStats.RegionName, VisitorStats.VisitDate") == true)
                    {
                        // Handle race condition where another thread created the record between our check and insert
                        _logger.LogDebug("Unique constraint violation for visitor {AvatarId} in {RegionName} on {Date} - attempt {Attempt}/{MaxAttempts}", 
                            avatarId, regionName, today, attempt + 1, maxRetries);
                        
                        if (attempt == maxRetries - 1)
                        {
                            // Final attempt - try to update the existing record that another thread created
                            _logger.LogWarning("Final attempt to handle unique constraint violation for visitor {AvatarId} in {RegionName} on {Date}", 
                                avatarId, regionName, today);
                            
                            using var contextRetry = _dbContextFactory.CreateDbContext();
                            ConfigureContextForBulkOperations(contextRetry);
                            var existingRecord = await contextRetry.VisitorStats
                                .AsTracking() // Only track this specific record
                                .FirstOrDefaultAsync(vs => vs.AvatarId == avatarId && 
                                    vs.RegionName == regionName && 
                                    vs.VisitDate == today);
                            
                            if (existingRecord != null)
                            {
                                // Update the existing record
                                existingRecord.LastSeenAt = now;
                                
                                // Update names if we have better ones
                                if (!string.IsNullOrEmpty(avatarName) && 
                                    avatarName != "Unknown User" && 
                                    avatarName != "Loading..." &&
                                    (string.IsNullOrEmpty(existingRecord.AvatarName) || 
                                     existingRecord.AvatarName == "Unknown User" ||
                                     existingRecord.AvatarName == "Loading..."))
                                {
                                    existingRecord.AvatarName = avatarName;
                                }
                                
                                if (!string.IsNullOrEmpty(displayName) && 
                                    displayName != "Loading..." &&
                                    displayName != "???" &&
                                    (string.IsNullOrEmpty(existingRecord.DisplayName) || 
                                     existingRecord.DisplayName == "Loading..." ||
                                     existingRecord.DisplayName == "???"))
                                {
                                    existingRecord.DisplayName = displayName;
                                }
                                
                                await contextRetry.SaveChangesAsync();
                                contextRetry.ChangeTracker.Clear();
                                
                                // MEMORY FIX: Store names in persistent stats cache
                                if (!string.IsNullOrEmpty(avatarName) || !string.IsNullOrEmpty(displayName))
                                {
                                    await _statsNameCache.StoreNameAsync(avatarId, displayName, avatarName);
                                }
                                
                                _logger.LogDebug("Successfully updated existing record for visitor {AvatarId} in {RegionName} after constraint violation", 
                                    avatarId, regionName);
                            }
                            else
                            {
                                _logger.LogWarning("Could not find existing record to update for visitor {AvatarId} in {RegionName} after constraint violation", 
                                    avatarId, regionName);
                            }
                        }
                        else
                        {
                            // Wait a small random time before retrying to reduce collision probability
                            await Task.Delay(new Random().Next(10, 50));
                        }
                    }
                }
                
                // Update cache and clean up old entries periodically (only outside cooldown period)
                if (!isWithinCooldown)
                {
                    _recentRecordings.AddOrUpdate(cacheKey, now, (key, oldValue) => now);
                }
                else
                {
                    _logger.LogDebug("Updated LastSeenAt for existing visitor {AvatarId} ({AvatarName}) in {RegionName} at {SLTTime}", 
                        avatarId, avatarName ?? "Unknown", regionName, sltNow.ToString("yyyy-MM-dd HH:mm:ss"));
                }
            }
            catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
            {
                // This should not happen due to our retry logic above, but log it if it does
                _logger.LogError(dbEx, "Unexpected unique constraint violation after retry logic for visitor {AvatarId} in {RegionName}", avatarId, regionName);
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
                
                // Since we now store dates in SLT format, convert input UTC dates to SLT for querying
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var sltStartDate = TimeZoneInfo.ConvertTimeFromUtc(startDate, sltTimeZone).Date;
                var sltEndDate = TimeZoneInfo.ConvertTimeFromUtc(endDate, sltTimeZone).Date;
                
                _logger.LogInformation("GetRegionStatsAsync: Region='{RegionName}', UTC range {StartDateUtc} to {EndDateUtc}, SLT range {StartDateSlt} to {EndDateSlt}",
                    regionName, startDate, endDate, sltStartDate, sltEndDate);
                
                // Query using SLT dates (since we store data in SLT format now)
                // Use case-insensitive comparison for region name
                var rawStats = await context.VisitorStats
                    .Where(vs => vs.RegionName.ToLower() == regionName.ToLower() && 
                        vs.VisitDate >= sltStartDate && 
                        vs.VisitDate <= sltEndDate)
                    .ToListAsync();
                
                _logger.LogInformation("GetRegionStatsAsync: Found {RecordCount} records for region '{RegionName}'", rawStats.Count, regionName);
                
                // Get ALL historical data before the start date to determine truly new vs returning visitors
                var allHistoricalVisitors = await context.VisitorStats
                    .Where(vs => vs.RegionName == regionName && 
                        vs.VisitDate < sltStartDate)
                    .Select(vs => vs.AvatarId)
                    .Distinct()
                    .ToListAsync();
                
                var historicalVisitorSet = new HashSet<string>(allHistoricalVisitors);
                
                // Track which visitors we've already seen in the current period to avoid double-counting
                var seenInPeriod = new HashSet<string>();
                
                // Process the stats grouped by date
                var stats = rawStats
                    .GroupBy(vs => vs.VisitDate.Date) // Group by the actual date in database (already SLT)
                    .OrderBy(g => g.Key) // Important: process dates in order
                    .Select(g => 
                    {
                        var dailyVisitors = g.Select(vs => vs.AvatarId).Distinct().ToList();
                        var newVisitorsToday = dailyVisitors.Where(id => !seenInPeriod.Contains(id)).ToList();
                        var trueUniqueToday = newVisitorsToday.Count(id => !historicalVisitorSet.Contains(id));
                        
                        // Add today's visitors to our running total
                        foreach (var visitorId in dailyVisitors)
                        {
                            seenInPeriod.Add(visitorId);
                        }
                        
                        return new DailyVisitorStatsDto
                        {
                            Date = g.Key, // Use the actual database date (already SLT)
                            RegionName = regionName,
                            UniqueVisitors = dailyVisitors.Count, // Total unique visitors this day
                            TrueUniqueVisitors = trueUniqueToday, // New visitors never seen before
                            TotalVisits = g.Count(), // Total visit records (could be multiple per avatar if they teleported in/out)
                            SLTDate = _sltTimeService.FormatSLTWithDate(g.Key, "MMM dd, yyyy") // Format SLT date for display
                        };
                    })
                    .ToList();
                
                // Fill in missing dates with zero counts using the SLT date range
                // But don't include future dates - only go up to today's SLT date
                var todaySLT = _sltTimeService.GetCurrentSLT().Date;
                var actualEndDate = sltEndDate > todaySLT ? todaySLT : sltEndDate;
                var allDates = Enumerable.Range(0, (int)(actualEndDate - sltStartDate).TotalDays + 1)
                    .Select(offset => sltStartDate.AddDays(offset))
                    .ToList();
                
                var completeStats = allDates.Select(date => 
                    stats.FirstOrDefault(s => s.Date == date) ?? 
                    new DailyVisitorStatsDto 
                    { 
                        Date = date, 
                        RegionName = regionName, 
                        UniqueVisitors = 0,
                        TrueUniqueVisitors = 0,
                        TotalVisits = 0,
                        SLTDate = _sltTimeService.FormatSLTWithDate(date, "MMM dd, yyyy")
                    }).ToList();
                
                // Calculate totals in memory to avoid SQLite limitations using raw data
                var allVisitorsInPeriod = rawStats.Select(vs => vs.AvatarId).Distinct().ToList();
                var totalUniqueVisitors = allVisitorsInPeriod.Count;
                var totalTrueUniqueVisitors = allVisitorsInPeriod.Count(avatarId => !historicalVisitorSet.Contains(avatarId));
                var totalVisits = rawStats.Count;
                
                return new VisitorStatsSummaryDto
                {
                    RegionName = regionName,
                    DailyStats = completeStats,
                    TotalUniqueVisitors = totalUniqueVisitors,
                    TrueUniqueVisitors = totalTrueUniqueVisitors,
                    TotalVisits = totalVisits,
                    StartDate = sltStartDate, // Use SLT dates for consistency
                    EndDate = sltEndDate,
                    SLTStartDate = _sltTimeService.FormatSLTWithDate(sltStartDate, "MMM dd, yyyy"),
                    SLTEndDate = _sltTimeService.FormatSLTWithDate(sltEndDate, "MMM dd, yyyy")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stats for region {RegionName}", regionName);
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var sltStartDate = TimeZoneInfo.ConvertTimeFromUtc(startDate, sltTimeZone).Date;
                var sltEndDate = TimeZoneInfo.ConvertTimeFromUtc(endDate, sltTimeZone).Date;
                return new VisitorStatsSummaryDto { 
                    RegionName = regionName, 
                    StartDate = sltStartDate, 
                    EndDate = sltEndDate,
                    SLTStartDate = _sltTimeService.FormatSLTWithDate(sltStartDate, "MMM dd, yyyy"),
                    SLTEndDate = _sltTimeService.FormatSLTWithDate(sltEndDate, "MMM dd, yyyy")
                };
            }
        }
        
        public async Task<List<VisitorStatsSummaryDto>> GetAllRegionStatsAsync(DateTime startDate, DateTime endDate)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                // Configure context for memory efficiency
                ConfigureContextForBulkOperations(context);
                
                // Convert UTC input dates to SLT for database querying
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var sltStartDate = TimeZoneInfo.ConvertTimeFromUtc(startDate, sltTimeZone).Date;
                var sltEndDate = TimeZoneInfo.ConvertTimeFromUtc(endDate, sltTimeZone).Date;
                
                _logger.LogInformation("GetAllRegionStatsAsync: UTC range {StartDateUtc} to {EndDateUtc}, SLT range {StartDateSlt} to {EndDateSlt}",
                    startDate, endDate, sltStartDate, sltEndDate);
                
                // Check if there's any data in the date range
                var totalRecords = await context.VisitorStats
                    .Where(vs => vs.VisitDate >= sltStartDate && vs.VisitDate <= sltEndDate)
                    .CountAsync();
                
                _logger.LogInformation("GetAllRegionStatsAsync: Found {TotalRecords} visitor records in date range", totalRecords);
                
                var regions = await context.VisitorStats
                    .Where(vs => vs.VisitDate >= sltStartDate && vs.VisitDate <= sltEndDate)
                    .Select(vs => vs.RegionName)
                    .Distinct()
                    .Take(50) // Limit to prevent excessive memory usage
                    .ToListAsync();
                
                _logger.LogInformation("GetAllRegionStatsAsync: Found {RegionCount} distinct regions", regions.Count);
                
                var results = new List<VisitorStatsSummaryDto>();
                foreach (var region in regions)
                {
                    var regionStats = await GetRegionStatsAsync(region, startDate, endDate);
                    results.Add(regionStats);
                    
                    // Force garbage collection of intermediate objects more aggressively
                    if (results.Count % 5 == 0) // Every 5 regions instead of 10
                    {
                        GC.Collect(1, GCCollectionMode.Optimized);
                    }
                }
                
                // Clear context to release memory immediately
                context.ChangeTracker.Clear();
                
                // Add memory pressure hint for large result set
                if (results.Count > 10)
                {
                    GC.AddMemoryPressure(results.Count * 100000); // ~100KB per region estimate
                }
                
                var orderedResults = results.OrderBy(r => r.RegionName).ToList();
                
                // Remove memory pressure after building final result
                if (results.Count > 10)
                {
                    GC.RemoveMemoryPressure(results.Count * 100000);
                }
                
                return orderedResults;
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
                
                // Configure context for memory efficiency
                ConfigureContextForBulkOperations(context);
                
                // Convert UTC input dates to SLT for database querying
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var sltStartDate = TimeZoneInfo.ConvertTimeFromUtc(startDate, sltTimeZone).Date;
                var sltEndDate = TimeZoneInfo.ConvertTimeFromUtc(endDate, sltTimeZone).Date;
                
                _logger.LogInformation("GetUniqueVisitorsAsync: UTC range {StartDateUtc} to {EndDateUtc}, SLT range {StartDateSlt} to {EndDateSlt}, Region: {RegionName}",
                    startDate, endDate, sltStartDate, sltEndDate, regionName ?? "All");
                
                var query = context.VisitorStats
                    .Where(vs => vs.VisitDate >= sltStartDate && vs.VisitDate <= sltEndDate);
                
                if (!string.IsNullOrEmpty(regionName))
                {
                    query = query.Where(vs => vs.RegionName.ToLower() == regionName.ToLower());
                }
                
                // Check how many records match the query
                var matchCount = await query.CountAsync();
                _logger.LogInformation("GetUniqueVisitorsAsync: Found {MatchCount} records matching query for region '{RegionName}'", matchCount, regionName ?? "All");
                
                // MEMORY FIX: Significantly reduce query limits to prevent memory bloat
                // For "true unique" determination, we look back 60 days from the start date but limit results
                var trueUniqueThresholdDate = sltStartDate.AddDays(-60);
                var historicalVisitors = await context.VisitorStats
                    .Where(vs => (string.IsNullOrEmpty(regionName) || vs.RegionName.ToLower() == regionName.ToLower()) && 
                        vs.VisitDate < trueUniqueThresholdDate)
                    .Select(vs => vs.AvatarId)
                    .Distinct()
                    .Take(5000) // Reduced from 10000 to 3000
                    .ToListAsync();
                
                var historicalVisitorSet = new HashSet<string>(historicalVisitors);
                
                // MEMORY FIX: Aggressively limit current period data
                var allVisitorStats = await query
                    .OrderByDescending(vs => vs.LastSeenAt)
                    .Take(2500) // Reduced from 5000 to 2000
                    .ToListAsync();
                
                // Clear historical visitors list immediately after creating set
                historicalVisitors.Clear();
                historicalVisitors = null;
                
                // MEMORY FIX: Limit historical data for visitor classification
                var historicalData = await context.VisitorStats
                    .Where(vs => (string.IsNullOrEmpty(regionName) || vs.RegionName.ToLower() == regionName.ToLower()) && 
                        vs.VisitDate < sltStartDate && vs.VisitDate >= sltStartDate.AddDays(-90)) // Only look back 90 days max
                    .OrderByDescending(vs => vs.VisitDate)
                    .Take(2500) // Reduced from 5000 to 2000
                    .ToListAsync();
                
                // MEMORY FIX: Use more efficient dictionary creation and limit size
                var lastVisitDates = historicalData
                    .GroupBy(vs => vs.AvatarId)
                    .Take(1000) // Reduced from 2000 to 1000
                    .ToDictionary(g => g.Key, g => g.Max(vs => vs.VisitDate));
                
                // Clear historical data immediately after dictionary creation
                historicalData.Clear();
                historicalData = null;
                
                // MEMORY FIX: Process visitors in batches to reduce memory pressure
                var visitors = new List<UniqueVisitorDto>();
                var visitorGroups = allVisitorStats.GroupBy(vs => vs.AvatarId).Take(500); // Reduced from 1000 to 500
                
                foreach (var g in visitorGroups)
                {
                    var latestRecord = g.OrderByDescending(vs => vs.LastSeenAt).First();
                    var isTrueUnique = !historicalVisitorSet.Contains(g.Key);
                    
                    // Determine visitor type
                    VisitorType visitorType;
                    if (isTrueUnique)
                    {
                        visitorType = VisitorType.Brand_New;
                    }
                    else if (lastVisitDates.ContainsKey(g.Key))
                    {
                        var daysSinceLastVisit = (sltStartDate - lastVisitDates[g.Key]).TotalDays;
                        visitorType = daysSinceLastVisit > 30 ? VisitorType.Returning : VisitorType.Regular;
                    }
                    else
                    {
                        visitorType = VisitorType.Returning; // Default for edge cases
                    }
                    
                    visitors.Add(new UniqueVisitorDto
                    {
                        AvatarId = g.Key,
                        AvatarName = latestRecord.AvatarName,
                        DisplayName = latestRecord.DisplayName,
                        FirstSeen = g.Min(vs => vs.FirstSeenAt),
                        LastSeen = g.Max(vs => vs.LastSeenAt),
                        VisitCount = g.Count(),
                        RegionsVisited = g.Select(vs => vs.RegionName).Distinct().Take(10).ToList(), // Limit regions per visitor
                        IsTrueUnique = isTrueUnique,
                        VisitorType = visitorType,
                        SLTFirstSeen = _sltTimeService.FormatSLTWithDate(g.Min(vs => vs.FirstSeenAt), "MMM dd, yyyy HH:mm"),
                        SLTLastSeen = _sltTimeService.FormatSLTWithDate(g.Max(vs => vs.LastSeenAt), "MMM dd, yyyy HH:mm")
                    });
                }

                // Clear context to release memory immediately
                context.ChangeTracker.Clear();
                
                // Clear large temporary collections to help garbage collection
                historicalVisitorSet.Clear();
                lastVisitDates.Clear();
                allVisitorStats.Clear();
                allVisitorStats = null;

                // MEMORY FIX: Use persistent stats names instead of mass enhancement
                // Get names from stats cache for all visitors efficiently
                var avatarIds = visitors.Select(v => v.AvatarId).ToList();
                var statsNames = await _statsNameCache.GetBestNamesAsync(avatarIds);
                
                // Apply stats names to visitors
                foreach (var visitor in visitors)
                {
                    if (statsNames.TryGetValue(visitor.AvatarId, out var bestName))
                    {
                        // Update display name from stats cache if we don't have a good one
                        if (string.IsNullOrEmpty(visitor.DisplayName) || 
                            visitor.DisplayName == "Loading..." || 
                            visitor.DisplayName == "???")
                        {
                            visitor.DisplayName = bestName;
                        }
                    }
                }
                
                // Clear temporary collections
                avatarIds.Clear();
                statsNames.Clear();
                
                // Force GC hint that we're done with large allocations
                GC.Collect(1, GCCollectionMode.Optimized);
                
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
                
                // MEMORY FIX: Also cleanup old stats display names (60 days retention)
                // This prevents the stats names table from growing indefinitely
                await _statsNameCache.CleanupOldNamesAsync(60);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old visitor records");
            }
        }

        /// <summary>
        /// Timer callback to periodically clean up the recent recordings cache to prevent memory leaks
        /// </summary>
        private void CleanupRecentRecordings(object? state)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddMinutes(-30); // Clean entries older than 30 minutes (our cooldown is 30 seconds)
                var keysToRemove = _recentRecordings
                    .Where(kvp => kvp.Value < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _recentRecordings.TryRemove(key, out _);
                }

                // MEMORY FIX: Aggressive size limitation to prevent unbounded growth
                const int maxCacheSize = 50000; // Reasonable limit for busy regions
                if (_recentRecordings.Count > maxCacheSize)
                {
                    var entriesToRemove = _recentRecordings.Count - maxCacheSize;
                    var oldestEntries = _recentRecordings
                        .OrderBy(kvp => kvp.Value)
                        .Take(entriesToRemove)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    
                    foreach (var key in oldestEntries)
                    {
                        _recentRecordings.TryRemove(key, out _);
                    }
                    
                    _logger.LogInformation("Trimmed {Count} oldest recording cache entries to maintain size limit ({MaxSize})", 
                        entriesToRemove, maxCacheSize);
                }

                if (keysToRemove.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} expired recent recording entries. Total remaining: {Total}", 
                        keysToRemove.Count, _recentRecordings.Count);
                }

                // Log warning if cache is still growing unusually large
                if (_recentRecordings.Count > 30000)
                {
                    _logger.LogWarning("Recent recordings cache has {Count} entries - this may indicate heavy activity or potential memory leak", 
                        _recentRecordings.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled cleanup of recent recordings cache");
            }
        }

        /// <summary>
        /// Records multiple visitors in a single batch operation for improved performance during bulk operations
        /// </summary>
        public async Task RecordVisitorBatchAsync(IEnumerable<(string AvatarId, string RegionName, ulong SimHandle, string? AvatarName, string? DisplayName)> visitors)
        {
            if (visitors == null || !visitors.Any())
                return;

            var visitorList = visitors.ToList();
            var sltNow = _sltTimeService.GetCurrentSLT();
            var today = sltNow.Date;
            var now = DateTime.UtcNow;

            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                // Group visitors by unique key to handle duplicates
                var groupedVisitors = visitorList
                    .GroupBy(v => $"{v.AvatarId}:{v.RegionName}:{today:yyyy-MM-dd}")
                    .Select(g => g.First()) // Take first of each group
                    .ToList();

                // Batch fetch existing records
                var avatarIds = groupedVisitors.Select(v => v.AvatarId).ToList();
                var regionNames = groupedVisitors.Select(v => v.RegionName).Distinct().ToList();
                
                var existingRecords = await context.VisitorStats
                    .Where(vs => avatarIds.Contains(vs.AvatarId) && 
                                regionNames.Contains(vs.RegionName) && 
                                vs.VisitDate == today)
                    .ToDictionaryAsync(vs => $"{vs.AvatarId}:{vs.RegionName}:{vs.VisitDate:yyyy-MM-dd}");

                var recordsToAdd = new List<VisitorStats>();
                var recordsToUpdate = new List<VisitorStats>();

                foreach (var visitor in groupedVisitors)
                {
                    var key = $"{visitor.AvatarId}:{visitor.RegionName}:{today:yyyy-MM-dd}";
                    var regionX = (uint)(visitor.SimHandle >> 32);
                    var regionY = (uint)(visitor.SimHandle & 0xFFFFFFFF);

                    if (existingRecords.TryGetValue(key, out var existing))
                    {
                        // Update existing record
                        existing.LastSeenAt = now;
                        
                        // Update names if we have better ones
                        if (!string.IsNullOrEmpty(visitor.AvatarName) && 
                            visitor.AvatarName != "Unknown User" && 
                            visitor.AvatarName != "Loading..." &&
                            (string.IsNullOrEmpty(existing.AvatarName) || 
                             existing.AvatarName == "Unknown User" ||
                             existing.AvatarName == "Loading..."))
                        {
                            existing.AvatarName = visitor.AvatarName;
                        }
                        
                        if (!string.IsNullOrEmpty(visitor.DisplayName) && 
                            visitor.DisplayName != "Loading..." &&
                            visitor.DisplayName != "???" &&
                            (string.IsNullOrEmpty(existing.DisplayName) || 
                             existing.DisplayName == "Loading..." ||
                             existing.DisplayName == "???"))
                        {
                            existing.DisplayName = visitor.DisplayName;
                        }
                        
                        recordsToUpdate.Add(existing);
                    }
                    else
                    {
                        // Add new record
                        var newRecord = new VisitorStats
                        {
                            AvatarId = visitor.AvatarId,
                            RegionName = visitor.RegionName,
                            SimHandle = visitor.SimHandle,
                            VisitDate = today,
                            FirstSeenAt = now,
                            LastSeenAt = now,
                            AvatarName = visitor.AvatarName,
                            DisplayName = visitor.DisplayName,
                            RegionX = regionX,
                            RegionY = regionY
                        };
                        
                        recordsToAdd.Add(newRecord);
                    }
                }

                if (recordsToAdd.Count > 0)
                {
                    context.VisitorStats.AddRange(recordsToAdd);
                }

                await context.SaveChangesAsync();
                
                _logger.LogDebug("Batch processed {TotalCount} visitors: {AddedCount} new, {UpdatedCount} updated", 
                    groupedVisitors.Count, recordsToAdd.Count, recordsToUpdate.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch visitor recording for {Count} visitors", visitorList.Count);
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
        
        public async Task<VisitorClassificationDto> GetVisitorClassificationAsync(string regionName, DateTime startDate, DateTime endDate)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                // Get all visitor data for the period
                var periodVisitors = await context.VisitorStats
                    .Where(vs => vs.RegionName == regionName && 
                        vs.VisitDate >= startDate.Date && 
                        vs.VisitDate <= endDate.Date)
                    .ToListAsync();
                
                // Get all historical data to classify visitors properly
                var allHistoricalData = await context.VisitorStats
                    .Where(vs => vs.RegionName == regionName && vs.VisitDate < startDate.Date)
                    .ToListAsync();
                
                // Create lookup for last visit dates
                var lastVisitDates = allHistoricalData
                    .GroupBy(vs => vs.AvatarId)
                    .ToDictionary(g => g.Key, g => g.Max(vs => vs.VisitDate));
                
                // Classify each unique visitor
                var uniqueVisitors = periodVisitors
                    .GroupBy(vs => vs.AvatarId)
                    .Select(g =>
                    {
                        var latestRecord = g.OrderByDescending(vs => vs.LastSeenAt).First();
                        var firstSeenInPeriod = g.Min(vs => vs.FirstSeenAt);
                        
                        // Determine visitor type
                        VisitorType visitorType;
                        bool isNewVisitor = !lastVisitDates.ContainsKey(g.Key);
                        
                        if (isNewVisitor)
                        {
                            visitorType = VisitorType.Brand_New;
                        }
                        else
                        {
                            var daysSinceLastVisit = (startDate.Date - lastVisitDates[g.Key]).TotalDays;
                            if (daysSinceLastVisit > 30)
                                visitorType = VisitorType.Returning;
                            else
                                visitorType = VisitorType.Regular;
                        }
                        
                        return new UniqueVisitorDto
                        {
                            AvatarId = g.Key,
                            AvatarName = latestRecord.AvatarName,
                            DisplayName = latestRecord.DisplayName,
                            FirstSeen = firstSeenInPeriod,
                            LastSeen = g.Max(vs => vs.LastSeenAt),
                            VisitCount = g.Count(),
                            RegionsVisited = new List<string> { regionName },
                            IsTrueUnique = isNewVisitor,
                            VisitorType = visitorType,
                            SLTFirstSeen = _sltTimeService.FormatSLTWithDate(firstSeenInPeriod, "MMM dd, yyyy HH:mm"),
                            SLTLastSeen = _sltTimeService.FormatSLTWithDate(g.Max(vs => vs.LastSeenAt), "MMM dd, yyyy HH:mm")
                        };
                    })
                    .ToList();
                
                // MEMORY FIX: Use persistent stats names instead of mass enhancement
                var visitorAvatarIds = uniqueVisitors.Select(v => v.AvatarId).ToList();
                var visitorStatsNames = await _statsNameCache.GetBestNamesAsync(visitorAvatarIds);
                
                // Apply stats names to visitors
                foreach (var visitor in uniqueVisitors)
                {
                    if (visitorStatsNames.TryGetValue(visitor.AvatarId, out var bestName))
                    {
                        // Update display name from stats cache if we don't have a good one
                        if (string.IsNullOrEmpty(visitor.DisplayName) || 
                            visitor.DisplayName == "Loading..." || 
                            visitor.DisplayName == "???")
                        {
                            visitor.DisplayName = bestName;
                        }
                    }
                }
                
                // Create daily breakdown
                var seenInPeriod = new HashSet<string>();
                var dailyBreakdown = periodVisitors
                    .GroupBy(vs => vs.VisitDate)
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        var dailyVisitors = g.Select(vs => vs.AvatarId).Distinct().ToList();
                        var newTodayVisitors = dailyVisitors.Where(id => !seenInPeriod.Contains(id)).ToList();
                        
                        var brandNew = 0;
                        var returning = 0;
                        var regular = 0;
                        
                        foreach (var visitorId in newTodayVisitors)
                        {
                            var visitor = uniqueVisitors.FirstOrDefault(v => v.AvatarId == visitorId);
                            if (visitor != null)
                            {
                                switch (visitor.VisitorType)
                                {
                                    case VisitorType.Brand_New:
                                        brandNew++;
                                        break;
                                    case VisitorType.Returning:
                                        returning++;
                                        break;
                                    case VisitorType.Regular:
                                        regular++;
                                        break;
                                }
                            }
                        }
                        
                        // Add to seen set
                        foreach (var visitorId in dailyVisitors)
                        {
                            seenInPeriod.Add(visitorId);
                        }
                        
                        return new DailyClassificationDto
                        {
                            Date = g.Key,
                            BrandNewVisitors = brandNew,
                            ReturningVisitors = returning,
                            RegularVisitors = regular,
                            TotalUniqueVisitors = dailyVisitors.Count,
                            SLTDate = _sltTimeService.FormatSLTWithDate(g.Key, "MMM dd, yyyy")
                        };
                    })
                    .ToList();
                
                // Calculate totals
                var totalBrandNew = uniqueVisitors.Count(v => v.VisitorType == VisitorType.Brand_New);
                var totalReturning = uniqueVisitors.Count(v => v.VisitorType == VisitorType.Returning);
                var totalRegular = uniqueVisitors.Count(v => v.VisitorType == VisitorType.Regular);
                
                return new VisitorClassificationDto
                {
                    RegionName = regionName,
                    StartDate = startDate,
                    EndDate = endDate,
                    BrandNewVisitors = totalBrandNew,
                    ReturningVisitors = totalReturning,
                    RegularVisitors = totalRegular,
                    TotalUniqueVisitors = uniqueVisitors.Count,
                    DailyBreakdown = dailyBreakdown,
                    VisitorDetails = uniqueVisitors.OrderByDescending(v => v.LastSeen).ToList(),
                    SLTStartDate = _sltTimeService.FormatSLTWithDate(startDate, "MMM dd, yyyy"),
                    SLTEndDate = _sltTimeService.FormatSLTWithDate(endDate, "MMM dd, yyyy")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting visitor classification for region {RegionName}", regionName);
                return new VisitorClassificationDto 
                { 
                    RegionName = regionName, 
                    StartDate = startDate, 
                    EndDate = endDate,
                    SLTStartDate = _sltTimeService.FormatSLTWithDate(startDate, "MMM dd, yyyy"),
                    SLTEndDate = _sltTimeService.FormatSLTWithDate(endDate, "MMM dd, yyyy")
                };
            }
        }
        
        public async Task<HourlyActivitySummaryDto> GetHourlyActivityAsync(DateTime startDate, DateTime endDate, string? regionName = null)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                // Convert UTC input dates to SLT for database querying
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var sltStartDate = TimeZoneInfo.ConvertTimeFromUtc(startDate, sltTimeZone).Date;
                var sltEndDate = TimeZoneInfo.ConvertTimeFromUtc(endDate, sltTimeZone).Date;
                
                var query = context.VisitorStats
                    .Where(vs => vs.VisitDate >= sltStartDate && vs.VisitDate <= sltEndDate);
                
                if (!string.IsNullOrEmpty(regionName))
                {
                    query = query.Where(vs => vs.RegionName == regionName);
                }
                
                // Get all visitor records for the period
                var visitorRecords = await query.ToListAsync();
                
                _logger.LogDebug("Found {Count} visitor records for hourly analysis from {StartDate} to {EndDate} for region {RegionName}", 
                    visitorRecords.Count, sltStartDate, sltEndDate, regionName ?? "All");
                
                // Convert UTC timestamps to SLT (Pacific Time) and group by hour
                TimeZoneInfo timeZone;
                try 
                {
                    timeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                }
                catch
                {
                    try
                    {
                        // Try alternative ID for Unix systems
                        timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
                    }
                    catch
                    {
                        // Fall back to UTC-8 if timezone lookup fails
                        timeZone = TimeZoneInfo.CreateCustomTimeZone("SLT", TimeSpan.FromHours(-8), "SLT", "SLT");
                        _logger.LogWarning("Could not find Pacific timezone, using UTC-8 fallback");
                    }
                }
                
                // Create hourly buckets (0-23)
                var hourlyStats = new Dictionary<int, List<string>>(); // Hour -> List of unique avatar IDs
                var hourlyVisits = new Dictionary<int, int>(); // Hour -> Total visit count
                
                for (int hour = 0; hour < 24; hour++)
                {
                    hourlyStats[hour] = new List<string>();
                    hourlyVisits[hour] = 0;
                }
                
                // Process each visitor record
                foreach (var record in visitorRecords)
                {
                    // Convert both FirstSeenAt and LastSeenAt to SLT
                    var firstSeenSLT = TimeZoneInfo.ConvertTimeFromUtc(record.FirstSeenAt, timeZone);
                    var lastSeenSLT = TimeZoneInfo.ConvertTimeFromUtc(record.LastSeenAt, timeZone);
                    
                    // Add visitor to the hour buckets they were active in
                    // For simplicity, we'll use the FirstSeenAt hour, but we could expand this
                    // to cover the entire time range if FirstSeenAt and LastSeenAt are significantly different
                    
                    var activeHour = firstSeenSLT.Hour;
                    
                    // Add unique visitor to the hour
                    if (!hourlyStats[activeHour].Contains(record.AvatarId))
                    {
                        hourlyStats[activeHour].Add(record.AvatarId);
                    }
                    
                    // Increment visit count for the hour
                    hourlyVisits[activeHour]++;
                    
                    // If LastSeenAt is significantly different (more than 1 hour), 
                    // add presence to those hours too
                    if ((lastSeenSLT - firstSeenSLT).TotalHours > 1)
                    {
                        var startHour = firstSeenSLT.Hour;
                        var endHour = lastSeenSLT.Hour;
                        
                        // Handle day boundary crossings
                        if (endHour < startHour) // Crossed midnight
                        {
                            // Add to hours from startHour to 23
                            for (int h = startHour + 1; h <= 23; h++)
                            {
                                if (!hourlyStats[h].Contains(record.AvatarId))
                                {
                                    hourlyStats[h].Add(record.AvatarId);
                                }
                            }
                            // Add to hours from 0 to endHour
                            for (int h = 0; h <= endHour; h++)
                            {
                                if (!hourlyStats[h].Contains(record.AvatarId))
                                {
                                    hourlyStats[h].Add(record.AvatarId);
                                }
                            }
                        }
                        else
                        {
                            // Normal case - add to intermediate hours
                            for (int h = startHour + 1; h <= endHour; h++)
                            {
                                if (!hourlyStats[h].Contains(record.AvatarId))
                                {
                                    hourlyStats[h].Add(record.AvatarId);
                                }
                            }
                        }
                    }
                }
                
                // Calculate the number of days analyzed
                var daysAnalyzed = (int)(endDate.Date - startDate.Date).TotalDays + 1;
                
                // Build the hourly statistics DTOs
                var hourlyStatsList = new List<HourlyVisitorStatsDto>();
                
                for (int hour = 0; hour < 24; hour++)
                {
                    var uniqueVisitors = hourlyStats[hour].Count;
                    var totalVisits = hourlyVisits[hour];
                    var averageVisitors = daysAnalyzed > 0 ? (double)uniqueVisitors / daysAnalyzed : 0;
                    
                    // Format hour label in 12-hour format
                    var hourLabel = DateTime.Today.AddHours(hour).ToString("h:00 tt");
                    
                    hourlyStatsList.Add(new HourlyVisitorStatsDto
                    {
                        Hour = hour,
                        HourLabel = hourLabel,
                        UniqueVisitors = uniqueVisitors,
                        TotalVisits = totalVisits,
                        AverageVisitors = Math.Round(averageVisitors, 2)
                    });
                }
                
                // Find peak and quiet hours (handle empty data case)
                var peakHour = hourlyStatsList.Any() ? hourlyStatsList.OrderByDescending(h => h.AverageVisitors).First() : 
                    new HourlyVisitorStatsDto { Hour = 12, HourLabel = "12:00 PM", AverageVisitors = 0 };
                var quietHour = hourlyStatsList.Any() ? hourlyStatsList.OrderBy(h => h.AverageVisitors).First() : 
                    new HourlyVisitorStatsDto { Hour = 0, HourLabel = "12:00 AM", AverageVisitors = 0 };
                
                var summary = new HourlyActivitySummaryDto
                {
                    RegionName = regionName ?? "All Regions",
                    StartDate = startDate,
                    EndDate = endDate,
                    DaysAnalyzed = daysAnalyzed,
                    HourlyStats = hourlyStatsList,
                    PeakHour = peakHour.Hour,
                    PeakHourLabel = peakHour.HourLabel,
                    PeakHourAverage = peakHour.AverageVisitors,
                    QuietHour = quietHour.Hour,
                    QuietHourLabel = quietHour.HourLabel,
                    QuietHourAverage = quietHour.AverageVisitors,
                    SLTStartDate = _sltTimeService.FormatSLTWithDate(startDate, "MMM dd, yyyy"),
                    SLTEndDate = _sltTimeService.FormatSLTWithDate(endDate, "MMM dd, yyyy")
                };
                
                return summary;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting hourly activity for region {RegionName}", regionName);
                return new HourlyActivitySummaryDto 
                { 
                    RegionName = regionName ?? "All Regions", 
                    StartDate = startDate, 
                    EndDate = endDate,
                    SLTStartDate = _sltTimeService.FormatSLTWithDate(startDate, "MMM dd, yyyy"),
                    SLTEndDate = _sltTimeService.FormatSLTWithDate(endDate, "MMM dd, yyyy")
                };
            }
        }

        /// <summary>
        /// Smart date normalization helper to handle mixed UTC/SLT data during transition period
        /// </summary>
        private DateTime GetNormalizedSLTDate(DateTime visitDate, DateTime sltStartDate, DateTime sltEndDate, 
            DateTime utcStartDate, DateTime utcEndDate, TimeZoneInfo sltTimeZone)
        {
            var dateOnly = visitDate.Date;
            
            _logger.LogDebug("Normalizing date {Date}: SLT range [{SLTStart} to {SLTEnd}], UTC range [{UTCStart} to {UTCEnd}]",
                dateOnly, sltStartDate, sltEndDate, utcStartDate.Date, utcEndDate.Date);
            
            // PRIORITY FIX: Check for today's date specifically first to handle the most common case
            var today = _sltTimeService.GetCurrentSLT().Date;
            if (dateOnly == today)
            {
                _logger.LogDebug("Date {Date} matches today {Today}, treating as SLT", dateOnly, today);
                return dateOnly; // Definitely SLT for today's data
            }
            
            // If the date falls within the SLT date range, treat it as SLT
            if (dateOnly >= sltStartDate && dateOnly <= sltEndDate)
            {
                _logger.LogDebug("Date {Date} falls within SLT range, treating as SLT", dateOnly);
                return dateOnly; // Already SLT
            }
            
            // If the date falls within the UTC date range, treat it as UTC and convert to SLT
            if (dateOnly >= utcStartDate.Date && dateOnly <= utcEndDate.Date)
            {
                // Treat as UTC midnight and convert to SLT date
                var utcDateTime = DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc);
                var converted = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, sltTimeZone).Date;
                _logger.LogDebug("Date {Date} falls within UTC range, converting to SLT: {Converted}", dateOnly, converted);
                return converted;
            }
            
            // Fallback: if unsure, check if it looks like a recent date and assume it's SLT
            // (This handles edge cases during the transition period)
            var daysDifference = Math.Abs((dateOnly - DateTime.Today).TotalDays);
            if (daysDifference <= 3) // Recent data within 3 days
            {
                _logger.LogDebug("Date {Date} is recent ({Days} days), assuming SLT", dateOnly, daysDifference);
                return dateOnly; // Assume SLT for recent data
            }
            
            // For older dates, assume UTC and convert
            var fallbackUtc = DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc);
            var fallbackConverted = TimeZoneInfo.ConvertTimeFromUtc(fallbackUtc, sltTimeZone).Date;
            _logger.LogDebug("Date {Date} is old, assuming UTC and converting to SLT: {Converted}", dateOnly, fallbackConverted);
            return fallbackConverted;
        }

        public void Dispose()
        {
            _recordingCleanupTimer?.Dispose();
        }
    }
}