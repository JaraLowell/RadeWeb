#if DEBUG
using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    /// <summary>
    /// Controller for testing IM relay functionality
    /// </summary>
    [ApiController]
    [Route("api/test/im-relay")]
    public class TestIMRelayController : ControllerBase
    {
        private readonly ILogger<TestIMRelayController> _logger;
        private readonly IAccountService _accountService;
        private readonly IChatProcessingService _chatProcessingService;

        public TestIMRelayController(
            ILogger<TestIMRelayController> logger,
            IAccountService accountService,
            IChatProcessingService chatProcessingService)
        {
            _logger = logger;
            _accountService = accountService;
            _chatProcessingService = chatProcessingService;
        }

        /// <summary>
        /// Test IM relay by simulating an incoming IM message
        /// </summary>
        [HttpPost("test-im")]
        public async Task<IActionResult> TestIMRelay([FromBody] TestIMRequest request)
        {
            try
            {
                // Get the account to verify it exists and has relay configured
                var account = await _accountService.GetAccountAsync(request.AccountId);
                if (account == null)
                {
                    return NotFound("Account not found");
                }

                if (string.IsNullOrEmpty(account.AvatarRelayUuid) || account.AvatarRelayUuid == "00000000-0000-0000-0000-000000000000")
                {
                    return BadRequest("Account does not have a valid relay avatar UUID configured");
                }

                // Create a simulated incoming IM message
                var imMessage = new ChatMessageDto
                {
                    AccountId = request.AccountId,
                    SenderName = request.SenderName,
                    SenderId = request.SenderId,
                    Message = request.Message,
                    ChatType = "IM",
                    Channel = "IM",
                    Timestamp = DateTime.UtcNow,
                    RegionName = "Test Region",
                    SessionId = $"im-{request.SenderId}",
                    SessionName = request.SenderName,
                    TargetId = request.SenderId
                };

                // Process through the chat processing pipeline
                await _chatProcessingService.ProcessChatMessageAsync(imMessage, request.AccountId);

                _logger.LogInformation("Tested IM relay for account {AccountId} with message: {Message}", 
                    request.AccountId, request.Message);

                return Ok(new { 
                    success = true, 
                    message = "IM relay test completed",
                    relayAvatarUuid = account.AvatarRelayUuid,
                    originalMessage = request.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing IM relay for account {AccountId}", request.AccountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Test proximity detection by simulating an avatar approaching
        /// </summary>
        [HttpPost("test-proximity")]
        public async Task<IActionResult> TestProximityRelay([FromBody] TestProximityRequest request)
        {
            try
            {
                // Get the account instance
                var instance = _accountService.GetInstance(request.AccountId);
                if (instance == null)
                {
                    return NotFound("Account instance not found or not connected");
                }

                // Get the account to verify it has relay configured
                var account = await _accountService.GetAccountAsync(request.AccountId);
                if (account == null)
                {
                    return NotFound("Account not found");
                }

                if (string.IsNullOrEmpty(account.AvatarRelayUuid) || account.AvatarRelayUuid == "00000000-0000-0000-0000-000000000000")
                {
                    return BadRequest("Account does not have a valid relay avatar UUID configured");
                }

                // Note: This is a test endpoint - in real usage, proximity detection happens automatically
                // when avatars come within 0.25m during Objects_AvatarUpdate or Grid_CoarseLocationUpdate events

                _logger.LogInformation("Tested proximity detection setup for account {AccountId}", request.AccountId);

                return Ok(new { 
                    success = true, 
                    message = "Proximity detection is configured and ready",
                    relayAvatarUuid = account.AvatarRelayUuid,
                    proximityThreshold = "0.25m",
                    note = "Proximity alerts will be sent automatically when avatars come within range"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing proximity relay for account {AccountId}", request.AccountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get proximity alert status for an account (which avatars are currently tracked)
        /// </summary>
        [HttpGet("proximity-status/{accountId}")]
        public IActionResult GetProximityStatus(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound("Account instance not found or not connected");
                }

                // Use reflection to access the private proximity tracking dictionary
                var instanceType = instance.GetType();
                var proximityField = instanceType.GetField("_proximityAlertedAvatars", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (proximityField?.GetValue(instance) is System.Collections.Concurrent.ConcurrentDictionary<OpenMetaverse.UUID, DateTime> proximityDict)
                {
                    var trackedAvatars = proximityDict.Select(kvp => new
                    {
                        avatarId = kvp.Key.ToString(),
                        alertTime = kvp.Value
                    }).ToList();

                    return Ok(new
                    {
                        accountId = accountId,
                        trackedAvatarCount = trackedAvatars.Count,
                        trackedAvatars = trackedAvatars,
                        message = "These avatars have triggered proximity alerts and won't trigger again until they move away (>1m) and return"
                    });
                }

                return Ok(new
                {
                    accountId = accountId,
                    trackedAvatarCount = 0,
                    trackedAvatars = new object[0],
                    message = "No avatars currently tracked for proximity alerts"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting proximity status for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Clear proximity alert tracking for a specific avatar or all avatars
        /// </summary>
        [HttpPost("clear-proximity/{accountId}")]
        public IActionResult ClearProximityTracking(Guid accountId, [FromBody] ClearProximityRequest? request = null)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound("Account instance not found or not connected");
                }

                // Use reflection to access the private proximity tracking dictionary
                var instanceType = instance.GetType();
                var proximityField = instanceType.GetField("_proximityAlertedAvatars", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (proximityField?.GetValue(instance) is System.Collections.Concurrent.ConcurrentDictionary<OpenMetaverse.UUID, DateTime> proximityDict)
                {
                    var clearedCount = 0;

                    if (request != null && !string.IsNullOrEmpty(request.AvatarId))
                    {
                        // Clear specific avatar
                        if (OpenMetaverse.UUID.TryParse(request.AvatarId, out var avatarUuid))
                        {
                            if (proximityDict.TryRemove(avatarUuid, out _))
                            {
                                clearedCount = 1;
                            }
                        }
                        else
                        {
                            return BadRequest("Invalid avatar UUID format");
                        }
                    }
                    else
                    {
                        // Clear all avatars
                        clearedCount = proximityDict.Count;
                        proximityDict.Clear();
                    }

                    return Ok(new
                    {
                        accountId = accountId,
                        clearedCount = clearedCount,
                        specificAvatar = request?.AvatarId ?? "all",
                        message = $"Cleared proximity tracking for {clearedCount} avatar(s)"
                    });
                }

                return Ok(new
                {
                    accountId = accountId,
                    clearedCount = 0,
                    message = "No proximity tracking to clear"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing proximity tracking for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get current relay configuration for an account
        /// </summary>
        [HttpGet("config/{accountId}")]
        public async Task<IActionResult> GetRelayConfig(Guid accountId)
        {
            try
            {
                var account = await _accountService.GetAccountAsync(accountId);
                if (account == null)
                {
                    return NotFound("Account not found");
                }

                var hasValidRelay = !string.IsNullOrEmpty(account.AvatarRelayUuid) && 
                                   account.AvatarRelayUuid != "00000000-0000-0000-0000-000000000000";

                return Ok(new {
                    accountId = accountId,
                    accountName = $"{account.FirstName} {account.LastName}",
                    avatarUuid = account.AvatarRelayUuid,
                    hasValidRelay = hasValidRelay,
                    isConnected = account.IsConnected,
                    whisperRelayEnabled = hasValidRelay,
                    proximityDetectionEnabled = hasValidRelay,
                    proximityThreshold = "0.25m"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting relay config for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }
    }

    /// <summary>
    /// Request model for testing IM relay
    /// </summary>
    public class TestIMRequest
    {
        public Guid AccountId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string SenderId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request model for testing proximity detection
    /// </summary>
    public class TestProximityRequest
    {
        public Guid AccountId { get; set; }
        public string NearbyAvatarId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request model for clearing proximity tracking
    /// </summary>
    public class ClearProximityRequest
    {
        public string? AvatarId { get; set; }
    }
}
#endif