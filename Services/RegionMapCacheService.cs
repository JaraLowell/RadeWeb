using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace RadegastWeb.Services
{
    /// <summary>
    /// In-memory cache service for region map images to avoid repeated downloads
    /// from Linden Lab's Map API when the region hasn't changed.
    /// </summary>
    public interface IRegionMapCacheService
    {
        Task<byte[]?> GetRegionMapAsync(ulong regionX, ulong regionY);
        void CacheRegionMap(ulong regionX, ulong regionY, byte[] imageData);
        bool IsRegionMapCached(ulong regionX, ulong regionY);
        void ClearExpiredMaps();
        int GetCacheSize();
    }

    public class RegionMapCacheService : IRegionMapCacheService, IDisposable
    {
        private readonly ILogger<RegionMapCacheService> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly ConcurrentDictionary<string, DateTime> _cacheTimestamps = new();
        private readonly Timer _cleanupTimer;
        
        // Cache settings
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(1); // Region maps don't change often
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(15);
        private readonly long _maxCacheSize = 50 * 1024 * 1024; // 50MB max cache size
        private readonly int _maxRegionCount = 200; // Maximum number of cached regions
        
        private readonly object _lockObject = new object();
        private volatile bool _disposed = false;

        public RegionMapCacheService(
            ILogger<RegionMapCacheService> logger,
            IMemoryCache memoryCache)
        {
            _logger = logger;
            _memoryCache = memoryCache;
            
            // Start cleanup timer
            _cleanupTimer = new Timer(CleanupCallback, null, _cleanupInterval, _cleanupInterval);
            
            _logger.LogInformation("Region map cache service initialized with {ExpiryHours}h expiry and {MaxSizeMB}MB max size", 
                _cacheExpiry.TotalHours, _maxCacheSize / (1024 * 1024));
        }

        public async Task<byte[]?> GetRegionMapAsync(ulong regionX, ulong regionY)
        {
            if (_disposed)
                return null;

            var cacheKey = GetCacheKey(regionX, regionY);
            
            // Check if we have the map in cache
            if (_memoryCache.TryGetValue(cacheKey, out byte[]? cachedMap) && cachedMap != null)
            {
                // Check if cache is still valid
                if (_cacheTimestamps.TryGetValue(cacheKey, out var cacheTime) && 
                    DateTime.UtcNow - cacheTime < _cacheExpiry)
                {
                    _logger.LogDebug("Cache hit for region map ({RegionX}, {RegionY})", regionX, regionY);
                    return cachedMap;
                }
                else
                {
                    // Cache expired, remove it
                    _memoryCache.Remove(cacheKey);
                    _cacheTimestamps.TryRemove(cacheKey, out _);
                    _logger.LogDebug("Cache expired for region map ({RegionX}, {RegionY})", regionX, regionY);
                }
            }

            // Not in cache or expired, need to download
            _logger.LogDebug("Cache miss for region map ({RegionX}, {RegionY})", regionX, regionY);
            
            try
            {
                var mapUrl = $"http://map.secondlife.com/map-1-{regionX}-{regionY}-objects.jpg";
                
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

                // Cache the downloaded image
                CacheRegionMap(regionX, regionY, imageBytes);
                
                _logger.LogDebug("Successfully downloaded and cached region map ({RegionX}, {RegionY}), size: {SizeKB}KB", 
                    regionX, regionY, imageBytes.Length / 1024);
                
                return imageBytes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading region map for ({RegionX}, {RegionY})", regionX, regionY);
                return null;
            }
        }

        public void CacheRegionMap(ulong regionX, ulong regionY, byte[] imageData)
        {
            if (_disposed || imageData == null || imageData.Length == 0)
                return;

            lock (_lockObject)
            {
                // Check cache size limits before adding
                if (_cacheTimestamps.Count >= _maxRegionCount)
                {
                    _logger.LogDebug("Cache at maximum region count ({MaxCount}), cleaning oldest entries", _maxRegionCount);
                    RemoveOldestEntries(Math.Max(1, _maxRegionCount / 4)); // Remove 25% of entries
                }

                var estimatedCacheSize = GetEstimatedCacheSize();
                if (estimatedCacheSize + imageData.Length > _maxCacheSize)
                {
                    _logger.LogDebug("Cache size limit reached ({CurrentSizeMB}MB + {NewSizeKB}KB > {MaxSizeMB}MB), cleaning cache", 
                        estimatedCacheSize / (1024 * 1024), imageData.Length / 1024, _maxCacheSize / (1024 * 1024));
                    RemoveOldestEntries(Math.Max(1, _maxRegionCount / 2)); // Remove 50% of entries
                }

                var cacheKey = GetCacheKey(regionX, regionY);
                
                // Use sliding expiration
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    SlidingExpiration = _cacheExpiry,
                    Size = imageData.Length,
                    Priority = CacheItemPriority.Normal
                };

                _memoryCache.Set(cacheKey, imageData, cacheOptions);
                _cacheTimestamps[cacheKey] = DateTime.UtcNow;
                
                _logger.LogDebug("Cached region map ({RegionX}, {RegionY}), size: {SizeKB}KB", 
                    regionX, regionY, imageData.Length / 1024);
            }
        }

        public bool IsRegionMapCached(ulong regionX, ulong regionY)
        {
            if (_disposed)
                return false;

            var cacheKey = GetCacheKey(regionX, regionY);
            
            if (_memoryCache.TryGetValue(cacheKey, out _))
            {
                // Check if cache is still valid
                if (_cacheTimestamps.TryGetValue(cacheKey, out var cacheTime) && 
                    DateTime.UtcNow - cacheTime < _cacheExpiry)
                {
                    return true;
                }
            }
            
            return false;
        }

        public void ClearExpiredMaps()
        {
            if (_disposed)
                return;

            lock (_lockObject)
            {
                var expiredKeys = new List<string>();
                var cutoffTime = DateTime.UtcNow - _cacheExpiry;
                
                foreach (var kvp in _cacheTimestamps)
                {
                    if (kvp.Value < cutoffTime)
                    {
                        expiredKeys.Add(kvp.Key);
                    }
                }

                foreach (var key in expiredKeys)
                {
                    _memoryCache.Remove(key);
                    _cacheTimestamps.TryRemove(key, out _);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogDebug("Cleared {Count} expired region maps from cache", expiredKeys.Count);
                }
            }
        }

        public int GetCacheSize()
        {
            return _cacheTimestamps.Count;
        }

        private static string GetCacheKey(ulong regionX, ulong regionY)
        {
            return $"region_map_{regionX}_{regionY}";
        }

        private void RemoveOldestEntries(int countToRemove)
        {
            if (_disposed || countToRemove <= 0)
                return;

            // Get oldest entries by timestamp
            var oldestEntries = _cacheTimestamps
                .OrderBy(kvp => kvp.Value)
                .Take(countToRemove)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldestEntries)
            {
                _memoryCache.Remove(key);
                _cacheTimestamps.TryRemove(key, out _);
            }

            _logger.LogDebug("Removed {Count} oldest entries from region map cache", oldestEntries.Count);
        }

        private long GetEstimatedCacheSize()
        {
            // Rough estimate - typical region map is ~50-100KB
            return _cacheTimestamps.Count * 75 * 1024; // 75KB average per region
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
            
            // Clear all cached maps
            lock (_lockObject)
            {
                var allKeys = _cacheTimestamps.Keys.ToList();
                foreach (var key in allKeys)
                {
                    _memoryCache.Remove(key);
                    _cacheTimestamps.TryRemove(key, out _);
                }
            }
            
            _logger.LogInformation("Region map cache service disposed, cleared {Count} cached maps", _cacheTimestamps.Count);
        }
    }
}