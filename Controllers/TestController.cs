using Microsoft.AspNetCore.Mvc;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestController : ControllerBase
    {
        private readonly ILogger<TestController> _logger;

        public TestController(ILogger<TestController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Test endpoint to verify Map API access
        /// </summary>
        /// <param name="regionX">Region X coordinate</param>
        /// <param name="regionY">Region Y coordinate</param>
        /// <returns>Test result</returns>
        [HttpGet("map/{regionX}/{regionY}")]
        public async Task<IActionResult> TestMapApi(int regionX, int regionY)
        {
            try
            {
                var mapUrl = $"http://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
                _logger.LogInformation("Testing Map API access for URL: {MapUrl}", mapUrl);

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                
                var response = await httpClient.GetAsync(mapUrl);
                
                var result = new
                {
                    mapUrl = mapUrl,
                    statusCode = (int)response.StatusCode,
                    isSuccess = response.IsSuccessStatusCode,
                    contentType = response.Content.Headers.ContentType?.ToString(),
                    contentLength = response.Content.Headers.ContentLength,
                    regionX = regionX,
                    regionY = regionY
                };

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Map API test successful for region ({RegionX}, {RegionY})", regionX, regionY);
                    return Ok(result);
                }
                else
                {
                    _logger.LogWarning("Map API test failed for region ({RegionX}, {RegionY}). Status: {StatusCode}", 
                        regionX, regionY, response.StatusCode);
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing Map API for region ({RegionX}, {RegionY})", regionX, regionY);
                return StatusCode(500, new { error = "Internal server error", message = ex.Message });
            }
        }

        /// <summary>
        /// Test endpoint to proxy a map image (for testing purposes)
        /// </summary>
        /// <param name="regionX">Region X coordinate</param>
        /// <param name="regionY">Region Y coordinate</param>
        /// <returns>Map image</returns>
        [HttpGet("map/{regionX}/{regionY}/image")]
        public async Task<IActionResult> GetTestMapImage(int regionX, int regionY)
        {
            try
            {
                var mapUrl = $"http://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
                _logger.LogInformation("Proxying map image from URL: {MapUrl}", mapUrl);

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                
                var response = await httpClient.GetAsync(mapUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    return NotFound(new { error = "Map image not available", regionX, regionY });
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                return File(imageBytes, "image/jpeg", $"test_map_{regionX}_{regionY}.jpg");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proxying map image for region ({RegionX}, {RegionY})", regionX, regionY);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}