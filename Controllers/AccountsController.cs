using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AccountsController : ControllerBase
    {
        private readonly IAccountService _accountService;
        private readonly IPresenceService _presenceService;
        private readonly ILogger<AccountsController> _logger;

        public AccountsController(IAccountService accountService, IPresenceService presenceService, ILogger<AccountsController> logger)
        {
            _accountService = accountService;
            _presenceService = presenceService;
            _logger = logger;
        }

        /// <summary>
        /// Get all accounts
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<AccountStatus>>> GetAccounts()
        {
            try
            {
                var accounts = await _accountService.GetAccountStatusesAsync();
                return Ok(accounts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving accounts");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get a specific account
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<Account>> GetAccount(Guid id)
        {
            try
            {
                var account = await _accountService.GetAccountAsync(id);
                if (account == null)
                {
                    return NotFound();
                }
                return Ok(account);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Create a new account
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<Account>> CreateAccount(Account account)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var createdAccount = await _accountService.CreateAccountAsync(account);
                return CreatedAtAction(nameof(GetAccount), new { id = createdAccount.Id }, createdAccount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating account");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Delete an account
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAccount(Guid id)
        {
            try
            {
                var result = await _accountService.DeleteAccountAsync(id);
                if (!result)
                {
                    return NotFound();
                }
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Login an account
        /// </summary>
        [HttpPost("{id}/login")]
        public async Task<IActionResult> LoginAccount(Guid id)
        {
            try
            {
                var result = await _accountService.LoginAccountAsync(id);
                if (!result)
                {
                    return BadRequest("Login failed");
                }
                return Ok(new { message = "Login successful" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging in account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Logout an account
        /// </summary>
        [HttpPost("{id}/logout")]
        public async Task<IActionResult> LogoutAccount(Guid id)
        {
            try
            {
                var result = await _accountService.LogoutAccountAsync(id);
                if (!result)
                {
                    return BadRequest("Logout failed");
                }
                return Ok(new { message = "Logout successful" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging out account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Send chat message
        /// </summary>
        [HttpPost("{id}/chat")]
        public async Task<IActionResult> SendChat(Guid id, [FromBody] SendChatRequest request)
        {
            try
            {
                request.AccountId = id; // Ensure the account ID matches the route
                
                var result = await _accountService.SendChatAsync(
                    request.AccountId, 
                    request.Message, 
                    request.ChatType, 
                    request.Channel);
                
                if (!result)
                {
                    return BadRequest("Failed to send chat message");
                }
                
                return Ok(new { message = "Chat sent successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending chat for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Send instant message
        /// </summary>
        [HttpPost("{id}/im")]
        public async Task<IActionResult> SendIM(Guid id, [FromBody] SendIMRequest request)
        {
            try
            {
                var result = await _accountService.SendIMAsync(id, request.TargetId, request.Message);
                
                if (!result)
                {
                    return BadRequest("Failed to send instant message");
                }
                
                return Ok(new { message = "IM sent successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending IM for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get nearby avatars
        /// </summary>
        [HttpGet("{id}/avatars")]
        public async Task<ActionResult<List<AvatarDto>>> GetNearbyAvatars(Guid id)
        {
            try
            {
                var avatars = await _accountService.GetNearbyAvatarsAsync(id);
                return Ok(avatars);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting nearby avatars for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get chat sessions
        /// </summary>
        [HttpGet("{id}/sessions")]
        public async Task<ActionResult<List<ChatSessionDto>>> GetChatSessions(Guid id)
        {
            try
            {
                var sessions = await _accountService.GetChatSessionsAsync(id);
                return Ok(sessions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat sessions for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get cached display names for an account
        /// </summary>
        [HttpGet("{id}/display-names")]
        public async Task<ActionResult<List<DisplayName>>> GetCachedDisplayNames(Guid id)
        {
            try
            {
                var displayNames = await _accountService.GetCachedDisplayNamesAsync(id);
                return Ok(displayNames);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cached display names for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get display name for a specific avatar
        /// </summary>
        [HttpGet("{id}/display-names/{avatarId}")]
        public async Task<ActionResult<string>> GetDisplayName(Guid id, string avatarId, [FromQuery] NameDisplayMode mode = NameDisplayMode.Smart)
        {
            try
            {
                var displayName = await _accountService.GetDisplayNameAsync(id, avatarId, mode);
                return Ok(new { avatarId, displayName, mode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting display name for avatar {AvatarId} on account {AccountId}", avatarId, id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Set account away status
        /// </summary>
        [HttpPost("{id}/presence/away")]
        public async Task<IActionResult> SetAwayStatus(Guid id, [FromBody] SetPresenceRequest request)
        {
            try
            {
                await _presenceService.SetAwayAsync(id, request.IsEnabled);
                return Ok(new { message = $"Away status {(request.IsEnabled ? "enabled" : "disabled")}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting away status for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Set account busy status
        /// </summary>
        [HttpPost("{id}/presence/busy")]
        public async Task<IActionResult> SetBusyStatus(Guid id, [FromBody] SetPresenceRequest request)
        {
            try
            {
                await _presenceService.SetBusyAsync(id, request.IsEnabled);
                return Ok(new { message = $"Busy status {(request.IsEnabled ? "enabled" : "disabled")}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting busy status for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Set active account (switches away from other accounts)
        /// </summary>
        [HttpPost("{id}/presence/active")]
        public async Task<IActionResult> SetActiveAccount(Guid id)
        {
            try
            {
                await _presenceService.SetActiveAccountAsync(id);
                return Ok(new { message = "Active account set", accountId = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting active account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get account presence status
        /// </summary>
        [HttpGet("{id}/presence")]
        public ActionResult<object> GetPresenceStatus(Guid id)
        {
            try
            {
                var status = _presenceService.GetAccountStatus(id);
                return Ok(new { accountId = id, status = status.ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting presence status for account {AccountId}", id);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}