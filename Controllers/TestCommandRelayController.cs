using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Services;
using RadegastWeb.Models;

namespace RadegastWeb.Controllers
{
    /// <summary>
    /// Controller for testing avatar command relay functionality via IM messages
    /// </summary>
    [ApiController]
    [Route("api/test/command-relay")]
    public class TestCommandRelayController : ControllerBase
    {
        private readonly ILogger<TestCommandRelayController> _logger;
        private readonly IAccountService _accountService;
        private readonly IChatProcessingService _chatProcessingService;

        public TestCommandRelayController(
            ILogger<TestCommandRelayController> logger,
            IAccountService accountService,
            IChatProcessingService chatProcessingService)
        {
            _logger = logger;
            _accountService = accountService;
            _chatProcessingService = chatProcessingService;
        }

        /// <summary>
        /// Test command relay by simulating an IM command from the relay avatar
        /// </summary>
        [HttpPost("test-command")]
        public async Task<IActionResult> TestCommandRelay([FromBody] TestRelayCommandRequest request)
        {
            try
            {
                // Get the account to verify it exists and has relay configured
                var account = await _accountService.GetAccountAsync(request.AccountId);
                if (account == null)
                {
                    return NotFound("Account not found");
                }

                if (string.IsNullOrEmpty(account.AvatarRelayUuid) || 
                    account.AvatarRelayUuid == "00000000-0000-0000-0000-000000000000")
                {
                    return BadRequest("Account does not have a valid relay avatar UUID configured");
                }

                // Create a simulated IM message from the relay avatar
                var imMessage = new ChatMessageDto
                {
                    AccountId = request.AccountId,
                    SenderName = request.SenderName ?? "Test Relay Avatar",
                    SenderId = account.AvatarRelayUuid, // Send from the configured relay avatar
                    Message = request.Command,
                    ChatType = "IM",
                    Channel = "IM",
                    Timestamp = DateTime.UtcNow,
                    RegionName = "Test Region",
                    SessionId = $"im-{account.AvatarRelayUuid}",
                    SessionName = request.SenderName ?? "Test Relay Avatar",
                    TargetId = account.AvatarRelayUuid
                };

                // Process through the chat processing pipeline
                await _chatProcessingService.ProcessChatMessageAsync(imMessage, request.AccountId);

                _logger.LogInformation("Tested command relay for account {AccountId} with command: {Command}", 
                    request.AccountId, request.Command);

                return Ok(new { 
                    success = true, 
                    message = "Command relay test completed",
                    relayAvatarUuid = account.AvatarRelayUuid,
                    processedCommand = request.Command,
                    note = "Check the logs and account activity to see if the command was processed correctly"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing command relay for account {AccountId}", request.AccountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get command relay configuration and status for an account
        /// </summary>
        [HttpGet("config/{accountId}")]
        public async Task<IActionResult> GetCommandRelayConfig(Guid accountId)
        {
            try
            {
                var account = await _accountService.GetAccountAsync(accountId);
                if (account == null)
                {
                    return NotFound("Account not found");
                }

                var instance = _accountService.GetInstance(accountId);
                var isConnected = instance?.IsConnected ?? false;

                var hasValidRelay = !string.IsNullOrEmpty(account.AvatarRelayUuid) && 
                                   account.AvatarRelayUuid != "00000000-0000-0000-0000-000000000000";

                return Ok(new {
                    accountId = accountId,
                    accountName = $"{account.FirstName} {account.LastName}",
                    avatarRelayUuid = account.AvatarRelayUuid,
                    hasValidRelay = hasValidRelay,
                    isConnected = isConnected,
                    commandRelayEnabled = hasValidRelay && isConnected,
                    supportedCommands = new[]
                    {
                        "//sit <uuid> - Sit on object with specified UUID",
                        "//stand - Stand up from current position",
                        "//touch <uuid> - Touch object with specified UUID",
                        "//say <message> - Say message in local chat",
                        "//im <uuid> <message> - Send instant message to avatar"
                    },
                    testEndpoint = "/api/test/command-relay/test-command"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting command relay config for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// List all available test commands with examples
        /// </summary>
        [HttpGet("commands")]
        public IActionResult GetAvailableCommands()
        {
            return Ok(new
            {
                commands = new[]
                {
                    new
                    {
                        command = "//sit",
                        syntax = "//sit <uuid>",
                        description = "Make the avatar sit on the specified object",
                        example = "//sit 12345678-1234-1234-1234-123456789abc"
                    },
                    new
                    {
                        command = "//stand",
                        syntax = "//stand",
                        description = "Make the avatar stand up from current position",
                        example = "//stand"
                    },
                    new
                    {
                        command = "//touch",
                        syntax = "//touch <uuid>",
                        description = "Touch the specified object to trigger dialogs or scripts",
                        example = "//touch 12345678-1234-1234-1234-123456789abc"
                    },
                    new
                    {
                        command = "//say",
                        syntax = "//say <message>",
                        description = "Make the avatar say a message in local chat",
                        example = "//say Hello everyone!"
                    },
                    new
                    {
                        command = "//im",
                        syntax = "//im <uuid> <message>",
                        description = "Send an instant message to the specified avatar",
                        example = "//im 12345678-1234-1234-1234-123456789abc Hello there!"
                    }
                },
                notes = new[]
                {
                    "Commands must be sent as IMs from the configured AvatarRelayUuid",
                    "The account must be connected and have a valid AvatarRelayUuid configured",
                    "Feedback messages will be sent back to the relay avatar via IM",
                    "Commands are case-insensitive but must start with '//' prefix"
                }
            });
        }
    }

    /// <summary>
    /// Request model for testing command relay
    /// </summary>
    public class TestRelayCommandRequest
    {
        public Guid AccountId { get; set; }
        public string Command { get; set; } = string.Empty;
        public string? SenderName { get; set; }
    }
}