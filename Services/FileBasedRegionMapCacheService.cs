using System.Collections.Concurrent;
using System.Text.Json;

namespace RadegastWeb.Services
{
    /// <summary>
    /// File-based cache service for region map images stored per account in their cache folders
    /// Maps are cached for 24 hours and only refreshed when actually needed
    /// </summary>
    public class FileBasedRegionMapCacheService : IRegionMapCacheService, IDisposable
    {
        private readonly ILogger<FileBasedRegionMapCacheService> _logger;
        private readonly string _dataRoot;
        private readonly Timer _cleanupTimer;
        
        // Cache settings - longer expiry since maps don't change often
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(24); // 24 hours - maps rarely change
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6); // Clean up every 6 hours
        
        // In-memory metadata cache to avoid file system hits
        private readonly ConcurrentDictionary<string, MapCacheMetadata> _metadataCache = new();
        
        private volatile bool _disposed = false;

        public FileBasedRegionMapCacheService(
            ILogger<FileBasedRegionMapCacheService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            
            // Get the data root directory
            var contentRoot = configuration.GetValue<string>("ContentRoot") ?? Directory.GetCurrentDirectory();
            _dataRoot = Path.Combine(contentRoot, "data", "accounts");
            
            // Ensure the data directory exists
            if (!Directory.Exists(_dataRoot))
            {
                Directory.CreateDirectory(_dataRoot);
            }
            
            // Start cleanup timer
            _cleanupTimer = new Timer(CleanupCallback, null, _cleanupInterval, _cleanupInterval);
            
            // Load existing metadata on startup
            _ = Task.Run(LoadMetadataAsync);
            
            _logger.LogInformation("File-based region map cache service initialized with {ExpiryHours}h expiry", 
                _cacheExpiry.TotalHours);
        }

        public async Task<byte[]?> GetRegionMapAsync(ulong regionX, ulong regionY)
        {
            if (_disposed)
                return null;

            var cacheKey = GetCacheKey(regionX, regionY);
            
            // Check metadata cache first
            if (_metadataCache.TryGetValue(cacheKey, out var metadata))
            {
                // Check if cache is still valid
                if (DateTime.UtcNow - metadata.CacheTime < _cacheExpiry)
                {
                    // Try to read the cached file
                    var cachedData = await ReadCachedMapAsync(metadata.AccountId, regionX, regionY);
                    if (cachedData != null)
                    {
                        _logger.LogDebug("Cache hit for region map ({RegionX}, {RegionY}) for account {AccountId}", 
                            regionX, regionY, metadata.AccountId);
                        return cachedData;
                    }
                }
                
                // Cache expired or file missing, remove from metadata
                _metadataCache.TryRemove(cacheKey, out _);
            }

            _logger.LogDebug("Cache miss for region map ({RegionX}, {RegionY})", regionX, regionY);
            
            // Download the map
            var mapData = await DownloadRegionMapAsync(regionX, regionY);
            if (mapData == null)
                return null;

            // We don't cache it here since we don't have an accountId context
            // Caching will be done by the controller when it knows which account requested it
            return mapData;
        }

        public async Task<byte[]?> GetRegionMapForAccountAsync(Guid accountId, ulong regionX, ulong regionY)
        {
            if (_disposed)
                return null;

            // Check if we have a cached version for this account
            var cachedData = await ReadCachedMapAsync(accountId, regionX, regionY);
            if (cachedData != null && IsMapCacheValid(accountId, regionX, regionY))
            {
                _logger.LogDebug("Cache hit for region map ({RegionX}, {RegionY}) for account {AccountId}", 
                    regionX, regionY, accountId);
                return cachedData;
            }

            _logger.LogDebug("Cache miss for region map ({RegionX}, {RegionY}) for account {AccountId}", 
                regionX, regionY, accountId);
            
            // Download and cache the map
            var mapData = await DownloadRegionMapAsync(regionX, regionY);
            if (mapData != null)
            {
                await CacheRegionMapForAccountAsync(accountId, regionX, regionY, mapData);
            }

            return mapData;
        }

        public async Task<byte[]?> GetRegionMapWithNameAsync(ulong regionX, ulong regionY, string regionName)
        {
            if (_disposed || string.IsNullOrEmpty(regionName))
                return await GetRegionMapAsync(regionX, regionY); // Fallback to coordinate-only cache

            // Check if we have a cached version with the specific region name
            var cachedData = await ReadCachedMapWithNameAsync(regionX, regionY, regionName);
            if (cachedData != null && IsMapCacheWithNameValid(regionX, regionY, regionName))
            {
                _logger.LogDebug("Cache hit for region map {RegionName} at ({RegionX}, {RegionY})", regionName, regionX, regionY);
                return cachedData;
            }

            _logger.LogDebug("Cache miss for region map {RegionName} at ({RegionX}, {RegionY})", regionName, regionX, regionY);
            
            // Download and cache the map with region name
            var mapData = await DownloadRegionMapAsync(regionX, regionY);
            if (mapData != null)
            {
                CacheRegionMapWithName(regionX, regionY, regionName, mapData);
            }

            return mapData;
        }

        public void CacheRegionMap(ulong regionX, ulong regionY, byte[] imageData)
        {
            // This method is kept for interface compatibility but doesn't cache
            // since we need account context for file-based caching
            _logger.LogDebug("CacheRegionMap called without account context - skipping cache");
        }

        public void CacheRegionMapWithName(ulong regionX, ulong regionY, string regionName, byte[] imageData)
        {
            if (_disposed || imageData == null || imageData.Length == 0 || string.IsNullOrEmpty(regionName))
                return;

            try
            {
                // Create a global cache directory for region-name-specific maps
                var globalCacheDir = Path.Combine(_dataRoot, "global_maps");
                if (!Directory.Exists(globalCacheDir))
                {
                    Directory.CreateDirectory(globalCacheDir);
                }
                
                var mapFilePath = GetMapFilePathWithName(regionX, regionY, regionName);
                
                // Write the image data
                File.WriteAllBytes(mapFilePath, imageData);
                
                _logger.LogDebug("Cached region map for {RegionName} at ({RegionX}, {RegionY}), size: {SizeKB}KB", 
                    regionName, regionX, regionY, imageData.Length / 1024);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error caching region map for {RegionName} at ({RegionX}, {RegionY})", 
                    regionName, regionX, regionY);
            }
        }
        
        public async Task<byte[]?> GetRegionMapForAccountWithNameAsync(Guid accountId, ulong regionX, ulong regionY, string regionName)
        {
            if (_disposed || string.IsNullOrEmpty(regionName))
                return null;

            // Check if we have a cached version for this account with the specific region name
            var cachedData = await ReadCachedMapForAccountWithNameAsync(accountId, regionX, regionY, regionName);
            if (cachedData != null && IsMapCacheForAccountWithNameValid(accountId, regionX, regionY, regionName))
            {
                _logger.LogDebug("Cache hit for account {AccountId} in region {RegionName} at ({RegionX}, {RegionY})", 
                    accountId, regionName, regionX, regionY);
                return cachedData;
            }

            _logger.LogDebug("Cache miss for account {AccountId} in region {RegionName} at ({RegionX}, {RegionY})", 
                accountId, regionName, regionX, regionY);
            
            // Download and cache the map for this account with region name
            var mapData = await DownloadRegionMapAsync(regionX, regionY);
            if (mapData != null)
            {
                await CacheRegionMapForAccountWithNameAsync(accountId, regionX, regionY, regionName, mapData);
            }

            return mapData;
        }
        
        public async Task CacheRegionMapForAccountWithNameAsync(Guid accountId, ulong regionX, ulong regionY, string regionName, byte[] imageData)
        {
            if (_disposed || imageData == null || imageData.Length == 0 || string.IsNullOrEmpty(regionName))
                return;

            try
            {
                var mapFilePath = GetMapFilePathForAccountWithName(accountId, regionX, regionY, regionName);
                
                // Ensure directory exists
                var directory = Path.GetDirectoryName(mapFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                // Write the image data
                await File.WriteAllBytesAsync(mapFilePath, imageData);
                
                _logger.LogDebug("Cached region map for account {AccountId} in {RegionName} at ({RegionX}, {RegionY}), size: {SizeKB}KB", 
                    accountId, regionName, regionX, regionY, imageData.Length / 1024);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error caching region map for account {AccountId} in {RegionName} at ({RegionX}, {RegionY})", 
                    accountId, regionName, regionX, regionY);
            }
        }

        public async Task CacheRegionMapForAccountAsync(Guid accountId, ulong regionX, ulong regionY, byte[] imageData)
        {
            if (_disposed || imageData == null || imageData.Length == 0)
                return;

            try
            {
                var accountCacheDir = GetAccountCacheDirectory(accountId);
                var mapFilePath = GetMapFilePath(accountId, regionX, regionY);
                
                // Write the image data
                await File.WriteAllBytesAsync(mapFilePath, imageData);
                
                // Update metadata cache
                var cacheKey = GetCacheKey(regionX, regionY);
                var metadata = new MapCacheMetadata
                {
                    AccountId = accountId,
                    RegionX = regionX,
                    RegionY = regionY,
                    CacheTime = DateTime.UtcNow,
                    FilePath = mapFilePath
                };
                
                _metadataCache.AddOrUpdate(cacheKey, metadata, (key, existing) => metadata);
                
                // Save metadata to disk for persistence across restarts
                await SaveMetadataAsync(accountId);
                
                _logger.LogDebug("Cached region map ({RegionX}, {RegionY}) for account {AccountId}, size: {SizeKB}KB", 
                    regionX, regionY, accountId, imageData.Length / 1024);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error caching region map ({RegionX}, {RegionY}) for account {AccountId}", 
                    regionX, regionY, accountId);
            }
        }

        public bool IsRegionMapCached(ulong regionX, ulong regionY)
        {
            if (_disposed)
                return false;

            var cacheKey = GetCacheKey(regionX, regionY);
            
            if (_metadataCache.TryGetValue(cacheKey, out var metadata))
            {
                // Check if cache is still valid and file exists
                if (DateTime.UtcNow - metadata.CacheTime < _cacheExpiry)
                {
                    return File.Exists(metadata.FilePath);
                }
            }
            
            return false;
        }

        public bool IsRegionMapCachedForAccount(Guid accountId, ulong regionX, ulong regionY)
        {
            if (_disposed)
                return false;

            return IsMapCacheValid(accountId, regionX, regionY);
        }

        public void ClearExpiredMaps()
        {
            if (_disposed)
                return;

            var expiredKeys = new List<string>();
            var cutoffTime = DateTime.UtcNow - _cacheExpiry;
            
            foreach (var kvp in _metadataCache)
            {
                if (kvp.Value.CacheTime < cutoffTime)
                {
                    expiredKeys.Add(kvp.Key);
                    
                    // Delete the actual file
                    try
                    {
                        if (File.Exists(kvp.Value.FilePath))
                        {
                            File.Delete(kvp.Value.FilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error deleting expired map file {FilePath}", kvp.Value.FilePath);
                    }
                }
            }

            // Remove expired entries from metadata cache
            foreach (var key in expiredKeys)
            {
                _metadataCache.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0)
            {
                _logger.LogDebug("Cleared {Count} expired region maps from cache", expiredKeys.Count);
            }
        }

        public async Task CleanupAccountMapsAsync(Guid accountId)
        {
            if (_disposed)
                return;

            await Task.Run(() =>
            {
                try
                {
                    var accountCacheDir = GetAccountCacheDirectory(accountId);
                    if (Directory.Exists(accountCacheDir))
                    {
                        // Delete all region map files (both old format and new format with region names)
                        var mapFiles = Directory.GetFiles(accountCacheDir, "region_*.jpg");
                        foreach (var file in mapFiles)
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error deleting map file {FilePath}", file);
                            }
                        }
                        
                        // Remove from metadata cache
                        var keysToRemove = _metadataCache
                            .Where(kvp => kvp.Value.AccountId == accountId)
                            .Select(kvp => kvp.Key)
                            .ToList();
                            
                        foreach (var key in keysToRemove)
                        {
                            _metadataCache.TryRemove(key, out _);
                        }
                        
                        // Delete metadata file
                        var metadataPath = Path.Combine(accountCacheDir, "map_metadata.json");
                        if (File.Exists(metadataPath))
                        {
                            File.Delete(metadataPath);
                        }
                        
                        _logger.LogInformation("Cleaned up {Count} map files for account {AccountId}", 
                            mapFiles.Length, accountId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cleaning up maps for account {AccountId}", accountId);
                }
            });
        }

        public int GetCacheSize()
        {
            return _metadataCache.Count;
        }

        private async Task<byte[]?> DownloadRegionMapAsync(ulong regionX, ulong regionY)
        {
            try
            {
                var mapUrl = $"https://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
                _logger.LogDebug("Downloading region map from: {MapUrl}", mapUrl);
                
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                
                var response = await httpClient.GetAsync(mapUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to download region map from {MapUrl}. Status: {StatusCode}", 
                        mapUrl, response.StatusCode);
                    return null;
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    _logger.LogWarning("Downloaded empty region map from {MapUrl}", mapUrl);
                    return null;
                }

                _logger.LogDebug("Successfully downloaded region map ({RegionX}, {RegionY}), size: {SizeKB}KB", 
                    regionX, regionY, imageBytes.Length / 1024);
                
                return imageBytes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading region map for ({RegionX}, {RegionY})", regionX, regionY);
                return null;
            }
        }

        private async Task<byte[]?> ReadCachedMapAsync(Guid accountId, ulong regionX, ulong regionY)
        {
            try
            {
                var mapFilePath = GetMapFilePath(accountId, regionX, regionY);
                
                if (!File.Exists(mapFilePath))
                    return null;
                
                return await File.ReadAllBytesAsync(mapFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cached map for account {AccountId}, region ({RegionX}, {RegionY})", 
                    accountId, regionX, regionY);
                return null;
            }
        }

        private bool IsMapCacheValid(Guid accountId, ulong regionX, ulong regionY)
        {
            try
            {
                var mapFilePath = GetMapFilePath(accountId, regionX, regionY);
                
                if (!File.Exists(mapFilePath))
                    return false;
                
                var fileInfo = new FileInfo(mapFilePath);
                return DateTime.UtcNow - fileInfo.LastWriteTimeUtc < _cacheExpiry;
            }
            catch
            {
                return false;
            }
        }

        private string GetAccountCacheDirectory(Guid accountId)
        {
            var accountCacheDir = Path.Combine(_dataRoot, accountId.ToString(), "cache", "maps");
            
            if (!Directory.Exists(accountCacheDir))
            {
                Directory.CreateDirectory(accountCacheDir);
            }
            
            return accountCacheDir;
        }

        private string GetMapFilePath(Guid accountId, ulong regionX, ulong regionY)
        {
            var accountCacheDir = GetAccountCacheDirectory(accountId);
            return Path.Combine(accountCacheDir, $"region_{regionX}_{regionY}.jpg");
        }

        private static string GetCacheKey(ulong regionX, ulong regionY)
        {
            return $"region_map_{regionX}_{regionY}";
        }
        
        private string GetMapFilePathWithName(ulong regionX, ulong regionY, string regionName)
        {
            var globalCacheDir = Path.Combine(_dataRoot, "global_maps");
            if (!Directory.Exists(globalCacheDir))
            {
                Directory.CreateDirectory(globalCacheDir);
            }
            
            // Sanitize region name for file system
            var sanitizedName = regionName.Replace(" ", "_").Replace("/", "_").Replace("\\", "_")
                                         .Replace(":", "_").Replace("*", "_").Replace("?", "_")
                                         .Replace("<", "_").Replace(">", "_").Replace("|", "_");
            
            return Path.Combine(globalCacheDir, $"region_{regionX}_{regionY}_{sanitizedName}.jpg");
        }
        
        private async Task<byte[]?> ReadCachedMapWithNameAsync(ulong regionX, ulong regionY, string regionName)
        {
            try
            {
                var mapFilePath = GetMapFilePathWithName(regionX, regionY, regionName);
                
                if (!File.Exists(mapFilePath))
                    return null;
                
                return await File.ReadAllBytesAsync(mapFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cached map for region {RegionName} at ({RegionX}, {RegionY})", 
                    regionName, regionX, regionY);
                return null;
            }
        }
        
        private bool IsMapCacheWithNameValid(ulong regionX, ulong regionY, string regionName)
        {
            try
            {
                var mapFilePath = GetMapFilePathWithName(regionX, regionY, regionName);
                
                if (!File.Exists(mapFilePath))
                    return false;
                
                var fileInfo = new FileInfo(mapFilePath);
                return DateTime.UtcNow - fileInfo.LastWriteTimeUtc < _cacheExpiry;
            }
            catch
            {
                return false;
            }
        }
        
        private string GetMapFilePathForAccountWithName(Guid accountId, ulong regionX, ulong regionY, string regionName)
        {
            var accountCacheDir = GetAccountCacheDirectory(accountId);
            
            // Sanitize region name for file system
            var sanitizedName = regionName.Replace(" ", "_").Replace("/", "_").Replace("\\", "_")
                                         .Replace(":", "_").Replace("*", "_").Replace("?", "_")
                                         .Replace("<", "_").Replace(">", "_").Replace("|", "_");
            
            return Path.Combine(accountCacheDir, $"region_{regionX}_{regionY}_{sanitizedName}.jpg");
        }
        
        private async Task<byte[]?> ReadCachedMapForAccountWithNameAsync(Guid accountId, ulong regionX, ulong regionY, string regionName)
        {
            try
            {
                var mapFilePath = GetMapFilePathForAccountWithName(accountId, regionX, regionY, regionName);
                
                if (!File.Exists(mapFilePath))
                    return null;
                
                return await File.ReadAllBytesAsync(mapFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cached map for account {AccountId} in region {RegionName} at ({RegionX}, {RegionY})", 
                    accountId, regionName, regionX, regionY);
                return null;
            }
        }
        
        private bool IsMapCacheForAccountWithNameValid(Guid accountId, ulong regionX, ulong regionY, string regionName)
        {
            try
            {
                var mapFilePath = GetMapFilePathForAccountWithName(accountId, regionX, regionY, regionName);
                
                if (!File.Exists(mapFilePath))
                    return false;
                
                var fileInfo = new FileInfo(mapFilePath);
                return DateTime.UtcNow - fileInfo.LastWriteTimeUtc < _cacheExpiry;
            }
            catch
            {
                return false;
            }
        }
        
        public async Task ClearAccountRegionMapAsync(Guid accountId)
        {
            if (_disposed)
                return;

            await Task.Run(() =>
            {
                try
                {
                    var accountCacheDir = GetAccountCacheDirectory(accountId);
                    if (Directory.Exists(accountCacheDir))
                    {
                        var mapFiles = Directory.GetFiles(accountCacheDir, "region_*.jpg");
                        var deletedCount = 0;
                        
                        foreach (var file in mapFiles)
                        {
                            try
                            {
                                File.Delete(file);
                                deletedCount++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error deleting map file {FilePath}", file);
                            }
                        }
                        
                        if (deletedCount > 0)
                        {
                            _logger.LogDebug("Cleared {Count} map files for account {AccountId} due to region change", 
                                deletedCount, accountId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error clearing region maps for account {AccountId}", accountId);
                }
            });
        }

        private async Task SaveMetadataAsync(Guid accountId)
        {
            try
            {
                var accountCacheDir = GetAccountCacheDirectory(accountId);
                var metadataPath = Path.Combine(accountCacheDir, "map_metadata.json");
                
                // Get metadata for this account only
                var accountMetadata = _metadataCache.Values
                    .Where(m => m.AccountId == accountId)
                    .ToList();
                
                var json = JsonSerializer.Serialize(accountMetadata, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                await File.WriteAllTextAsync(metadataPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error saving metadata for account {AccountId}", accountId);
            }
        }

        private async Task LoadMetadataAsync()
        {
            try
            {
                if (!Directory.Exists(_dataRoot))
                    return;

                var accountDirs = Directory.GetDirectories(_dataRoot);
                
                foreach (var accountDir in accountDirs)
                {
                    var accountIdStr = Path.GetFileName(accountDir);
                    if (!Guid.TryParse(accountIdStr, out var accountId))
                        continue;
                    
                    var metadataPath = Path.Combine(accountDir, "cache", "maps", "map_metadata.json");
                    if (!File.Exists(metadataPath))
                        continue;
                    
                    var json = await File.ReadAllTextAsync(metadataPath);
                    var accountMetadata = JsonSerializer.Deserialize<List<MapCacheMetadata>>(json);
                    
                    if (accountMetadata != null)
                    {
                        foreach (var metadata in accountMetadata)
                        {
                            var cacheKey = GetCacheKey(metadata.RegionX, metadata.RegionY);
                            _metadataCache.TryAdd(cacheKey, metadata);
                        }
                    }
                }
                
                _logger.LogInformation("Loaded {Count} cached map metadata entries", _metadataCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cached metadata");
            }
        }

        private void CleanupCallback(object? state)
        {
            if (_disposed)
                return;

            try
            {
                ClearExpiredMaps();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during region map cache cleanup");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            
            _cleanupTimer?.Dispose();
            
            _logger.LogInformation("File-based region map cache service disposed, had {Count} cached maps", 
                _metadataCache.Count);
        }
    }

    public class MapCacheMetadata
    {
        public Guid AccountId { get; set; }
        public ulong RegionX { get; set; }
        public ulong RegionY { get; set; }
        public DateTime CacheTime { get; set; }
        public string FilePath { get; set; } = string.Empty;
    }
}