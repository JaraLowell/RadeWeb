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

        /// <summary>
        /// Download the region map image for an account using Linden Lab's public Map API
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <returns>The region map image as JPEG</returns>
        [HttpGet("{accountId}/map")]
        public async Task<IActionResult> GetRegionMap(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null || !instance.IsConnected)
                {
                    return BadRequest(new { error = "Account not connected" });
                }

                var client = instance.Client;
                var currentSim = client.Network.CurrentSim;
                
                if (currentSim == null)
                {
                    return BadRequest(new { error = "No current region" });
                }

                // Calculate region coordinates from the sim handle
                var regionX = (ulong)(currentSim.Handle >> 32) / 256;
                var regionY = (ulong)(currentSim.Handle & 0xFFFFFFFF) / 256;

                // Use Linden Lab's public Map API to get the region image
                // Format: http://map.secondlife.com/map-{z}-{x}-{y}-objects.jpg
                // z=1 means most zoomed in (one region per tile)
                var mapUrl = $"http://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
                _logger.LogInformation("Fetching region map from URL: {MapUrl} for region {RegionName} at ({RegionX}, {RegionY})", 
                    mapUrl, currentSim.Name, regionX, regionY);

                // Download the image from Linden Lab's servers
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                
                var response = await httpClient.GetAsync(mapUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch region map from {MapUrl}. Status: {StatusCode}", 
                        mapUrl, response.StatusCode);
                    return NotFound(new { error = "Region map image not available" });
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    return NotFound(new { error = "Failed to download region map image" });
                }

                // Return the image as JPEG
                return File(imageBytes, "image/jpeg", $"{currentSim.Name}_map.jpg");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region map for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get region map information including image URL and metadata
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <returns>Region map metadata</returns>
        [HttpGet("{accountId}/map/info")]
        public ActionResult<object> GetRegionMapInfo(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null || !instance.IsConnected)
                {
                    return BadRequest(new { error = "Account not connected" });
                }

                var client = instance.Client;
                var currentSim = client.Network.CurrentSim;
                
                if (currentSim == null)
                {
                    return BadRequest(new { error = "No current region" });
                }

                // Calculate region coordinates from the sim handle
                var regionX = (ulong)(currentSim.Handle >> 32) / 256;
                var regionY = (ulong)(currentSim.Handle & 0xFFFFFFFF) / 256;

                // Generate the public map URL using Linden Lab's Map API
                var publicMapUrl = $"http://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";

                var result = new
                {
                    regionName = currentSim.Name,
                    regionX = regionX,
                    regionY = regionY,
                    mapImageUrl = $"/api/region/{accountId}/map",
                    publicMapUrl = publicMapUrl,
                    localPosition = new
                    {
                        x = client.Self.SimPosition.X,
                        y = client.Self.SimPosition.Y,
                        z = client.Self.SimPosition.Z
                    },
                    hasMapImage = true // Always true with public API
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region map info for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}