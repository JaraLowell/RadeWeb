using Microsoft.AspNetCore.Mvc;
using LibreMetaverse;
using LibreMetaverse.Assets;
using RadegastWeb.Core;
using RadegastWeb.Models;
using RadegastWeb.Services;
using System.Text;

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

        [HttpGet("{accountId}/preview/{itemUuid}")]
        public async Task<ActionResult> GetInventoryItemPreview(Guid accountId, string itemUuid)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    return NotFound(new { message = "Account not found or not running" });
                }

                if (!instance.IsConnected)
                {
                    return BadRequest(new { message = "Account is not connected" });
                }

                if (!UUID.TryParse(itemUuid, out var inventoryItemUuid))
                {
                    return BadRequest(new { message = "Invalid item UUID format" });
                }

                var item = instance.Client.Inventory.Store?[inventoryItemUuid] as InventoryItem;
                if (item == null)
                {
                    return NotFound(new { message = "Inventory item is not loaded in cache yet. Refresh inventory and try again." });
                }

                if (item.AssetType == AssetType.Texture || item is InventorySnapshot)
                {
                    if (item.AssetUUID == UUID.Zero)
                    {
                        return BadRequest(new { message = "This image item has no asset UUID." });
                    }

                    var imageUrls = new[]
                    {
                        $"https://secondlife.com/app/image/{item.AssetUUID}/3",
                        $"https://secondlife.com/app/image/{item.AssetUUID}/2",
                        $"https://secondlife.com/app/image/{item.AssetUUID}/1",
                        $"https://picture-service.secondlife.com/{item.AssetUUID}/1024x1024.jpg",
                        $"https://picture-service.secondlife.com/{item.AssetUUID}/512x512.jpg",
                        $"https://picture-service.secondlife.com/{item.AssetUUID}/256x192.jpg"
                    };

                    return Ok(new
                    {
                        kind = "image",
                        name = item.Name,
                        imageUrls,
                        imageUrl = $"https://secondlife.com/app/image/{item.AssetUUID}/1",
                        fallbackImageUrl = $"https://asset-cdn.glb.agni.lindenlab.com/?texture_id={item.AssetUUID}"
                    });
                }

                if (item.AssetType == AssetType.Notecard || item.AssetType == AssetType.LSLText)
                {
                    if (item.AssetUUID == UUID.Zero)
                    {
                        return BadRequest(new { message = "This item has no asset UUID." });
                    }

                    var asset = await RequestInventoryItemAssetAsync(instance, item, TimeSpan.FromSeconds(25))
                               ?? await RequestAssetAsync(instance, item.AssetUUID, item.AssetType, TimeSpan.FromSeconds(20));
                    if (asset == null)
                    {
                        return NotFound(new { message = "Unable to download asset content for this item." });
                    }

                    if (asset is AssetNotecard notecard)
                    {
                        if (!notecard.Decode())
                        {
                            return BadRequest(new { message = "Failed to decode notecard content." });
                        }

                        return Ok(new
                        {
                            kind = "text",
                            textType = "notecard",
                            name = item.Name,
                            content = ExpandEmbeddedNotecardReferences(notecard)
                        });
                    }

                    if (asset is AssetScriptText script)
                    {
                        if (!script.Decode())
                        {
                            return BadRequest(new { message = "Failed to decode script content." });
                        }

                        return Ok(new
                        {
                            kind = "text",
                            textType = "script",
                            name = item.Name,
                            content = script.Source ?? string.Empty
                        });
                    }

                    var fallbackText = asset.AssetData != null
                        ? Encoding.UTF8.GetString(asset.AssetData)
                        : string.Empty;

                    return Ok(new
                    {
                        kind = "text",
                        textType = "plain",
                        name = item.Name,
                        content = fallbackText
                    });
                }

                return BadRequest(new
                {
                    message = $"Preview is currently supported only for textures, snapshots, notecards, and scripts. Item type was {item.AssetType}."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading inventory preview for account {AccountId} item {ItemUuid}", accountId, itemUuid);
                return StatusCode(500, new { message = "Error retrieving item preview", error = ex.Message });
            }
        }

        private static string ExpandEmbeddedNotecardReferences(AssetNotecard notecard)
        {
            var body = notecard.BodyText ?? string.Empty;
            if (body.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(body.Length);
            for (var index = 0; index < body.Length; index++)
            {
                var currentChar = body[index];
                if ((int)currentChar == 0xDBC0 && index + 1 < body.Length)
                {
                    var embeddedIndex = (int)body[++index] - 0xDC00;
                    var embeddedName = (embeddedIndex >= 0 && embeddedIndex < notecard.EmbeddedItems.Count)
                        ? notecard.EmbeddedItems[embeddedIndex].Name
                        : "Unknown embedded item";
                    builder.Append($"[Embedded item: {embeddedName}]");
                    continue;
                }

                builder.Append(currentChar);
            }

            return builder.ToString();
        }

        private static async Task<Asset?> RequestAssetAsync(WebRadegastInstance instance, UUID assetUuid, AssetType assetType, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);

            try
            {
                return await instance.Client.Assets.RequestAssetAsync(assetUuid, assetType, true, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private static async Task<Asset?> RequestInventoryItemAssetAsync(WebRadegastInstance instance, InventoryItem item, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);

            try
            {
                return await instance.Client.Assets.RequestInventoryAssetAsync(item, true, UUID.Random(), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }
}
