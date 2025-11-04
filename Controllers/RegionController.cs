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
        /// Download the region map image for an account using Linden Lab's public Map API with in-memory caching
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

                _logger.LogDebug("Getting region map for {RegionName} at ({RegionX}, {RegionY})", 
                    currentSim.Name, regionX, regionY);

                // Try to get the map from cache first (account-specific)
                var imageBytes = await _regionMapCacheService.GetRegionMapForAccountAsync(accountId, regionX, regionY);
                
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    _logger.LogWarning("Failed to get region map for ({RegionX}, {RegionY})", regionX, regionY);
                    return NotFound(new { error = "Region map image not available" });
                }

                _logger.LogDebug("Serving region map for {RegionName} at ({RegionX}, {RegionY}), size: {SizeKB}KB", 
                    currentSim.Name, regionX, regionY, imageBytes.Length / 1024);

                // Return the image as JPEG with longer caching headers since maps rarely change
                Response.Headers["Cache-Control"] = "public, max-age=21600"; // 6 hour browser cache to match server cache
                Response.Headers["ETag"] = $"\"{regionX}_{regionY}\""; // Simple ETag for cache validation
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

                // Check if the map is cached for this account
                var isCached = _regionMapCacheService.IsRegionMapCachedForAccount(accountId, regionX, regionY);

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
                    hasMapImage = true, // Always true with public API
                    isCached = isCached,
                    cacheSize = _regionMapCacheService.GetCacheSize()
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
    }
}