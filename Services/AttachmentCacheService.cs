using System.Xml.Serialization;
using OpenMetaverse;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Interface for attachment caching service
    /// </summary>
    public interface IAttachmentCacheService
    {
        /// <summary>
        /// Save attachments to XML cache file for an account
        /// </summary>
        Task SaveAttachmentsAsync(Guid accountId, List<AttachmentItem> attachments);
        
        /// <summary>
        /// Load attachments from XML cache file for an account
        /// </summary>
        Task<List<AttachmentItem>> LoadAttachmentsAsync(Guid accountId);
        
        /// <summary>
        /// Clear cached attachments for an account
        /// </summary>
        Task ClearAttachmentsAsync(Guid accountId);
        
        /// <summary>
        /// Check if attachments cache exists for an account
        /// </summary>
        bool HasCachedAttachments(Guid accountId);

        /// <summary>
        /// Save inventory tree cache for an account
        /// </summary>
        Task SaveInventoryAsync(Guid accountId, InventoryCacheCollection inventory);

        /// <summary>
        /// Load inventory tree cache for an account
        /// </summary>
        Task<InventoryCacheCollection> LoadInventoryAsync(Guid accountId);

        /// <summary>
        /// Check if inventory cache exists for an account
        /// </summary>
        bool HasCachedInventory(Guid accountId);

        /// <summary>
        /// Update worn state markers in inventory cache using current worn attachments
        /// </summary>
        Task UpdateInventoryWornStateAsync(Guid accountId, List<AttachmentItem> attachments);
    }
    
    /// <summary>
    /// Service for caching attachment information to XML files
    /// Stores attachments per account in data/accounts/{accountId}/cache/attachments.xml
    /// </summary>
    public class AttachmentCacheService : IAttachmentCacheService
    {
        private readonly ILogger<AttachmentCacheService> _logger;
        private readonly string _dataRoot;
        private readonly XmlSerializer _attachmentSerializer;
        private readonly XmlSerializer _inventorySerializer;
        
        public AttachmentCacheService(ILogger<AttachmentCacheService> logger, IConfiguration configuration)
        {
            _logger = logger;
            
            // Get the data root directory
            var contentRoot = configuration.GetValue<string>("ContentRoot") ?? Directory.GetCurrentDirectory();
            _dataRoot = Path.Combine(contentRoot, "data", "accounts");
            
            // Initialize XML serializer
            _attachmentSerializer = new XmlSerializer(typeof(AttachmentCollection));
            _inventorySerializer = new XmlSerializer(typeof(InventoryCacheCollection));
            
            // Ensure the data directory exists
            if (!Directory.Exists(_dataRoot))
            {
                Directory.CreateDirectory(_dataRoot);
            }
        }
        
        public Task SaveAttachmentsAsync(Guid accountId, List<AttachmentItem> attachments)
        {
            try
            {
                var cacheFilePath = GetCacheFilePath(accountId);
                var cacheDir = Path.GetDirectoryName(cacheFilePath);
                
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }
                
                var collection = new AttachmentCollection
                {
                    Items = attachments,
                    LastUpdated = DateTime.UtcNow
                };
                
                using (var fileStream = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var streamWriter = new StreamWriter(fileStream))
                {
                    _attachmentSerializer.Serialize(streamWriter, collection);
                }
                
                _logger.LogDebug("Saved {Count} attachments to cache for account {AccountId}", 
                    attachments.Count, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving attachments cache for account {AccountId}", accountId);
                throw;
            }
            
            return Task.CompletedTask;
        }
        
        public Task<List<AttachmentItem>> LoadAttachmentsAsync(Guid accountId)
        {
            try
            {
                var cacheFilePath = GetCacheFilePath(accountId);
                
                if (!File.Exists(cacheFilePath))
                {
                    _logger.LogDebug("No attachments cache file found for account {AccountId}", accountId);
                    return Task.FromResult(new List<AttachmentItem>());
                }
                
                using (var fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var collection = (AttachmentCollection?)_attachmentSerializer.Deserialize(fileStream);
                    
                    if (collection == null)
                    {
                        _logger.LogWarning("Failed to deserialize attachments cache for account {AccountId}", accountId);
                        return Task.FromResult(new List<AttachmentItem>());
                    }
                    
                    _logger.LogDebug("Loaded {Count} attachments from cache for account {AccountId}", 
                        collection.Items.Count, accountId);
                    
                    return Task.FromResult(collection.Items);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading attachments cache for account {AccountId}", accountId);
                return Task.FromResult(new List<AttachmentItem>());
            }
        }
        
        public Task ClearAttachmentsAsync(Guid accountId)
        {
            try
            {
                var cacheFilePath = GetCacheFilePath(accountId);
                
                if (File.Exists(cacheFilePath))
                {
                    File.Delete(cacheFilePath);
                    _logger.LogDebug("Cleared attachments cache for account {AccountId}", accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing attachments cache for account {AccountId}", accountId);
            }
            
            return Task.CompletedTask;
        }
        
        public bool HasCachedAttachments(Guid accountId)
        {
            var cacheFilePath = GetCacheFilePath(accountId);
            return File.Exists(cacheFilePath);
        }

        public Task SaveInventoryAsync(Guid accountId, InventoryCacheCollection inventory)
        {
            try
            {
                var cacheFilePath = GetInventoryCacheFilePath(accountId);
                var cacheDir = Path.GetDirectoryName(cacheFilePath);

                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                inventory.LastUpdated = DateTime.UtcNow;

                using (var fileStream = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    _inventorySerializer.Serialize(fileStream, inventory);
                }

                _logger.LogDebug("Saved inventory cache for account {AccountId} with {RootCount} root nodes",
                    accountId, inventory.RootNodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving inventory cache for account {AccountId}", accountId);
                throw;
            }

            return Task.CompletedTask;
        }

        public Task<InventoryCacheCollection> LoadInventoryAsync(Guid accountId)
        {
            try
            {
                var cacheFilePath = GetInventoryCacheFilePath(accountId);

                if (!File.Exists(cacheFilePath))
                {
                    _logger.LogDebug("No inventory cache file found for account {AccountId}", accountId);
                    return Task.FromResult(new InventoryCacheCollection());
                }

                using (var fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var collection = (InventoryCacheCollection?)_inventorySerializer.Deserialize(fileStream);
                    if (collection == null)
                    {
                        _logger.LogWarning("Failed to deserialize inventory cache for account {AccountId}", accountId);
                        return Task.FromResult(new InventoryCacheCollection());
                    }

                    return Task.FromResult(collection);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading inventory cache for account {AccountId}", accountId);
                return Task.FromResult(new InventoryCacheCollection());
            }
        }

        public bool HasCachedInventory(Guid accountId)
        {
            var cacheFilePath = GetInventoryCacheFilePath(accountId);
            return File.Exists(cacheFilePath);
        }

        public async Task UpdateInventoryWornStateAsync(Guid accountId, List<AttachmentItem> attachments)
        {
            var inventory = await LoadInventoryAsync(accountId);
            if (inventory.RootNodes.Count == 0)
            {
                return;
            }

            var wornByItemId = attachments
                .Where(a => !string.IsNullOrWhiteSpace(a.Uuid))
                .GroupBy(a => a.Uuid)
                .ToDictionary(g => g.Key, g => g.First());

            ApplyWornState(inventory.RootNodes, wornByItemId);
            await SaveInventoryAsync(accountId, inventory);
        }

        private static void ApplyWornState(List<InventoryCacheNode> nodes, Dictionary<string, AttachmentItem> wornByItemId)
        {
            foreach (var node in nodes)
            {
                if (!node.IsFolder)
                {
                    if (wornByItemId.TryGetValue(node.Uuid, out var attachment))
                    {
                        node.IsWorn = true;
                        node.WornAttachmentPoint = attachment.AttachmentPoint;
                    }
                    else
                    {
                        node.IsWorn = false;
                        node.WornAttachmentPoint = string.Empty;
                    }
                }

                if (node.Children.Count > 0)
                {
                    ApplyWornState(node.Children, wornByItemId);
                }
            }
        }
        
        private string GetCacheFilePath(Guid accountId)
        {
            return Path.Combine(_dataRoot, accountId.ToString(), "cache", "attachments.xml");
        }

        private string GetInventoryCacheFilePath(Guid accountId)
        {
            return Path.Combine(_dataRoot, accountId.ToString(), "cache", "inventory.xml");
        }
    }
}
