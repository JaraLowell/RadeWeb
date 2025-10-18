using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GroupsController : ControllerBase
    {
        private readonly IAccountService _accountService;
        private readonly ILogger<GroupsController> _logger;

        public GroupsController(IAccountService accountService, ILogger<GroupsController> logger)
        {
            _accountService = accountService;
            _logger = logger;
        }

        /// <summary>
        /// Get all groups for the specified account
        /// </summary>
        [HttpGet("{accountId}")]
        public async Task<ActionResult<IEnumerable<GroupDto>>> GetGroups(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound($"Account {accountId} not found or not initialized");
                }

                var groups = await instance.GetGroupsAsync();
                return Ok(groups);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting groups for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get a specific group by ID for the specified account
        /// </summary>
        [HttpGet("{accountId}/group/{groupId}")]
        public async Task<ActionResult<GroupDto>> GetGroup(Guid accountId, string groupId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound($"Account {accountId} not found or not initialized");
                }

                var group = await instance.GetGroupAsync(groupId);
                if (group == null)
                {
                    return NotFound($"Group {groupId} not found");
                }

                return Ok(group);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting group {GroupId} for account {AccountId}", groupId, accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get group name by ID for the specified account
        /// </summary>
        [HttpGet("{accountId}/group/{groupId}/name")]
        public async Task<ActionResult<string>> GetGroupName(Guid accountId, string groupId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound($"Account {accountId} not found or not initialized");
                }

                var groupName = await instance.GetGroupNameAsync(groupId);
                return Ok(groupName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting group name for {GroupId} on account {AccountId}", groupId, accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Set the ignore status for a group
        /// </summary>
        [HttpPut("{accountId}/group/{groupId}/ignore")]
        public async Task<ActionResult> SetGroupIgnoreStatus(Guid accountId, string groupId, [FromBody] SetGroupIgnoreRequest request)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound($"Account {accountId} not found or not initialized");
                }

                await instance.SetGroupIgnoreStatusAsync(groupId, request.IsIgnored);
                
                var statusText = request.IsIgnored ? "ignored" : "unignored";
                _logger.LogInformation("Group {GroupId} {Status} for account {AccountId}", groupId, statusText, accountId);
                
                return Ok(new { success = true, message = $"Group {statusText} successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting ignore status for group {GroupId} on account {AccountId}", groupId, accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Check if a group is ignored
        /// </summary>
        [HttpGet("{accountId}/group/{groupId}/ignored")]
        public async Task<ActionResult<bool>> IsGroupIgnored(Guid accountId, string groupId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound($"Account {accountId} not found or not initialized");
                }

                var isIgnored = await instance.IsGroupIgnoredAsync(groupId);
                return Ok(isIgnored);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking ignore status for group {GroupId} on account {AccountId}", groupId, accountId);
                return StatusCode(500, "Internal server error");
            }
        }
    }

    public class SetGroupIgnoreRequest
    {
        public bool IsIgnored { get; set; }
    }
}