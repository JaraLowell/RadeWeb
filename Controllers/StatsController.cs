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

        public StatsController(IStatsService statsService, ILogger<StatsController> logger)
        {
            _statsService = statsService;
            _logger = logger;
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
                var endDate = DateTime.UtcNow.Date;
                var startDate = endDate.AddDays(-Math.Max(1, days));

                List<VisitorStatsSummaryDto> stats;

                if (!string.IsNullOrEmpty(region))
                {
                    var regionStats = await _statsService.GetRegionStatsAsync(region, startDate, endDate);
                    stats = new List<VisitorStatsSummaryDto> { regionStats };
                }
                else
                {
                    stats = await _statsService.GetAllRegionStatsAsync(startDate, endDate);
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
                var endDate = DateTime.UtcNow.Date;
                var startDate = endDate.AddDays(-Math.Max(1, days));

                var stats = await _statsService.GetRegionStatsAsync(regionName, startDate, endDate);
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
                var endDate = DateTime.UtcNow.Date;
                var startDate = endDate.AddDays(-Math.Max(1, days));

                var visitors = await _statsService.GetUniqueVisitorsAsync(startDate, endDate, region);
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
                var endDate = DateTime.UtcNow.Date;
                var startDate = endDate.AddDays(-Math.Max(1, days));

                var classification = await _statsService.GetVisitorClassificationAsync(regionName, startDate, endDate);
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
        /// Get hourly visitor activity for the past X days (default 7) in SLT time
        /// </summary>
        [HttpGet("hourly")]
        public async Task<ActionResult<HourlyActivitySummaryDto>> GetHourlyActivity(
            [FromQuery] int days = 7,
            [FromQuery] string? region = null)
        {
            try
            {
                var endDate = DateTime.UtcNow.Date;
                var startDate = endDate.AddDays(-Math.Max(1, days));

                var hourlyStats = await _statsService.GetHourlyActivityAsync(startDate, endDate, region);
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
                var endDate = DateTime.UtcNow.Date;
                var startDate30 = endDate.AddDays(-Math.Max(1, days));
                var startDate7 = endDate.AddDays(-7);
                var startDate1 = endDate.AddDays(-1);

                // Get stats for different time periods
                List<VisitorStatsSummaryDto> stats30Days;
                List<VisitorStatsSummaryDto> stats7Days;
                List<VisitorStatsSummaryDto> statsToday;

                if (!string.IsNullOrEmpty(region))
                {
                    // Get stats for specific region
                    var regionStats30 = await _statsService.GetRegionStatsAsync(region, startDate30, endDate);
                    var regionStats7 = await _statsService.GetRegionStatsAsync(region, startDate7, endDate);
                    var regionStatsToday = await _statsService.GetRegionStatsAsync(region, startDate1, endDate);
                    
                    stats30Days = new List<VisitorStatsSummaryDto> { regionStats30 };
                    stats7Days = new List<VisitorStatsSummaryDto> { regionStats7 };
                    statsToday = new List<VisitorStatsSummaryDto> { regionStatsToday };
                }
                else
                {
                    // Get stats for all regions
                    stats30Days = await _statsService.GetAllRegionStatsAsync(startDate30, endDate);
                    stats7Days = await _statsService.GetAllRegionStatsAsync(startDate7, endDate);
                    statsToday = await _statsService.GetAllRegionStatsAsync(startDate1, endDate);
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
                        .ToList()
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