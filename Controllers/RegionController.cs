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
        private readonly IRegionMapCacheService _regionMapCacheService;
        private readonly ILogger<RegionController> _logger;

        public RegionController(IAccountService accountService, IRegionMapCacheService regionMapCacheService, ILogger<RegionController> logger)
        {
            _accountService = accountService;
            _regionMapCacheService = regionMapCacheService;
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
        /// Get the public Second Life map URL for the account's current region
        /// No server-side caching needed - browser handles caching of the public URL
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <returns>Redirect to the public Second Life map image</returns>
        [HttpGet("{accountId}/map")]
        public IActionResult GetRegionMap(Guid accountId)
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

                // Generate the public map URL from Second Life's CDN
                var publicMapUrl = $"https://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
                _logger.LogDebug("Redirecting to public map URL for {RegionName} at ({RegionX}, {RegionY}): {Url}", 
                    currentSim.Name, regionX, regionY, publicMapUrl);

                // Redirect to the public URL - browser will handle caching
                return Redirect(publicMapUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region map URL for account {AccountId}", accountId);
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

                // Generate the direct public map URL from Second Life's CDN
                // Browser will fetch and cache this directly - no server-side caching needed
                var publicMapUrl = $"https://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";

                var result = new
                {
                    regionName = currentSim.Name,
                    regionX = regionX,
                    regionY = regionY,
                    mapImageUrl = publicMapUrl, // Direct public URL
                    publicMapUrl = publicMapUrl,
                    localPosition = new
                    {
                        x = client.Self.SimPosition.X,
                        y = client.Self.SimPosition.Y,
                        z = client.Self.SimPosition.Z
                    },
                    hasMapImage = true // Public URL is always available
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region map info for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get region map cache statistics
        /// </summary>
        /// <returns>Cache statistics</returns>
        [HttpGet("cache/stats")]
        public ActionResult<object> GetCacheStats()
        {
            try
            {
                var result = new
                {
                    cachedRegionCount = _regionMapCacheService.GetCacheSize(),
                    timestamp = DateTime.UtcNow
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cache stats");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Clear expired maps from cache
        /// </summary>
        /// <returns>Success result</returns>
        [HttpPost("cache/cleanup")]
        public ActionResult<object> CleanupCache()
        {
            try
            {
                var sizeBefore = _regionMapCacheService.GetCacheSize();
                _regionMapCacheService.ClearExpiredMaps();
                var sizeAfter = _regionMapCacheService.GetCacheSize();

                var result = new
                {
                    message = "Cache cleanup completed",
                    sizeBefore = sizeBefore,
                    sizeAfter = sizeAfter,
                    clearedCount = sizeBefore - sizeAfter,
                    timestamp = DateTime.UtcNow
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up cache");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get the current parcel music URL
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <returns>Current music URL from the parcel</returns>
        [HttpGet("{accountId}/music")]
        public ActionResult<object> GetParcelMusicUrl(Guid accountId)
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

                // Get the parcel at the avatar's current position
                var parcel = currentSim.Parcels.FirstOrDefault(p => p.Value.LocalID == client.Parcels.GetParcelLocalID(currentSim, client.Self.SimPosition));
                var musicUrl = parcel.Value?.MusicURL ?? string.Empty;

                return Ok(new { musicUrl = musicUrl });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting parcel music URL for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Set the parcel music URL (requires parcel permissions)
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <param name="request">Music URL to set</param>
        /// <returns>Success or failure result</returns>
        [HttpPost("{accountId}/music")]
        public ActionResult<object> SetParcelMusicUrl(Guid accountId, [FromBody] SetMusicUrlRequest request)
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

                // TODO: Implement parcel music URL update
                // The LibreMetaverse API for updating parcel properties needs to be researched further
                // For now, return not implemented status
                return StatusCode(501, new { error = "Music URL update not yet implemented", message = "This feature requires additional LibreMetaverse API research" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting parcel music URL for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error", message = ex.Message });
            }
        }

        /// <summary>
        /// Restart the current region (requires Estate Manager or higher permissions)
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <returns>Success or failure result</returns>
        [HttpPost("{accountId}/restart")]
        public ActionResult<object> RestartRegion(Guid accountId)
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

                // Request region restart - this will only work if the user has Estate Manager or Owner permissions
                // LibreMetaverse's EstateTools.RestartRegion() sends the appropriate packet
                client.Estate.RestartRegion();

                _logger.LogInformation("Region restart requested for account {AccountId} in region {RegionName}", accountId, currentSim.Name);

                return Ok(new { success = true, message = "Region restart request sent. The region will restart shortly if you have the required permissions." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restarting region for account {AccountId}", accountId);
                return StatusCode(500, new { error = "Internal server error", message = ex.Message });
            }
        }
    }

    public class SetMusicUrlRequest
    {
        public string? MusicUrl { get; set; }
    }
}