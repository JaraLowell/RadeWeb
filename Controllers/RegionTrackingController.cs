using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RegionTrackingController : ControllerBase
    {
        private readonly IRegionTrackingService _trackingService;
        private readonly ILogger<RegionTrackingController> _logger;

        public RegionTrackingController(
            IRegionTrackingService trackingService,
            ILogger<RegionTrackingController> logger)
        {
            _trackingService = trackingService;
            _logger = logger;
        }

        /// <summary>
        /// Get the current tracking configuration
        /// </summary>
        [HttpGet("config")]
        public async Task<ActionResult<RegionTrackingConfig>> GetConfig()
        {
            var config = await _trackingService.GetConfigAsync();
            if (config == null)
            {
                return NotFound("Tracking configuration not found");
            }
            
            return Ok(config);
        }

        /// <summary>
        /// Update the tracking configuration
        /// </summary>
        [HttpPut("config")]
        public async Task<IActionResult> UpdateConfig([FromBody] RegionTrackingConfig config)
        {
            try
            {
                await _trackingService.UpdateConfigAsync(config);
                return Ok(new { message = "Configuration updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tracking config");
                return StatusCode(500, new { error = "Failed to update configuration" });
            }
        }

        /// <summary>
        /// Get the latest status for all tracked regions
        /// </summary>
        [HttpGet("status/latest")]
        public async Task<ActionResult<List<RegionStatus>>> GetLatestStatuses()
        {
            try
            {
                var statuses = await _trackingService.GetLatestStatusesAsync();
                return Ok(statuses);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting latest statuses");
                return StatusCode(500, new { error = "Failed to retrieve statuses" });
            }
        }

        /// <summary>
        /// Get historical status data for a specific region
        /// </summary>
        [HttpGet("status/{regionName}")]
        public async Task<ActionResult<List<RegionStatus>>> GetRegionHistory(
            string regionName,
            [FromQuery] DateTime? since = null)
        {
            try
            {
                var history = await _trackingService.GetRegionHistoryAsync(regionName, since);
                return Ok(history);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region history for {RegionName}", regionName);
                return StatusCode(500, new { error = "Failed to retrieve region history" });
            }
        }

        /// <summary>
        /// Manually trigger a region check (useful for testing)
        /// </summary>
        [HttpPost("check")]
        public IActionResult TriggerCheck()
        {
            try
            {
                // Run the check in the background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _trackingService.CheckRegionsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in background region check");
                    }
                });

                return Accepted(new { message = "Region check triggered" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering region check");
                return StatusCode(500, new { error = "Failed to trigger check" });
            }
        }

        /// <summary>
        /// Get statistics for a region (agent count trends, uptime, etc.)
        /// </summary>
        [HttpGet("stats/{regionName}")]
        public async Task<ActionResult<object>> GetRegionStats(
            string regionName,
            [FromQuery] DateTime? since = null)
        {
            try
            {
                var history = await _trackingService.GetRegionHistoryAsync(
                    regionName, 
                    since ?? DateTime.UtcNow.AddDays(-7));

                if (!history.Any())
                {
                    return NotFound(new { error = "No data found for this region" });
                }

                var totalChecks = history.Count;
                var onlineChecks = history.Count(r => r.IsOnline);
                var uptimePercentage = totalChecks > 0 ? (double)onlineChecks / totalChecks * 100 : 0;
                
                // Agent count statistics
                var checksWithAgents = history.Where(r => r.AgentCount.HasValue && r.IsOnline).ToList();
                var avgAgents = checksWithAgents.Any() 
                    ? checksWithAgents.Average(r => r.AgentCount!.Value) 
                    : (double?)null;
                var maxAgents = checksWithAgents.Any() 
                    ? checksWithAgents.Max(r => r.AgentCount!.Value) 
                    : (int?)null;
                var minAgents = checksWithAgents.Any() 
                    ? checksWithAgents.Min(r => r.AgentCount!.Value) 
                    : (int?)null;

                var latestStatus = history.OrderByDescending(r => r.CheckedAt).First();

                return Ok(new
                {
                    regionName,
                    totalChecks,
                    onlineChecks,
                    offlineChecks = totalChecks - onlineChecks,
                    uptimePercentage = Math.Round(uptimePercentage, 2),
                    agentStats = new
                    {
                        current = latestStatus.AgentCount,
                        average = avgAgents.HasValue ? (double?)Math.Round(avgAgents.Value, 1) : null,
                        maximum = maxAgents,
                        minimum = minAgents,
                        totalDataPoints = checksWithAgents.Count
                    },
                    firstCheck = history.Min(r => r.CheckedAt),
                    lastCheck = history.Max(r => r.CheckedAt),
                    currentStatus = latestStatus.IsOnline ? "Online" : "Offline"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stats for {RegionName}", regionName);
                return StatusCode(500, new { error = "Failed to retrieve stats" });
            }
        }
        
        /// <summary>
        /// Get current agent counts for all tracked regions (live snapshot)
        /// </summary>
        [HttpGet("agents/current")]
        public async Task<ActionResult<object>> GetCurrentAgentCounts()
        {
            try
            {
                var latestStatuses = await _trackingService.GetLatestStatusesAsync();
                
                var agentData = latestStatuses
                    .OrderByDescending(r => r.AgentCount ?? 0)
                    .Select(r => new
                    {
                        regionName = r.RegionName,
                        isOnline = r.IsOnline,
                        agentCount = r.AgentCount ?? 0,
                        lastChecked = r.CheckedAt,
                        accessLevel = r.AccessLevel
                    })
                    .ToList();

                return Ok(new
                {
                    timestamp = DateTime.UtcNow,
                    totalRegions = agentData.Count,
                    onlineRegions = agentData.Count(r => r.isOnline),
                    totalAgents = agentData.Where(r => r.isOnline).Sum(r => r.agentCount),
                    regions = agentData
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current agent counts");
                return StatusCode(500, new { error = "Failed to retrieve agent counts" });
            }
        }

        /// <summary>
        /// Manually cleanup old region tracking records
        /// </summary>
        /// <param name="keepDays">Number of days of history to keep (default: 32)</param>
        [HttpDelete("cleanup")]
        public async Task<ActionResult<object>> CleanupOldRecords([FromQuery] int keepDays = 32)
        {
            try
            {
                if (keepDays < 1 || keepDays > 365)
                {
                    return BadRequest(new { error = "keepDays must be between 1 and 365" });
                }

                var deletedCount = await _trackingService.CleanupOldRecordsAsync(keepDays);
                
                return Ok(new
                {
                    message = "Cleanup completed successfully",
                    deletedRecords = deletedCount,
                    keepDays = keepDays,
                    cutoffDate = DateTime.UtcNow.AddDays(-keepDays)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old records");
                return StatusCode(500, new { error = "Failed to cleanup old records" });
            }
        }
    }
}
