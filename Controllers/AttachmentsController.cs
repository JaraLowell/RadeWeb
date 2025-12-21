using Microsoft.AspNetCore.Mvc;
using OpenMetaverse;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    /// <summary>
    /// Controller for managing avatar attachments
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AttachmentsController : ControllerBase
    {
        private readonly ILogger<AttachmentsController> _logger;
        private readonly IAccountService _accountService;

        public AttachmentsController(
            ILogger<AttachmentsController> logger,
            IAccountService accountService)
        {
            _logger = logger;
            _accountService = accountService;
        }

        /// <summary>
        /// Get cached attachments for an account
        /// Returns the list of worn attachments from the XML cache
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <returns>List of attachments</returns>
        [HttpGet("{accountId}")]
        public async Task<ActionResult<List<AttachmentDto>>> GetAttachments(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    _logger.LogWarning("Attachments requested for non-existent account {AccountId}", accountId);
                    return NotFound(new { message = "Account not found or not running" });
                }

                var attachments = await instance.GetCachedAttachmentsAsync();
                
                // Convert to DTOs
                var attachmentDtos = attachments.Select(a => new AttachmentDto
                {
                    Uuid = a.Uuid,
                    Name = a.Name,
                    AttachmentPoint = a.AttachmentPoint,
                    IsTouchable = a.IsTouchable,
                    PrimUuid = a.PrimUuid
                }).ToList();

                _logger.LogDebug("Retrieved {Count} attachments for account {AccountId}", 
                    attachmentDtos.Count, accountId);

                return Ok(attachmentDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting attachments for account {AccountId}", accountId);
                return StatusCode(500, new { message = "Error retrieving attachments", error = ex.Message });
            }
        }

        /// <summary>
        /// Touch an attachment by its primitive UUID
        /// This simulates clicking on the attachment
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="primUuid">Primitive UUID of the attachment to touch</param>
        /// <returns>Result of the touch operation</returns>
        [HttpPost("{accountId}/touch/{primUuid}")]
        public ActionResult TouchAttachment(Guid accountId, string primUuid)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    _logger.LogWarning("Touch attachment requested for non-existent account {AccountId}", accountId);
                    return NotFound(new { message = "Account not found or not running" });
                }

                if (!instance.IsConnected)
                {
                    return BadRequest(new { message = "Account is not connected" });
                }

                if (!UUID.TryParse(primUuid, out var objectUuid))
                {
                    return BadRequest(new { message = "Invalid UUID format" });
                }

                // Use the existing TouchObject method
                bool success = instance.TouchObject(objectUuid);

                if (success)
                {
                    _logger.LogInformation("Successfully touched attachment {PrimUuid} for account {AccountId}", 
                        primUuid, accountId);
                    return Ok(new { message = "Attachment touched successfully", primUuid });
                }
                else
                {
                    _logger.LogWarning("Failed to touch attachment {PrimUuid} for account {AccountId}", 
                        primUuid, accountId);
                    return BadRequest(new { message = "Failed to touch attachment - object may not be touchable or not found" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error touching attachment {PrimUuid} for account {AccountId}", 
                    primUuid, accountId);
                return StatusCode(500, new { message = "Error touching attachment", error = ex.Message });
            }
        }

        /// <summary>
        /// Refresh the attachment cache for an account
        /// This will re-scan worn attachments and update the cache
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <returns>Result of the refresh operation</returns>
        [HttpPost("{accountId}/refresh")]
        public async Task<ActionResult> RefreshAttachments(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    _logger.LogWarning("Refresh attachments requested for non-existent account {AccountId}", accountId);
                    return NotFound(new { message = "Account not found or not running" });
                }

                if (!instance.IsConnected)
                {
                    return BadRequest(new { message = "Account is not connected" });
                }

                await instance.UpdateAttachmentCacheAsync();

                _logger.LogInformation("Successfully refreshed attachments cache for account {AccountId}", accountId);
                return Ok(new { message = "Attachments cache refreshed successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing attachments for account {AccountId}", accountId);
                return StatusCode(500, new { message = "Error refreshing attachments", error = ex.Message });
            }
        }
    }
}
