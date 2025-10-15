using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;
using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Controllers
{
    /// <summary>
    /// Controller for managing Corrade plugin configuration and commands
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class CorradeController : ControllerBase
    {
        private readonly ILogger<CorradeController> _logger;
        private readonly ICorradeService _corradeService;
        private readonly IAuthenticationService _authService;

        public CorradeController(
            ILogger<CorradeController> logger,
            ICorradeService corradeService,
            IAuthenticationService authService)
        {
            _logger = logger;
            _corradeService = corradeService;
            _authService = authService;
        }

        /// <summary>
        /// Get current Corrade configuration
        /// </summary>
        [HttpGet("config")]
        public async Task<ActionResult<CorradeConfig>> GetConfiguration()
        {
            try
            {
                // Check authentication
                if (!_authService.ValidateHttpContext(HttpContext))
                {
                    return Unauthorized("Authentication required");
                }

                var config = await _corradeService.LoadConfigurationAsync();
                
                // Remove passwords from response for security
                var safeConfig = new CorradeConfig
                {
                    Groups = config.Groups.Select(g => new CorradeGroup
                    {
                        GroupUuid = g.GroupUuid,
                        GroupName = g.GroupName,
                        AllowLocalChat = g.AllowLocalChat,
                        AllowGroupRelay = g.AllowGroupRelay,
                        AllowAvatarIM = g.AllowAvatarIM,
                        Password = "***" // Hide password
                    }).ToList()
                };

                return Ok(safeConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Corrade configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Update Corrade configuration
        /// </summary>
        [HttpPost("config")]
        public async Task<ActionResult> UpdateConfiguration([FromBody] CorradeConfig config)
        {
            try
            {
                // Check authentication
                if (!_authService.ValidateHttpContext(HttpContext))
                {
                    return Unauthorized("Authentication required");
                }

                // Validate configuration
                if (config.Groups == null)
                {
                    return BadRequest("Groups configuration is required");
                }

                // Validate each group
                foreach (var group in config.Groups)
                {
                    if (string.IsNullOrWhiteSpace(group.GroupUuid))
                    {
                        return BadRequest("Group UUID is required for all groups");
                    }

                    if (string.IsNullOrWhiteSpace(group.Password))
                    {
                        return BadRequest("Password is required for all groups");
                    }

                    // Validate UUID format
                    if (!System.Guid.TryParse(group.GroupUuid, out _))
                    {
                        return BadRequest($"Invalid UUID format for group: {group.GroupUuid}");
                    }
                }

                await _corradeService.SaveConfigurationAsync(config);
                
                _logger.LogInformation("Updated Corrade configuration with {GroupCount} groups", config.Groups.Count);
                return Ok(new { message = "Configuration updated successfully", groupCount = config.Groups.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Corrade configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Add a new group to Corrade configuration
        /// </summary>
        [HttpPost("config/groups")]
        public async Task<ActionResult> AddGroup([FromBody] CorradeGroup group)
        {
            try
            {
                // Check authentication
                if (!_authService.ValidateHttpContext(HttpContext))
                {
                    return Unauthorized("Authentication required");
                }

                // Validate group
                if (string.IsNullOrWhiteSpace(group.GroupUuid))
                {
                    return BadRequest("Group UUID is required");
                }

                if (string.IsNullOrWhiteSpace(group.Password))
                {
                    return BadRequest("Password is required");
                }

                if (!System.Guid.TryParse(group.GroupUuid, out _))
                {
                    return BadRequest("Invalid UUID format");
                }

                var config = await _corradeService.LoadConfigurationAsync();

                // Check if group already exists
                if (config.Groups.Any(g => g.GroupUuid.Equals(group.GroupUuid, StringComparison.OrdinalIgnoreCase)))
                {
                    return Conflict("Group already exists in configuration");
                }

                config.Groups.Add(group);
                await _corradeService.SaveConfigurationAsync(config);

                _logger.LogInformation("Added new group {GroupUuid} to Corrade configuration", group.GroupUuid);
                return Ok(new { message = "Group added successfully", groupUuid = group.GroupUuid });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding group to Corrade configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Remove a group from Corrade configuration
        /// </summary>
        [HttpDelete("config/groups/{groupUuid}")]
        public async Task<ActionResult> RemoveGroup(string groupUuid)
        {
            try
            {
                // Check authentication
                if (!_authService.ValidateHttpContext(HttpContext))
                {
                    return Unauthorized("Authentication required");
                }

                if (string.IsNullOrWhiteSpace(groupUuid))
                {
                    return BadRequest("Group UUID is required");
                }

                var config = await _corradeService.LoadConfigurationAsync();

                var groupToRemove = config.Groups.FirstOrDefault(g => 
                    g.GroupUuid.Equals(groupUuid, StringComparison.OrdinalIgnoreCase));

                if (groupToRemove == null)
                {
                    return NotFound("Group not found in configuration");
                }

                config.Groups.Remove(groupToRemove);
                await _corradeService.SaveConfigurationAsync(config);

                _logger.LogInformation("Removed group {GroupUuid} from Corrade configuration", groupUuid);
                return Ok(new { message = "Group removed successfully", groupUuid = groupUuid });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing group from Corrade configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Test a Corrade command without executing it
        /// </summary>
        [HttpPost("test-command")]
        public async Task<ActionResult<CorradeCommandResult>> TestCommand([FromBody] TestCommandRequest request)
        {
            try
            {
                // Check authentication
                if (!_authService.ValidateHttpContext(HttpContext))
                {
                    return Unauthorized("Authentication required");
                }

                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest("Message is required");
                }

                // Only validate the command parsing, don't execute it
                var isCommand = _corradeService.IsWhisperCorradeCommand(request.Message);
                
                if (!isCommand)
                {
                    return Ok(new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Not a valid Corrade command",
                        ErrorCode = "NOT_CORRADE_COMMAND"
                    });
                }

                // For testing, we'll use a dummy account ID and sender
                var testAccountId = Guid.NewGuid();
                var testSenderId = "00000000-0000-0000-0000-000000000000";
                var testSenderName = "Test User";

                // This will parse and validate but not execute the command
                var result = await _corradeService.ProcessWhisperCommandAsync(testAccountId, testSenderId, testSenderName, request.Message);
                
                // Override execution errors for test mode
                if (result.ErrorCode == "ACCOUNT_OFFLINE")
                {
                    result.Success = true;
                    result.Message = "Command parsed successfully (test mode)";
                    result.ErrorCode = null;
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing Corrade command");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get Corrade plugin status and statistics
        /// </summary>
        [HttpGet("status")]
        public async Task<ActionResult> GetStatus()
        {
            try
            {
                // Check authentication
                if (!_authService.ValidateHttpContext(HttpContext))
                {
                    return Unauthorized("Authentication required");
                }

                var config = await _corradeService.LoadConfigurationAsync();
                
                var status = new
                {
                    IsEnabled = _corradeService.IsEnabled,
                    GroupCount = config.Groups.Count,
                    Groups = config.Groups.Select(g => new
                    {
                        GroupUuid = g.GroupUuid,
                        GroupName = g.GroupName ?? "Unknown",
                        AllowLocalChat = g.AllowLocalChat,
                        AllowGroupRelay = g.AllowGroupRelay,
                        AllowAvatarIM = g.AllowAvatarIM
                    }),
                    LastUpdated = System.IO.File.Exists(Path.Combine("data", "corrade.json")) 
                        ? System.IO.File.GetLastWriteTimeUtc(Path.Combine("data", "corrade.json"))
                        : (DateTime?)null
                };

                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Corrade status");
                return StatusCode(500, "Internal server error");
            }
        }
    }

    /// <summary>
    /// Request model for testing Corrade commands
    /// </summary>
    public class TestCommandRequest
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }
}