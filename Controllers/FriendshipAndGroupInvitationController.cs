using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FriendshipController : ControllerBase
    {
        private readonly IFriendshipRequestService _friendshipRequestService;
        private readonly ILogger<FriendshipController> _logger;

        public FriendshipController(IFriendshipRequestService friendshipRequestService, ILogger<FriendshipController> logger)
        {
            _friendshipRequestService = friendshipRequestService;
            _logger = logger;
        }

        /// <summary>
        /// Get active friendship requests for an account
        /// </summary>
        [HttpGet("{accountId}/requests")]
        public async Task<ActionResult<IEnumerable<FriendshipRequestDto>>> GetActiveFriendshipRequests(Guid accountId)
        {
            try
            {
                var requests = await _friendshipRequestService.GetActiveFriendshipRequestsAsync(accountId);
                return Ok(requests);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active friendship requests for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Respond to a friendship request
        /// </summary>
        [HttpPost("respond")]
        public async Task<ActionResult> RespondToFriendshipRequest([FromBody] FriendshipRequestResponseRequest request)
        {
            try
            {
                if (request.AccountId == Guid.Empty || string.IsNullOrEmpty(request.RequestId))
                {
                    return BadRequest("AccountId and RequestId are required");
                }

                var success = await _friendshipRequestService.RespondToFriendshipRequestAsync(request);
                
                if (!success)
                {
                    return BadRequest("Failed to respond to friendship request. Request may not exist or may have already been responded to.");
                }

                _logger.LogInformation("Friendship request {RequestId} responded to ({Accept}) for account {AccountId}", 
                    request.RequestId, request.Accept ? "Accept" : "Decline", request.AccountId);
                
                return Ok(new { success = true, message = request.Accept ? "Friendship accepted" : "Friendship declined" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to friendship request {RequestId} for account {AccountId}", 
                    request.RequestId, request.AccountId);
                return StatusCode(500, "Internal server error");
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class GroupInvitationController : ControllerBase
    {
        private readonly IGroupInvitationService _groupInvitationService;
        private readonly ILogger<GroupInvitationController> _logger;

        public GroupInvitationController(IGroupInvitationService groupInvitationService, ILogger<GroupInvitationController> logger)
        {
            _groupInvitationService = groupInvitationService;
            _logger = logger;
        }

        /// <summary>
        /// Get active group invitations for an account
        /// </summary>
        [HttpGet("{accountId}/invitations")]
        public async Task<ActionResult<IEnumerable<GroupInvitationDto>>> GetActiveGroupInvitations(Guid accountId)
        {
            try
            {
                var invitations = await _groupInvitationService.GetActiveGroupInvitationsAsync(accountId);
                return Ok(invitations);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active group invitations for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Respond to a group invitation
        /// </summary>
        [HttpPost("respond")]
        public async Task<ActionResult> RespondToGroupInvitation([FromBody] GroupInvitationResponseRequest request)
        {
            try
            {
                if (request.AccountId == Guid.Empty || string.IsNullOrEmpty(request.InvitationId))
                {
                    return BadRequest("AccountId and InvitationId are required");
                }

                var success = await _groupInvitationService.RespondToGroupInvitationAsync(request);
                
                if (!success)
                {
                    return BadRequest("Failed to respond to group invitation. Invitation may not exist or may have already been responded to.");
                }

                _logger.LogInformation("Group invitation {InvitationId} responded to ({Accept}) for account {AccountId}", 
                    request.InvitationId, request.Accept ? "Accept" : "Decline", request.AccountId);
                
                return Ok(new { success = true, message = request.Accept ? "Group invitation accepted" : "Group invitation declined" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to group invitation {InvitationId} for account {AccountId}", 
                    request.InvitationId, request.AccountId);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}