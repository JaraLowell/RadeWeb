using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TeleportController : ControllerBase
    {
        private readonly ILogger<TeleportController> _logger;
        private readonly IAccountService _accountService;
        private readonly ITeleportRequestService _teleportRequestService;

        public TeleportController(ILogger<TeleportController> logger, IAccountService accountService, ITeleportRequestService teleportRequestService)
        {
            _logger = logger;
            _accountService = accountService;
            _teleportRequestService = teleportRequestService;
        }

        /// <summary>
        /// Get active teleport requests for an account
        /// </summary>
        [HttpGet("requests/{accountId:guid}")]
        public async Task<ActionResult<IEnumerable<TeleportRequestDto>>> GetTeleportRequests(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound($"Account {accountId} not found");
                }

                var requests = await _teleportRequestService.GetActiveTeleportRequestsAsync(accountId);
                return Ok(requests);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting teleport requests for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Respond to a teleport request
        /// </summary>
        [HttpPost("respond")]
        public async Task<ActionResult> RespondToTeleportRequest([FromBody] TeleportRequestResponseRequest request)
        {
            try
            {
                if (request.AccountId == Guid.Empty || string.IsNullOrEmpty(request.RequestId))
                {
                    return BadRequest("AccountId and RequestId are required");
                }

                var success = await _teleportRequestService.RespondToTeleportRequestAsync(request);
                
                if (!success)
                {
                    return BadRequest("Failed to respond to teleport request. Request may not exist or may have already been responded to.");
                }

                _logger.LogInformation("Teleport request {RequestId} responded to ({Accept}) for account {AccountId}", 
                    request.RequestId, request.Accept ? "Accept" : "Decline", request.AccountId);
                
                return Ok(new { success = true, message = request.Accept ? "Teleport accepted" : "Teleport declined" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to teleport request {RequestId} for account {AccountId}", 
                    request.RequestId, request.AccountId);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}