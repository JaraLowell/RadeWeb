using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class InventoryController : ControllerBase
    {
        private readonly ILogger<InventoryController> _logger;
        private readonly IAccountService _accountService;
        private readonly IAttachmentCacheService _attachmentCacheService;

        public InventoryController(
            ILogger<InventoryController> logger,
            IAccountService accountService,
            IAttachmentCacheService attachmentCacheService)
        {
            _logger = logger;
            _accountService = accountService;
            _attachmentCacheService = attachmentCacheService;
        }

        [HttpGet("{accountId}")]
        public async Task<ActionResult<InventoryCacheCollection>> GetInventory(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                InventoryCacheCollection inventory;

                if (instance != null)
                {
                    inventory = await instance.GetCachedInventoryAsync();
                }
                else
                {
                    inventory = await _attachmentCacheService.LoadInventoryAsync(accountId);
                }

                return Ok(inventory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting inventory for account {AccountId}", accountId);
                return StatusCode(500, new { message = "Error retrieving inventory", error = ex.Message });
            }
        }
    }
}
