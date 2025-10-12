using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RegionController : ControllerBase
    {
        private readonly IAccountService _accountService;
        private readonly ILogger<RegionController> _logger;

        public RegionController(IAccountService accountService, ILogger<RegionController> logger)
        {
            _accountService = accountService;
            _logger = logger;
        }

        /// <summary>
        /// Get detailed region statistics for an account
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <returns>Detailed region statistics including time dilation, FPS, script counts, etc.</returns>
        [HttpGet("{accountId}/stats")]
        public async Task<ActionResult<RegionStatsDto>> GetRegionStats(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null || !instance.IsConnected)
                {
                    return BadRequest(new { error = "Account not connected" });
                }

                var regionStats = await _accountService.GetRegionStatsAsync(accountId);
                if (regionStats == null)
                {
                    return NotFound(new { error = "Region statistics not available" });
                }

                return Ok(regionStats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region stats for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get basic region information for an account
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <returns>Basic region information</returns>
        [HttpGet("{accountId}/info")]
        public async Task<ActionResult<RegionInfoDto>> GetRegionInfo(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null || !instance.IsConnected)
                {
                    return BadRequest(new { error = "Account not connected" });
                }

                // Get detailed stats and convert to basic info for backward compatibility
                var regionStats = await _accountService.GetRegionStatsAsync(accountId);
                if (regionStats == null)
                {
                    return NotFound(new { error = "Region information not available" });
                }

                var regionInfo = new RegionInfoDto
                {
                    Name = regionStats.RegionName,
                    MaturityLevel = regionStats.MaturityLevel,
                    AvatarCount = (int)regionStats.TotalAgents,
                    RegionType = regionStats.ProductName,
                    AccountId = accountId,
                    RegionX = regionStats.RegionX,
                    RegionY = regionStats.RegionY
                };

                return Ok(regionInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region info for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}