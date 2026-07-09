using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FriendsController : ControllerBase
    {
        private readonly ILogger<FriendsController> _logger;
        private readonly IAccountService _accountService;

        public FriendsController(
            ILogger<FriendsController> logger,
            IAccountService accountService)
        {
            _logger = logger;
            _accountService = accountService;
        }

        [HttpGet("{accountId}")]
        public async Task<ActionResult<IEnumerable<FriendDto>>> GetFriends(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return Ok(Enumerable.Empty<FriendDto>());
                }

                var friends = await instance.GetFriendsAsync();
                return Ok(friends);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting friends for account {AccountId}", accountId);
                return StatusCode(500, new { message = "Error retrieving friends", error = ex.Message });
            }
        }
    }
}
