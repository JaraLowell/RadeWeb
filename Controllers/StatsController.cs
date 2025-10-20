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

        public StatsController(IStatsService statsService, ILogger<StatsController> logger, ISLTimeService sltTimeService)
        {
            _statsService = statsService;
            _logger = logger;
            _sltTimeService = sltTimeService;
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
                var currentSLT = _sltTimeService.GetCurrentSLT().Date;
                var endDate = currentSLT;
                var startDateToday = endDate;
                
                var sltTimeZone = _sltTimeService.GetSLTTimeZone();
                var utcStartDateToday = TimeZoneInfo.ConvertTimeToUtc(startDateToday, sltTimeZone);
                var utcEndDate = TimeZoneInfo.ConvertTimeToUtc(endDate.AddDays(1), sltTimeZone);
                
                return Ok(new {
                    CurrentUTC = DateTime.UtcNow,
                    CurrentSLT = _sltTimeService.GetCurrentSLT(),
                    CurrentSLTDate = currentSLT,
                    StartDateToday = startDateToday,
                    EndDate = endDate,
                    UTCStartDateToday = utcStartDateToday,
                    UTCEndDate = utcEndDate,
                    SLTTimeZone = sltTimeZone.Id,
                    SLTFormattedStart = _sltTimeService.FormatSLTWithDate(utcStartDateToday, "MMM dd, yyyy HH:mm:ss"),
                    SLTFormattedEnd = _sltTimeService.FormatSLTWithDate(utcEndDate, "MMM dd, yyyy HH:mm:ss")
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
        /// Get statistics summary for dashboard
        /// </summary>
        [HttpGet("dashboard")]
        public async Task<ActionResult<object>> GetDashboardStats(
            [FromQuery] int days = 30,
            [FromQuery] string? region = null)
        {
            try
            {
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

                // Get stats for different time periods
                List<VisitorStatsSummaryDto> stats30Days;
                List<VisitorStatsSummaryDto> stats7Days;
                List<VisitorStatsSummaryDto> statsToday;

                if (!string.IsNullOrEmpty(region))
                {
                    // Get stats for specific region
                    var regionStats30 = await _statsService.GetRegionStatsAsync(region, utcStartDate30, utcEndDate);
                    var regionStats7 = await _statsService.GetRegionStatsAsync(region, utcStartDate7, utcEndDate);
                    var regionStatsToday = await _statsService.GetRegionStatsAsync(region, utcStartDateToday, utcEndDate);
                    
                    stats30Days = new List<VisitorStatsSummaryDto> { regionStats30 };
                    stats7Days = new List<VisitorStatsSummaryDto> { regionStats7 };
                    statsToday = new List<VisitorStatsSummaryDto> { regionStatsToday };
                }
                else
                {
                    // Get stats for all regions
                    stats30Days = await _statsService.GetAllRegionStatsAsync(utcStartDate30, utcEndDate);
                    stats7Days = await _statsService.GetAllRegionStatsAsync(utcStartDate7, utcEndDate);
                    statsToday = await _statsService.GetAllRegionStatsAsync(utcStartDateToday, utcEndDate);
                }

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
    }
}