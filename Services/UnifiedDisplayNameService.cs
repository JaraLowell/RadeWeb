using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenMetaverse;
using RadegastWeb.Data;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Unified display name service that combines caching, periodic refresh, and global name management
    /// </summary>
    public interface IUnifiedDisplayNameService
    {
        // Core display name functionality
        Task<string> GetDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        Task<string> GetLegacyNameAsync(string avatarId, string? fallbackName = null);
        Task<string> GetUserNameAsync(string avatarId, string? fallbackName = null);
        Task<DisplayName?> GetCachedDisplayNameAsync(string avatarId);
        string? GetCachedDisplayName(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart);
        
        // Update functionality
        void UpdateDisplayName(DisplayName displayName);
        Task UpdateDisplayNamesAsync(Dictionary<UUID, AgentDisplayName> displayNames);
        Task UpdateLegacyNamesAsync(Dictionary<UUID, string> legacyNames);
        Task PreloadDisplayNamesAsync(IEnumerable<string> avatarIds);
        Task<bool> RequestDisplayNamesAsync(List<string> avatarIds, Guid requestingAccountId);
        Task RefreshDisplayNameAsync(Guid accountId, string avatarId);
        
        // Cache management
        Task LoadCachedNamesAsync();
        Task SaveCacheAsync();
        void CleanExpiredCache();
        Task CleanExpiredCacheAsync(Guid accountId);
        Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId);
        void CleanupAccount(Guid accountId);
        
        // Grid client management
        void RegisterGridClient(Guid accountId, GridClient client);
        void UnregisterGridClient(Guid accountId);
        
        // Periodic processing registration
        void RegisterAccount(Guid accountId);
        void UnregisterAccount(Guid accountId);
        
        // Events for real-time updates
        event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
    }

    /// <summary>
    /// Unified implementation that combines GlobalDisplayNameCache and PeriodicDisplayNameService functionality
    /// </summary>
    public class UnifiedDisplayNameService : BackgroundService, IUnifiedDisplayNameService, IGlobalDisplayNameCache, IPeriodicDisplayNameService, IAsyncDisposable
    {
        private readonly ILogger<UnifiedDisplayNameService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IMemoryCache _memoryCache;
        
        // Global cache that spans all accounts (keyed by avatar UUID)
        private readonly ConcurrentDictionary<string, DisplayName> _globalNameCache = new();
        
        // Track which accounts are active for periodic processing
        private readonly ConcurrentDictionary<Guid, bool> _activeAccounts = new();
        
        // Grid clients for making display name requests
        private readonly ConcurrentDictionary<Guid, GridClient> _gridClients = new();
        
        // Processing queue and semaphores
        private readonly Channel<DisplayNameRequest> _requestQueue;
        private readonly ChannelWriter<DisplayNameRequest> _requestWriter;
        private readonly ChannelReader<DisplayNameRequest> _requestReader;
        private readonly SemaphoreSlim _requestSemaphore = new(5, 5); // Limit concurrent requests
        private readonly SemaphoreSlim _processingSemaphore = new(1, 1);
        
        // Cache expiration and refresh settings
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromDays(7);
        private readonly TimeSpan _refreshInterval = TimeSpan.FromHours(2);
        private readonly TimeSpan _periodicProcessingInterval = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _minimumCacheDuration = TimeSpan.FromHours(1);
        
        private const int MaxRequestsPerBatch = 20;
        private const int MaxAvatarsPerCycle = 20;
        private const string FallbackDisplayName = "Loading...";
        
        private Timer? _processingTimer;
        private readonly SemaphoreSlim _saveThrottle = new(1, 1);
        private DateTime _lastSaveTime = DateTime.MinValue;
        private readonly TimeSpan _saveThrottleInterval = TimeSpan.FromMinutes(1);
        private volatile bool _isDisposing = false;

        public event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;

        public UnifiedDisplayNameService(
            ILogger<UnifiedDisplayNameService> logger,
            IServiceProvider serviceProvider,
            IMemoryCache memoryCache)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _memoryCache = memoryCache;
            
            // Create bounded channel for request queue
            var options = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            };
            var channel = Channel.CreateBounded<DisplayNameRequest>(options);
            _requestQueue = channel;
            _requestWriter = channel.Writer;
            _requestReader = channel.Reader;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Unified Display Name Service started");

            // Load cached names on startup
            await LoadCachedNamesAsync();
            
            // Start periodic processing timer
            _processingTimer = new Timer(ProcessPeriodicRefresh, null, _periodicProcessingInterval, _periodicProcessingInterval);

            // Process request queue
            var requestProcessor = ProcessRequestQueueAsync(stoppingToken);
            
            // Wait for cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async void ProcessPeriodicRefresh(object? state)
        {
            if (_isDisposing || !_processingSemaphore.Wait(100))
                return;

            try
            {
                await ProcessActiveAccountsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic display name refresh");
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }

        private async Task ProcessActiveAccountsAsync()
        {
            if (!_activeAccounts.Any())
                return;

            var avatarsToRefresh = new List<string>();
            var now = DateTime.UtcNow;

            // Collect avatars that need refreshing from all active accounts
            foreach (var accountId in _activeAccounts.Keys)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                    var instance = accountService.GetInstance(accountId);
                    
                    if (instance?.IsConnected == true)
                    {
                        // Get nearby avatars and check if they need refresh
                        var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                        foreach (var avatar in nearbyAvatars.Take(MaxAvatarsPerCycle / _activeAccounts.Count))
                        {
                            if (_globalNameCache.TryGetValue(avatar.Id, out var cached))
                            {
                                // Refresh if cache is old
                                if (now - cached.LastUpdated > _minimumCacheDuration)
                                {
                                    avatarsToRefresh.Add(avatar.Id);
                                }
                            }
                            else
                            {
                                // No cache entry, add for refresh
                                avatarsToRefresh.Add(avatar.Id);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing account {AccountId} for periodic refresh", accountId);
                }
            }

            // Refresh collected avatars
            if (avatarsToRefresh.Any())
            {
                await PreloadDisplayNamesAsync(avatarsToRefresh.Take(MaxAvatarsPerCycle));
                _logger.LogDebug("Periodic refresh processed {Count} avatars", avatarsToRefresh.Count);
            }
        }

        // Core display name methods from IGlobalDisplayNameCache
        public Task<string> GetDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId))
                return Task.FromResult(fallbackName ?? "Unknown User");

            var cached = GetCachedDisplayName(avatarId, mode);
            if (!string.IsNullOrEmpty(cached) && cached != FallbackDisplayName)
                return Task.FromResult(cached);

            // Request if not cached or expired
            if (!_globalNameCache.TryGetValue(avatarId, out var displayName) || 
                DateTime.UtcNow - displayName.LastUpdated > _refreshInterval)
            {
                _ = Task.Run(() => PreloadDisplayNamesAsync(new[] { avatarId }));
            }

            return Task.FromResult(fallbackName ?? cached ?? FallbackDisplayName);
        }

        public Task<string> GetLegacyNameAsync(string avatarId, string? fallbackName = null)
        {
            if (_globalNameCache.TryGetValue(avatarId, out var displayName))
            {
                if (!string.IsNullOrEmpty(displayName.LegacyFullName))
                    return Task.FromResult(displayName.LegacyFullName);
            }

            return Task.FromResult(fallbackName ?? FallbackDisplayName);
        }

        public Task<string> GetUserNameAsync(string avatarId, string? fallbackName = null)
        {
            if (_globalNameCache.TryGetValue(avatarId, out var displayName))
            {
                if (!string.IsNullOrEmpty(displayName.UserName))
                    return Task.FromResult(displayName.UserName);
            }

            return Task.FromResult(fallbackName ?? FallbackDisplayName);
        }

        public Task<DisplayName?> GetCachedDisplayNameAsync(string avatarId)
        {
            var result = _globalNameCache.TryGetValue(avatarId, out var displayName) ? displayName : null;
            return Task.FromResult(result);
        }

        public string? GetCachedDisplayName(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart)
        {
            if (!_globalNameCache.TryGetValue(avatarId, out var displayName))
                return null;

            return mode switch
            {
                NameDisplayMode.DisplayNameAndUserName => GetDisplayNameAndUserName(displayName),
                NameDisplayMode.Smart => GetSmartDisplayName(displayName),
                NameDisplayMode.OnlyDisplayName => displayName.DisplayNameValue,
                NameDisplayMode.Standard => displayName.LegacyFullName,
                _ => GetSmartDisplayName(displayName)
            };
        }

        private string GetSmartDisplayName(DisplayName displayName)
        {
            // Prefer display name, fall back to username, then legacy name
            if (!string.IsNullOrEmpty(displayName.DisplayNameValue) && 
                displayName.DisplayNameValue != displayName.UserName && 
                displayName.DisplayNameValue != FallbackDisplayName)
            {
                return displayName.DisplayNameValue;
            }

            if (!string.IsNullOrEmpty(displayName.UserName) && displayName.UserName != FallbackDisplayName)
                return displayName.UserName;

            return displayName.LegacyFullName ?? FallbackDisplayName;
        }

        private string GetDisplayNameAndUserName(DisplayName displayName)
        {
            var displayPart = displayName.DisplayNameValue;
            var userName = displayName.UserName;

            if (string.IsNullOrEmpty(displayPart) || displayPart == FallbackDisplayName)
                displayPart = displayName.LegacyFullName;

            if (string.IsNullOrEmpty(userName) || userName == FallbackDisplayName)
                userName = displayName.LegacyFullName;

            if (displayPart == userName)
                return displayPart ?? FallbackDisplayName;

            return $"{displayPart} ({userName})";
        }

        // Update methods
        public void UpdateDisplayName(DisplayName displayName)
        {
            if (displayName?.AvatarId == null)
                return;

            var avatarId = displayName.AvatarId;
            var updated = false;

            _globalNameCache.AddOrUpdate(avatarId, displayName, (key, existing) =>
            {
                // Protect against invalid overwrites
                if (ShouldUpdateDisplayName(existing, displayName))
                {
                    updated = true;
                    displayName.LastUpdated = DateTime.UtcNow;
                    return displayName;
                }
                return existing;
            });

            if (updated)
            {
                DisplayNameChanged?.Invoke(this, new DisplayNameChangedEventArgs(avatarId, displayName));
                
                // Throttled save
                _ = Task.Run(SaveCacheThrottledAsync);
            }
        }

        private bool ShouldUpdateDisplayName(DisplayName existing, DisplayName newName)
        {
            // Always accept if no existing data
            if (existing == null)
                return true;

            // Don't overwrite valid display names with invalid ones
            if (!string.IsNullOrEmpty(existing.DisplayNameValue) && 
                existing.DisplayNameValue != FallbackDisplayName &&
                (string.IsNullOrEmpty(newName.DisplayNameValue) || newName.DisplayNameValue == FallbackDisplayName))
            {
                return false;
            }

            // Accept if new data is newer or more complete
            return newName.LastUpdated >= existing.LastUpdated;
        }

        public Task UpdateDisplayNamesAsync(Dictionary<UUID, AgentDisplayName> displayNames)
        {
            foreach (var kvp in displayNames)
            {
                var displayName = new DisplayName
                {
                    AvatarId = kvp.Key.ToString(),
                    DisplayNameValue = kvp.Value.DisplayName,
                    UserName = kvp.Value.UserName,
                    LegacyFirstName = kvp.Value.LegacyFirstName,
                    LegacyLastName = kvp.Value.LegacyLastName,
                    LastUpdated = DateTime.UtcNow
                };

                UpdateDisplayName(displayName);
            }
            return Task.CompletedTask;
        }

        public Task UpdateLegacyNamesAsync(Dictionary<UUID, string> legacyNames)
        {
            foreach (var kvp in legacyNames)
            {
                var avatarId = kvp.Key.ToString();
                var legacyName = kvp.Value;
                var parts = legacyName.Split(' ', 2);
                var firstName = parts.Length > 0 ? parts[0] : "";
                var lastName = parts.Length > 1 ? parts[1] : "";

                if (_globalNameCache.TryGetValue(avatarId, out var existing))
                {
                    existing.LegacyFirstName = firstName;
                    existing.LegacyLastName = lastName;
                    existing.LastUpdated = DateTime.UtcNow;
                    UpdateDisplayName(existing);
                }
                else
                {
                    var displayName = new DisplayName
                    {
                        AvatarId = avatarId,
                        LegacyFirstName = firstName,
                        LegacyLastName = lastName,
                        LastUpdated = DateTime.UtcNow
                    };
                    UpdateDisplayName(displayName);
                }
            }
            return Task.CompletedTask;
        }

        // Request processing
        public async Task PreloadDisplayNamesAsync(IEnumerable<string> avatarIds)
        {
            var validIds = avatarIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (!validIds.Any())
                return;

            foreach (var batch in validIds.Chunk(MaxRequestsPerBatch))
            {
                await _requestWriter.WriteAsync(new DisplayNameRequest
                {
                    AvatarIds = batch.ToList(),
                    RequestingAccountId = Guid.Empty,
                    Priority = RequestPriority.Normal
                });
            }
        }

        public async Task<bool> RequestDisplayNamesAsync(List<string> avatarIds, Guid requestingAccountId)
        {
            if (!avatarIds.Any())
                return false;

            await _requestWriter.WriteAsync(new DisplayNameRequest
            {
                AvatarIds = avatarIds,
                RequestingAccountId = requestingAccountId,
                Priority = RequestPriority.High
            });

            return true;
        }

        private async Task ProcessRequestQueueAsync(CancellationToken cancellationToken)
        {
            await foreach (var request in _requestReader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessDisplayNameRequestAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing display name request");
                }
            }
        }

        private async Task ProcessDisplayNameRequestAsync(DisplayNameRequest request)
        {
            // Filter out IDs we already have recent data for
            var idsToRequest = request.AvatarIds.Where(id =>
            {
                if (!_globalNameCache.TryGetValue(id, out var cached))
                    return true; // No cache, need to request

                // Request if cache is old
                return DateTime.UtcNow - cached.LastUpdated > _refreshInterval;
            }).ToList();

            if (!idsToRequest.Any())
                return;

            // Find a suitable grid client for the request
            var gridClient = GetAvailableGridClient(request.RequestingAccountId);
            if (gridClient?.Network.Connected != true)
            {
                _logger.LogWarning("No connected grid client available for display name request");
                return;
            }

            await _requestSemaphore.WaitAsync();
            try
            {
                await RequestDisplayNamesFromGridAsync(gridClient, idsToRequest);
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        private GridClient? GetAvailableGridClient(Guid preferredAccountId)
        {
            // Try preferred account first
            if (preferredAccountId != Guid.Empty && _gridClients.TryGetValue(preferredAccountId, out var preferred))
                return preferred;

            // Return any connected client
            return _gridClients.Values.FirstOrDefault(client => client.Network.Connected);
        }

        private async Task RequestDisplayNamesFromGridAsync(GridClient client, List<string> avatarIds)
        {
            var validUuids = new List<UUID>();
            foreach (var id in avatarIds)
            {
                if (UUID.TryParse(id, out var uuid))
                    validUuids.Add(uuid);
            }

            if (!validUuids.Any())
                return;

            try
            {
                // Use a TaskCompletionSource to handle the async callback
                var tcs = new TaskCompletionSource<bool>();
                var received = 0;
                var expected = validUuids.Count;

                EventHandler<UUIDNameReplyEventArgs> handler = (sender, e) =>
                {
                    received += e.Names.Count;
                    if (received >= expected)
                        tcs.TrySetResult(true);
                };

                client.Avatars.UUIDNameReply += handler;

                // Request the names
                client.Avatars.RequestAvatarNames(validUuids);

                // Wait for response or timeout
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

                client.Avatars.UUIDNameReply -= handler;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting display names from grid");
            }
        }

        // Grid client management
        public void RegisterGridClient(Guid accountId, GridClient client)
        {
            _gridClients.TryAdd(accountId, client);
            _logger.LogDebug("Registered grid client for account {AccountId}", accountId);
        }

        public void UnregisterGridClient(Guid accountId)
        {
            _gridClients.TryRemove(accountId, out _);
            _logger.LogDebug("Unregistered grid client for account {AccountId}", accountId);
        }

        // Account management for periodic processing
        public void RegisterAccount(Guid accountId)
        {
            _activeAccounts.TryAdd(accountId, true);
            _logger.LogDebug("Registered account {AccountId} for periodic display name processing", accountId);
        }

        public void UnregisterAccount(Guid accountId)
        {
            _activeAccounts.TryRemove(accountId, out _);
            _logger.LogDebug("Unregistered account {AccountId} from periodic display name processing", accountId);
        }

        // Cache persistence
        public async Task LoadCachedNamesAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<RadegastDbContext>();
                
                var cachedNames = await context.GlobalDisplayNames.ToListAsync();
                
                foreach (var globalDisplayName in cachedNames)
                {
                    var displayName = globalDisplayName.ToDisplayName();
                    _globalNameCache.TryAdd(displayName.AvatarId, displayName);
                }
                
                _logger.LogInformation("Loaded {Count} cached display names", cachedNames.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cached display names");
            }
        }

        public async Task SaveCacheAsync()
        {
            // Skip saving if we're disposing or service provider is not available
            if (_isDisposing)
            {
                _logger.LogDebug("Skipping cache save during disposal");
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<RadegastDbContext>();
                
                var namesToSave = _globalNameCache.Values.ToList();
                
                foreach (var displayName in namesToSave)
                {
                    var existing = await context.GlobalDisplayNames.FindAsync(displayName.AvatarId);
                    if (existing != null)
                    {
                        var updated = GlobalDisplayName.FromDisplayName(displayName);
                        updated.Id = existing.Id; // Preserve the database ID
                        context.Entry(existing).CurrentValues.SetValues(updated);
                    }
                    else
                    {
                        var globalDisplayName = GlobalDisplayName.FromDisplayName(displayName);
                        context.GlobalDisplayNames.Add(globalDisplayName);
                    }
                }
                
                await context.SaveChangesAsync();
                _logger.LogDebug("Saved {Count} display names to cache", namesToSave.Count);
            }
            catch (ObjectDisposedException)
            {
                // Service provider disposed during shutdown, this is expected
                _logger.LogDebug("Cannot save cache during shutdown - service provider disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving display name cache");
            }
        }

        private async Task SaveCacheThrottledAsync()
        {
            if (!_saveThrottle.Wait(100))
                return;

            try
            {
                var now = DateTime.UtcNow;
                if (now - _lastSaveTime < _saveThrottleInterval)
                    return;

                await SaveCacheAsync();
                _lastSaveTime = now;
            }
            finally
            {
                _saveThrottle.Release();
            }
        }

        public void CleanExpiredCache()
        {
            var cutoff = DateTime.UtcNow - _cacheExpiration;
            var expiredKeys = _globalNameCache
                .Where(kvp => kvp.Value.LastUpdated < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _globalNameCache.TryRemove(key, out _);
            }

            if (expiredKeys.Any())
                _logger.LogDebug("Cleaned {Count} expired display names from cache", expiredKeys.Count);
        }

        // Legacy compatibility methods
        public Task RefreshDisplayNameAsync(Guid accountId, string avatarId)
        {
            return PreloadDisplayNamesAsync(new[] { avatarId });
        }

        public Task CleanExpiredCacheAsync(Guid accountId)
        {
            CleanExpiredCache();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId)
        {
            return Task.FromResult<IEnumerable<DisplayName>>(_globalNameCache.Values.ToList());
        }

        public void CleanupAccount(Guid accountId)
        {
            UnregisterAccount(accountId);
            UnregisterGridClient(accountId);
        }

        // IPeriodicDisplayNameService compatibility
        public new Task StartAsync(CancellationToken cancellationToken)
        {
            return base.StartAsync(cancellationToken);
        }

        public new async Task StopAsync(CancellationToken cancellationToken)
        {
            // Complete the channel to stop the processing loop
            try
            {
                _requestWriter.Complete();
            }
            catch (InvalidOperationException)
            {
                // Channel is already completed, ignore
            }
            
            await base.StopAsync(cancellationToken);
        }

        // Disposal
        public ValueTask DisposeAsync()
        {
            _isDisposing = true;
            
            _processingTimer?.Dispose();
            
            // Channel should already be completed in StopAsync, but ensure it's completed
            if (!_requestWriter.TryComplete())
            {
                // Channel was already completed, which is expected
            }
            
            // Don't try to save cache during disposal as service provider may be disposed
            // Cache should be saved by the throttled save mechanism during normal operation
            
            _requestSemaphore.Dispose();
            _processingSemaphore.Dispose();
            _saveThrottle.Dispose();
            
            Dispose();
            
            return ValueTask.CompletedTask;
        }

        public override void Dispose()
        {
            _isDisposing = true;
            _processingTimer?.Dispose();
            _requestSemaphore?.Dispose();
            _processingSemaphore?.Dispose();
            _saveThrottle?.Dispose();
            base.Dispose();
        }
    }

    // Supporting classes
    internal class DisplayNameRequest
    {
        public List<string> AvatarIds { get; set; } = new();
        public Guid RequestingAccountId { get; set; }
        public RequestPriority Priority { get; set; }
    }

    internal enum RequestPriority
    {
        Low,
        Normal,
        High
    }
}