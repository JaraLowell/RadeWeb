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
    }
    
    /// <summary>
    /// Service for caching attachment information to XML files
    /// Stores attachments per account in data/accounts/{accountId}/cache/attachments.xml
    /// </summary>
    public class AttachmentCacheService : IAttachmentCacheService
    {
        private readonly ILogger<AttachmentCacheService> _logger;
        private readonly string _dataRoot;
        private readonly XmlSerializer _serializer;
        
        public AttachmentCacheService(ILogger<AttachmentCacheService> logger, IConfiguration configuration)
        {
            _logger = logger;
            
            // Get the data root directory
            var contentRoot = configuration.GetValue<string>("ContentRoot") ?? Directory.GetCurrentDirectory();
            _dataRoot = Path.Combine(contentRoot, "data", "accounts");
            
            // Initialize XML serializer
            _serializer = new XmlSerializer(typeof(AttachmentCollection));
            
            // Ensure the data directory exists
            if (!Directory.Exists(_dataRoot))
            {
                Directory.CreateDirectory(_dataRoot);
            }
        }
        
        public async Task SaveAttachmentsAsync(Guid accountId, List<AttachmentItem> attachments)
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
                    _serializer.Serialize(streamWriter, collection);
                }
                
                _logger.LogDebug("Saved {Count} attachments to cache for account {AccountId}", 
                    attachments.Count, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving attachments cache for account {AccountId}", accountId);
                throw;
            }
        }
        
        public async Task<List<AttachmentItem>> LoadAttachmentsAsync(Guid accountId)
        {
            try
            {
                var cacheFilePath = GetCacheFilePath(accountId);
                
                if (!File.Exists(cacheFilePath))
                {
                    _logger.LogDebug("No attachments cache file found for account {AccountId}", accountId);
                    return new List<AttachmentItem>();
                }
                
                using (var fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var collection = (AttachmentCollection?)_serializer.Deserialize(fileStream);
                    
                    if (collection == null)
                    {
                        _logger.LogWarning("Failed to deserialize attachments cache for account {AccountId}", accountId);
                        return new List<AttachmentItem>();
                    }
                    
                    _logger.LogDebug("Loaded {Count} attachments from cache for account {AccountId}", 
                        collection.Items.Count, accountId);
                    
                    return collection.Items;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading attachments cache for account {AccountId}", accountId);
                return new List<AttachmentItem>();
            }
        }
        
        public async Task ClearAttachmentsAsync(Guid accountId)
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
        }
        
        public bool HasCachedAttachments(Guid accountId)
        {
            var cacheFilePath = GetCacheFilePath(accountId);
            return File.Exists(cacheFilePath);
        }
        
        private string GetCacheFilePath(Guid accountId)
        {
            return Path.Combine(_dataRoot, accountId.ToString(), "cache", "attachments.xml");
        }
    }
}
