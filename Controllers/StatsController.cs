using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    /// <summary>
    /// Controller for visitor statistics and analytics
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class StatsController : ControllerBase
    {
        private readonly IStatsService _statsService;
        private readonly ILogger<StatsController> _logger;
        private readonly ISLTimeService _sltTimeService;
        private readonly IMasterDisplayNameService _displayNameService;
        private readonly INoticeService _noticeService;
        private readonly IStatsNameCache _statsNameCache;

        public StatsController(IStatsService statsService, ILogger<StatsController> logger, ISLTimeService sltTimeService, IMasterDisplayNameService displayNameService, INoticeService noticeService, IStatsNameCache statsNameCache)
        {
            _statsService = statsService;
            _logger = logger;
            _sltTimeService = sltTimeService;
            _displayNameService = displayNameService;
            _noticeService = noticeService;
            _statsNameCache = statsNameCache;
        }

        /// <summary>
        /// Get visitor statistics for the past 30 days for all regions
        /// </summary>
        [HttpGet("visitors")]
        public async Task<ActionResult<List<VisitorStatsSummaryDto>>> GetVisitorStats(
            [FromQuery] int days = 30,
            [FromQuery] string? region = null)
        {
            try
            {
                // Get current SLT date and calculate date range in SLT
                var currentSLT = _sltTimeService.GetCurrentSLT().Date;
                var endDate = currentSLT;
                
                // FIX: For days=1, show just today. For days>1, show that many days back from today
                var startDate = days == 1 ? endDate : endDate.AddDays(-days + 1);
                
                // Convert SLT dates to UTC for database queries
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcStartDate = TimeZoneInfo.ConvertTimeToUtc(startDate, sltTimeZone);
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone); // Include full end date

                List<VisitorStatsSummaryDto> stats;

                if (!string.IsNullOrEmpty(region))
                {
                    var regionStats = await _statsService.GetRegionStatsAsync(region, utcStartDate, utcEndDate);
                    stats = new List<VisitorStatsSummaryDto> { regionStats };
                }
                else
                {
                    stats = await _statsService.GetAllRegionStatsAsync(utcStartDate, utcEndDate);
                }

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting visitor statistics");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get visitor statistics for a specific region
        /// </summary>
        [HttpGet("visitors/region/{regionName}")]
        public async Task<ActionResult<VisitorStatsSummaryDto>> GetRegionStats(
            string regionName,
            [FromQuery] int days = 30)
        {
            try
            {
                // Get current SLT date and calculate date range in SLT
                var currentSLT = _sltTimeService.GetCurrentSLT().Date;
                var endDate = currentSLT;
                
                // FIX: For days=1, show just today. For days>1, show that many days back from today
                var startDate = days == 1 ? endDate : endDate.AddDays(-days + 1);
                
                // Convert SLT dates to UTC for database queries
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcStartDate = TimeZoneInfo.ConvertTimeToUtc(startDate, sltTimeZone);
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone); // Include full end date

                var stats = await _statsService.GetRegionStatsAsync(regionName, utcStartDate, utcEndDate);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region statistics for {RegionName}", regionName);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get unique visitors across all regions or for a specific region
        /// </summary>
        [HttpGet("visitors/unique")]
        public async Task<ActionResult<List<UniqueVisitorDto>>> GetUniqueVisitors(
            [FromQuery] int days = 30,
            [FromQuery] string? region = null)
        {
            try
            {
                // Get current SLT date and calculate date range in SLT
                var currentSLT = _sltTimeService.GetCurrentSLT().Date;
                var endDate = currentSLT;
                
                // FIX: For days=1, show just today. For days>1, show that many days back from today
                var startDate = days == 1 ? endDate : endDate.AddDays(-days + 1);
                
                // Convert SLT dates to UTC for database queries
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcStartDate = TimeZoneInfo.ConvertTimeToUtc(startDate, sltTimeZone);
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone); // Include full end date

                var visitors = await _statsService.GetUniqueVisitorsAsync(utcStartDate, utcEndDate, region);
                return Ok(visitors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting unique visitors for region {RegionName}", region);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get detailed visitor classification for a region
        /// </summary>
        [HttpGet("visitors/classification/{regionName}")]
        public async Task<ActionResult<VisitorClassificationDto>> GetVisitorClassification(
            string regionName,
            [FromQuery] int days = 30)
        {
            try
            {
                // Get current SLT date and calculate date range in SLT
                var currentSLT = _sltTimeService.GetCurrentSLT().Date;
                var endDate = currentSLT;
                
                // FIX: For days=1, show just today. For days>1, show that many days back from today
                var startDate = days == 1 ? endDate : endDate.AddDays(-days + 1);
                
                // Convert SLT dates to UTC for database queries
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcStartDate = TimeZoneInfo.ConvertTimeToUtc(startDate, sltTimeZone);
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone); // Include full end date

                var classification = await _statsService.GetVisitorClassificationAsync(regionName, utcStartDate, utcEndDate);
                return Ok(classification);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting visitor classification for region {RegionName}", regionName);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get regions currently being monitored
        /// </summary>
        [HttpGet("regions/monitored")]
        public async Task<ActionResult<List<string>>> GetMonitoredRegions()
        {
            try
            {
                var regions = await _statsService.GetMonitoredRegionsAsync();
                return Ok(regions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting monitored regions");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Clean up old visitor records (admin function)
        /// </summary>
        [HttpPost("cleanup")]
        public async Task<IActionResult> CleanupOldRecords([FromQuery] int keepDays = 90)
        {
            try
            {
                if (keepDays < 30) // Prevent accidental deletion of too much data
                {
                    return BadRequest("Keep days must be at least 30");
                }

                await _statsService.CleanupOldRecordsAsync(keepDays);
                return Ok(new { message = $"Cleanup completed, keeping records for the last {keepDays} days" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup operation");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Populate stats names from global display names cache (admin function)
        /// </summary>
        [HttpPost("populate-names")]
        public async Task<IActionResult> PopulateStatsNames()
        {
            try
            {
                await _statsNameCache.PopulateFromGlobalCacheAsync();
                return Ok(new { message = "Stats names population completed" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stats names population");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Test endpoint to verify hourly API is working
        /// </summary>
        [HttpGet("hourly/test")]
        public ActionResult<object> TestHourlyEndpoint()
        {
            return Ok(new { 
                message = "Hourly API endpoint is working", 
                utcTimestamp = DateTime.UtcNow,
                sltTime = _sltTimeService.GetCurrentSLT(),
                sltFormatted = _sltTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, yyyy HH:mm:ss")
            });
        }

        /// <summary>
        /// Debug endpoint to show date conversion logic
        /// </summary>
        [HttpGet("debug/dates")]
        public ActionResult<object> DebugDates()
        {
            try
            {
                var currentSLT = _sltTimeService.GetCurrentSLT();
                var currentSLTDate = currentSLT.Date;
                var endDate = currentSLTDate;
                var startDateToday = endDate;
                
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcStartDateToday = TimeZoneInfo.ConvertTimeToUtc(startDateToday, sltTimeZone);
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone);
                
                return Ok(new {
                    CurrentUTC = DateTime.UtcNow,
                    CurrentSLT = currentSLT,
                    CurrentSLTDate = currentSLTDate,
                    StartDateToday = startDateToday,
                    EndDate = endDate,
                    UTCStartDateToday = utcStartDateToday,
                    UTCEndDate = utcEndDate,
                    SLTTimeZone = sltTimeZone.Id,
                    SLTFormattedStart = _sltTimeService.FormatSLTWithDate(utcStartDateToday, "MMM dd, yyyy HH:mm:ss"),
                    SLTFormattedEnd = _sltTimeService.FormatSLTWithDate(utcEndDate, "MMM dd, yyyy HH:mm:ss"),
                    
                    // Show what dates we would store vs query for
                    WhatWeStoreForToday = _sltTimeService.GetCurrentSLT().Date,
                    WhatWeQueryForToday = TimeZoneInfo.ConvertTimeFromUtc(utcStartDateToday, sltTimeZone).Date,
                    DoTheyMatch = _sltTimeService.GetCurrentSLT().Date == TimeZoneInfo.ConvertTimeFromUtc(utcStartDateToday, sltTimeZone).Date
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in debug dates endpoint");
                return StatusCode(500, ex.Message);
            }
        }

        /// <summary>
        /// Get hourly visitor activity for the past X days (default 7) in SLT time
        /// </summary>
        [HttpGet("hourly")]
        public async Task<ActionResult<HourlyActivitySummaryDto>> GetHourlyActivity(
            [FromQuery] int days = 7,
            [FromQuery] string? region = null)
        {
            try
            {
                _logger.LogInformation("Hourly activity requested: days={Days}, region={Region}", days, region);
                
                // Get current SLT date and calculate date range in SLT
                var currentSLT = _sltTimeService.GetCurrentSLT().Date;
                var endDate = currentSLT;
                
                // FIX: For days=1, show just today. For days>1, show that many days back from today
                var startDate = days == 1 ? endDate : endDate.AddDays(-days + 1);
                
                // Convert SLT dates to UTC for database queries
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcStartDate = TimeZoneInfo.ConvertTimeToUtc(startDate, sltTimeZone);
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone); // Include full end date

                var hourlyStats = await _statsService.GetHourlyActivityAsync(utcStartDate, utcEndDate, region);
                
                _logger.LogInformation("Returning hourly stats with {HourCount} hours, peak at {PeakHour}", 
                    hourlyStats.HourlyStats.Count, hourlyStats.PeakHourLabel);
                
                return Ok(hourlyStats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting hourly activity for region {RegionName}", region);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get display name service retry queue statistics
        /// </summary>
        [HttpGet("displaynames/queue")]
        public ActionResult<object> GetDisplayNameQueueStats()
        {
            try
            {
                var (pendingRetries, readyForRetry, totalRequests, failureTypes) = _displayNameService.GetQueueStatistics();
                
                return Ok(new
                {
                    PendingRetries = pendingRetries,
                    ReadyForRetry = readyForRetry,
                    TotalRequests = totalRequests,
                    FailureTypes = failureTypes,
                    Timestamp = DateTime.UtcNow,
                    SLT = _sltTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, yyyy HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting display name queue statistics");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get statistics summary for dashboard
        /// </summary>
        [HttpGet("dashboard")]
        public async Task<ActionResult<object>> GetDashboardStats(
            [FromQuery] int days = 30,
            [FromQuery] string? region = null)
        {
            try
            {
                // MEMORY FIX: Limit dashboard query complexity to prevent memory issues
                days = Math.Min(days, 90); // Cap at 90 days maximum
                
                // Get current SLT date and calculate date ranges in SLT
                var currentSLT = _sltTimeService.GetCurrentSLT().Date;
                var endDate = currentSLT;
                var startDate30 = endDate.AddDays(-Math.Max(1, days));
                var startDate7 = endDate.AddDays(-7);
                var startDateToday = endDate; // Today's stats should start from today, not yesterday
                
                // Convert SLT dates to UTC for database queries
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone); // Include full end date
                var utcStartDate30 = TimeZoneInfo.ConvertTimeToUtc(startDate30, sltTimeZone);
                var utcStartDate7 = TimeZoneInfo.ConvertTimeToUtc(startDate7, sltTimeZone);
                var utcStartDateToday = TimeZoneInfo.ConvertTimeToUtc(startDateToday, sltTimeZone);

                // MEMORY FIX: Get stats for different time periods with explicit disposal
                List<VisitorStatsSummaryDto> stats30Days = null!;
                List<VisitorStatsSummaryDto> stats7Days = null!;
                List<VisitorStatsSummaryDto> statsToday = null!;
                List<UniqueVisitorDto> recentVisitors = null!;

                if (!string.IsNullOrEmpty(region))
                {
                    // Get stats for specific region (more efficient)
                    var regionStats30 = await _statsService.GetRegionStatsAsync(region, utcStartDate30, utcEndDate);
                    var regionStats7 = await _statsService.GetRegionStatsAsync(region, utcStartDate7, utcEndDate);
                    var regionStatsToday = await _statsService.GetRegionStatsAsync(region, utcStartDateToday, utcEndDate);
                    
                    stats30Days = new List<VisitorStatsSummaryDto> { regionStats30 };
                    stats7Days = new List<VisitorStatsSummaryDto> { regionStats7 };
                    statsToday = new List<VisitorStatsSummaryDto> { regionStatsToday };
                }
                else
                {
                    // MEMORY FIX: Execute these sequentially to reduce memory pressure
                    stats30Days = await _statsService.GetAllRegionStatsAsync(utcStartDate30, utcEndDate);
                    GC.Collect(0, GCCollectionMode.Optimized); // Force cleanup between operations
                    
                    stats7Days = await _statsService.GetAllRegionStatsAsync(utcStartDate7, utcEndDate);
                    GC.Collect(0, GCCollectionMode.Optimized);
                    
                    statsToday = await _statsService.GetAllRegionStatsAsync(utcStartDateToday, utcEndDate);
                    GC.Collect(0, GCCollectionMode.Optimized);
                }

                // MEMORY FIX: Get recent visitors with limited scope
                recentVisitors = await _statsService.GetUniqueVisitorsAsync(utcStartDate7, utcEndDate, region);

                var totalVisitors30Days = stats30Days.Sum(s => s.TotalUniqueVisitors);
                var totalVisitors7Days = stats7Days.Sum(s => s.TotalUniqueVisitors);
                var totalVisitorsToday = statsToday.Sum(s => s.TotalUniqueVisitors);

                var monitoredRegions = await _statsService.GetMonitoredRegionsAsync();

                var dashboard = new
                {
                    TotalUniqueVisitors30Days = totalVisitors30Days,
                    TotalTrueUniqueVisitors30Days = stats30Days.Sum(s => s.TrueUniqueVisitors),
                    TotalUniqueVisitors7Days = totalVisitors7Days,
                    TotalTrueUniqueVisitors7Days = stats7Days.Sum(s => s.TrueUniqueVisitors),
                    TotalVisitorsToday = totalVisitorsToday,
                    TotalTrueUniqueVisitorsToday = statsToday.Sum(s => s.TrueUniqueVisitors),
                    MonitoredRegionsCount = monitoredRegions.Count,
                    MonitoredRegions = monitoredRegions,
                    RegionStats = stats30Days.Select(s => new
                    {
                        s.RegionName,
                        s.TotalUniqueVisitors,
                        s.TrueUniqueVisitors,
                        s.TotalVisits,
                        AverageVisitorsPerDay = s.DailyStats.Count > 0 ? s.DailyStats.Average(d => d.UniqueVisitors) : 0,
                        AverageTrueUniquePerDay = s.DailyStats.Count > 0 ? s.DailyStats.Average(d => d.TrueUniqueVisitors) : 0
                    }).OrderByDescending(s => s.TotalUniqueVisitors).Take(10),
                    RecentActivity = stats7Days.SelectMany(s => s.DailyStats)
                        .GroupBy(d => d.Date)
                        .Select(g => new
                        {
                            Date = g.Key,
                            TotalVisitors = g.Sum(d => d.UniqueVisitors),
                            TotalTrueUnique = g.Sum(d => d.TrueUniqueVisitors),
                            TotalVisits = g.Sum(d => d.TotalVisits)
                        })
                        .OrderBy(d => d.Date)
                        .ToList(),
                    // Include recent visitors data for the Recent Visitors table
                    RecentVisitors = recentVisitors,
                    // Add SLT context information - show the range that was actually requested for today's stats
                    SLTDateRange = new
                    {
                        StartDate = _sltTimeService.FormatSLTWithDate(currentSLT, "MMM dd, yyyy"),
                        EndDate = _sltTimeService.FormatSLTWithDate(currentSLT, "MMM dd, yyyy"),
                        CurrentSLT = _sltTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, yyyy HH:mm:ss")
                    }
                };

                return Ok(dashboard);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting dashboard statistics");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Clean up notices for a specific account (admin function)
        /// </summary>
        [HttpDelete("notices/account/{accountId}")]
        public async Task<IActionResult> CleanupAccountNotices(Guid accountId)
        {
            try
            {
                int deletedCount = await _noticeService.CleanupAccountNoticesAsync(accountId);
                return Ok(new { 
                    message = $"Cleanup completed for account {accountId}", 
                    deletedCount = deletedCount 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during notice cleanup for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Clean up old notices across all accounts (admin function)
        /// </summary>
        [HttpPost("cleanup/notices")]
        public async Task<IActionResult> CleanupOldNotices([FromQuery] int keepDays = 30)
        {
            try
            {
                if (keepDays < 1)
                {
                    return BadRequest("Keep days must be at least 1");
                }

                int deletedCount = await _noticeService.CleanupOldNoticesAsync(keepDays);
                return Ok(new { 
                    message = $"Notice cleanup completed, deleted notices older than {keepDays} days",
                    deletedCount = deletedCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during old notices cleanup");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get notice statistics across all accounts
        /// </summary>
        [HttpGet("notices")]
        public async Task<ActionResult<object>> GetNoticeStatistics()
        {
            try
            {
                int totalNotices = await _noticeService.GetTotalNoticeCountAsync();
                Dictionary<Guid, int> accountNoticeCounts = await _noticeService.GetAccountNoticeCountsAsync();
                DateTime? oldestNotice = await _noticeService.GetOldestNoticeAsync();
                DateTime? newestNotice = await _noticeService.GetNewestNoticeAsync();

                return Ok(new
                {
                    TotalNotices = totalNotices,
                    AccountCounts = accountNoticeCounts,
                    OldestNotice = oldestNotice,
                    NewestNotice = newestNotice,
                    Timestamp = DateTime.UtcNow,
                    SLT = _sltTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, yyyy HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting notice statistics");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}