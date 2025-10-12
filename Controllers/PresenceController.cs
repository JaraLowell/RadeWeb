using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PresenceController : ControllerBase
    {
        private readonly IPresenceService _presenceService;
        private readonly ILogger<PresenceController> _logger;

        public PresenceController(IPresenceService presenceService, ILogger<PresenceController> logger)
        {
            _presenceService = presenceService;
            _logger = logger;
        }

        /// <summary>
        /// Handle browser close event - sets all accounts to away
        /// </summary>
        [HttpPost("browser-close")]
        public async Task<IActionResult> HandleBrowserClose()
        {
            try
            {
                await _presenceService.HandleBrowserCloseAsync();
                return Ok(new { message = "Browser close handled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling browser close");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Handle browser return event - updates account statuses
        /// </summary>
        [HttpPost("browser-return")]
        public async Task<IActionResult> HandleBrowserReturn()
        {
            try
            {
                await _presenceService.HandleBrowserReturnAsync();
                return Ok(new { message = "Browser return handled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling browser return");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Set active account globally
        /// </summary>
        [HttpPost("active-account")]
        public async Task<IActionResult> SetActiveAccount([FromBody] SetActiveAccountRequest request)
        {
            try
            {
                await _presenceService.SetActiveAccountAsync(request.AccountId);
                return Ok(new { message = "Active account set", accountId = request.AccountId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting active account");
                return StatusCode(500, "Internal server error");
            }
        }
    }

    public class SetActiveAccountRequest
    {
        public Guid? AccountId { get; set; }
    }
}