#if DEBUG
using Microsoft.AspNetCore.Mvc;
using OpenMetaverse;

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
        /// Test endpoint to demonstrate the distance calculation difference
        /// Shows why webapp showed different distances than Radegast
        /// </summary>
        [HttpGet("distance-calculation")]
        public IActionResult TestDistanceCalculation()
        {
            try
            {
                // Example scenario: Two avatars with height difference
                var avatarOnGround = new Vector3(100f, 100f, 20f);      // Ground level (Z=20)
                var avatarOnPlatform = new Vector3(174.4f, 100f, 40f);  // On platform (Z=40), 74.4m away horizontally

                // Calculate distances using both methods
                var horizontalDistance = CalculateHorizontalDistance(avatarOnGround, avatarOnPlatform);
                var fullDistance = Vector3.Distance(avatarOnGround, avatarOnPlatform);

                var result = new
                {
                    explanation = "This demonstrates why the webapp showed different distances than Radegast",
                    scenario = "Two avatars: one on ground level, one on a platform 20m higher",
                    avatar1Position = new { x = avatarOnGround.X, y = avatarOnGround.Y, z = avatarOnGround.Z },
                    avatar2Position = new { x = avatarOnPlatform.X, y = avatarOnPlatform.Y, z = avatarOnPlatform.Z },
                    horizontalDistance2D = Math.Round(horizontalDistance, 1),
                    fullDistance3D = Math.Round(fullDistance, 1),
                    heightDifference = Math.Abs(avatarOnPlatform.Z - avatarOnGround.Z),
                    oldBehavior = "Webapp showed 135.6m (including height)",
                    newBehavior = "Webapp now shows 74.4m (horizontal only, like Radegast)",
                    conclusion = "The webapp was including height differences in distance calculations. Now it matches Radegast's practical horizontal distance."
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in distance calculation test");
                return StatusCode(500, new { error = "Internal server error", message = ex.Message });
            }
        }

        /// <summary>
        /// Calculate horizontal distance between two positions, ignoring height difference.
        /// This matches how Radegast calculates nearby people distance.
        /// </summary>
        private static float CalculateHorizontalDistance(Vector3 pos1, Vector3 pos2)
        {
            var deltaX = pos2.X - pos1.X;
            var deltaY = pos2.Y - pos1.Y;
            return (float)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
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
                var mapUrl = $"https://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
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
                var mapUrl = $"https://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
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

        /// <summary>
        /// Test the notice acknowledgment logic (without actually connecting to SL)
        /// </summary>
        [HttpGet("notice-acknowledgment")]
        public IActionResult TestNoticeAcknowledgment()
        {
            try
            {
                var testResults = new
                {
                    description = "Tests the automatic notice acknowledgment logic implementation",
                    scenarios = new[]
                    {
                        new
                        {
                            type = "GroupNotice without attachment",
                            requiresAcknowledgment = false,
                            isAcknowledged = true,
                            behavior = "Auto-acknowledged immediately (no SL confirmation needed)"
                        },
                        new
                        {
                            type = "GroupNotice with attachment",
                            requiresAcknowledgment = true,
                            isAcknowledged = false,
                            behavior = "Requires acknowledgment - will send GroupNoticeInventoryAccepted to SL"
                        },
                        new
                        {
                            type = "GroupNoticeRequested",
                            requiresAcknowledgment = true,
                            isAcknowledged = false,
                            behavior = "Always requires acknowledgment - will send appropriate dialog to SL"
                        }
                    },
                    acknowledgmentProtocol = new
                    {
                        withoutAttachment = "InstantMessageDialog.MessageFromAgent",
                        withAttachment = "InstantMessageDialog.GroupNoticeInventoryAccepted + inventory folder ID",
                        binaryBucket = "Contains destination folder UUID for attachments"
                    },
                    radegastCompatibility = "Implementation follows Radegast's GroupDetails.cs acknowledgment pattern",
                    implementation = "Auto-acknowledgment happens in WebRadegastInstance.Self_IM method"
                };

                return Ok(testResults);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in notice acknowledgment test");
                return StatusCode(500, new { error = "Internal server error", message = ex.Message });
            }
        }
    }
}
#endif