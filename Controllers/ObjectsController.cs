using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Services;
using OpenMetaverse;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ObjectsController : ControllerBase
    {
        private readonly IAccountService _accountService;
        private readonly ILogger<ObjectsController> _logger;

        public ObjectsController(IAccountService accountService, ILogger<ObjectsController> logger)
        {
            _accountService = accountService;
            _logger = logger;
        }

        /// <summary>
        /// Touch an object in Second Life by UUID
        /// This is useful for re-triggering dialogs from seated objects
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <param name="objectId">The UUID of the object to touch</param>
        [HttpPost("{accountId}/touch/{objectId}")]
        public IActionResult TouchObject(Guid accountId, string objectId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null || !instance.IsConnected)
                {
                    return BadRequest(new { message = "Account not connected" });
                }

                if (!UUID.TryParse(objectId, out UUID objectUuid))
                {
                    return BadRequest(new { message = "Invalid object UUID" });
                }

                var success = instance.TouchObject(objectUuid);
                
                if (success)
                {
                    return Ok(new { message = "Object touched successfully", objectId = objectId });
                }
                else
                {
                    return BadRequest(new { message = "Failed to touch object - object may not be in range or may not exist" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error touching object {ObjectId} for account {AccountId}", objectId, accountId);
                return StatusCode(500, new { message = "Internal server error", error = ex.Message });
            }
        }

        /// <summary>
        /// Touch the object the avatar is currently sitting on
        /// Convenience endpoint for seated avatars to re-trigger dialogs
        /// </summary>
        /// <param name="accountId">The account ID</param>
        [HttpPost("{accountId}/touch-seated")]
        public IActionResult TouchSeatedObject(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null || !instance.IsConnected)
                {
                    return BadRequest(new { message = "Account not connected" });
                }

                if (!instance.IsSitting || instance.SittingOnLocalId == 0)
                {
                    return BadRequest(new { message = "Avatar is not sitting on an object" });
                }

                var objectUuid = instance.CurrentSittingObjectUuid;
                if (!objectUuid.HasValue)
                {
                    return BadRequest(new { message = "Could not determine the UUID of the seated object" });
                }

                var success = instance.TouchObject(objectUuid.Value);
                
                if (success)
                {
                    return Ok(new { message = "Seated object touched successfully", objectId = objectUuid.Value.ToString() });
                }
                else
                {
                    return BadRequest(new { message = "Failed to touch seated object" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error touching seated object for account {AccountId}", accountId);
                return StatusCode(500, new { message = "Internal server error", error = ex.Message });
            }
        }
    }
}
