using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenMetaverse;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Master display name service that handles all display name operations for all accounts.
    /// Based on Radegast's NameManager implementation but adapted for multi-account web environment.
    /// 
    /// Key principles from Radegast:
    /// 1. Never overwrite valid display names with placeholder values ("Loading...", null, "")
    /// 2. Never overwrite custom display names with default/legacy names unless existing is invalid
    /// 3. Use background queuing to batch requests and avoid API rate limits
    /// 4. Provide immediate fallback responses rather than blocking for network requests
    /// </summary>
    public interface IMasterDisplayNameService
    {
        // Core display name functionality
        Task<string> GetDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        Task<string> GetLegacyNameAsync(string avatarId, string? fallbackName = null);
        Task<string> GetUserNameAsync(string avatarId, string? fallbackName = null);
        string GetDisplayNameSync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        
        // Update functionality
        Task UpdateDisplayNamesAsync(Dictionary<UUID, AgentDisplayName> displayNames);
        Task UpdateLegacyNamesAsync(Dictionary<UUID, string> legacyNames);
        Task PreloadDisplayNamesAsync(IEnumerable<string> avatarIds);
        Task RequestDisplayNamesAsync(List<string> avatarIds, Guid requestingAccountId);
        
        // Account management
        void RegisterGridClient(Guid accountId, GridClient client);
        void UnregisterGridClient(Guid accountId);
        void RegisterAccount(Guid accountId);
        void UnregisterAccount(Guid accountId);
        void CleanupAccount(Guid accountId);
        
        // Cache management
        Task LoadCacheAsync();
        Task SaveCacheAsync();
        void CleanExpiredCache();
        Task<int> CleanupOldCachedNamesAsync(int keepDays = 60);
        
        // Events
        event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
        
        // Force refresh from database and network if needed
        Task<string> RefreshDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        
        // Monitoring
        (int PendingRetries, int ReadyForRetry, int TotalRequests, Dictionary<string, int> FailureTypes) GetQueueStatistics();
    }

    public class MasterDisplayNameService : BackgroundService, IMasterDisplayNameService
    {
        private readonly ILogger<MasterDisplayNameService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IMemoryCache _memoryCache;
        private readonly IGlobalDisplayNameCache _globalCache;
        
        // Active grid clients for making requests
        private readonly ConcurrentDictionary<Guid, GridClient> _gridClients = new();
        
        // Track registered accounts for periodic processing
        private readonly ConcurrentDictionary<Guid, bool> _registeredAccounts = new();
        
        // Track pending requests to prevent duplicates
        private readonly ConcurrentDictionary<string, DateTime> _pendingRequests = new();
        
        // Track retry attempts to enforce max retry limit
        private readonly ConcurrentDictionary<string, int> _retryAttempts = new();
        
        // Request queue for batching name requests
        private readonly Channel<NameRequest> _requestChannel;
        private readonly ChannelWriter<NameRequest> _requestWriter;
        private readonly ChannelReader<NameRequest> _requestReader;
        
        // Retry queue for failed name lookups (similar to Radegast's backlog)
        private readonly Channel<RetryNameRequest> _retryChannel;
        private readonly ChannelWriter<RetryNameRequest> _retryWriter;
        private readonly ChannelReader<RetryNameRequest> _retryReader;
        
        // Rate limiting and batching configuration
        private readonly SemaphoreSlim _requestSemaphore = new(5, 5); // Max 5 concurrent requests
        private readonly TimeSpan _batchDelay = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan _periodicInterval = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _refreshAge = TimeSpan.FromHours(2);
        private readonly TimeSpan _firstRetryDelay = TimeSpan.FromSeconds(5);   // First retry after 5 seconds
        private readonly TimeSpan _secondRetryDelay = TimeSpan.FromSeconds(30); // Second retry after 30 seconds  
        private readonly TimeSpan _thirdRetryDelay = TimeSpan.FromMinutes(2);   // Third retry after 2 minutes
        private readonly TimeSpan _pendingRequestTimeout = TimeSpan.FromMinutes(5); // Clear pending requests after 5 minutes
        private const int MaxBatchSize = 20;
        private const int MaxPeriodicAvatars = 20;
        private const int MaxRetryAttempts = 3; // Maximum of 3 retry attempts
        
        // Processing state
        private Timer? _periodicTimer;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private volatile bool _isDisposing = false;
        
        // Constants
        private const string LoadingPlaceholder = "Loading...";
        private const string UnknownUser = "Unknown User";

        public event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;

        public MasterDisplayNameService(
            ILogger<MasterDisplayNameService> logger,
            IServiceProvider serviceProvider,
            IMemoryCache memoryCache,
            IGlobalDisplayNameCache globalCache)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _memoryCache = memoryCache;
            _globalCache = globalCache;
            
            // Subscribe to global cache events
            _globalCache.DisplayNameChanged += (s, e) => DisplayNameChanged?.Invoke(this, e);
            
            // Create request channel for batching
            var channelOptions = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };
            _requestChannel = Channel.CreateBounded<NameRequest>(channelOptions);
            _requestWriter = _requestChannel.Writer;
            _requestReader = _requestChannel.Reader;
            
            // Create retry channel for failed lookups (similar to Radegast's backlog)
            var retryChannelOptions = new BoundedChannelOptions(2000)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // Drop oldest retries if full
                SingleReader = true,
                SingleWriter = false
            };
            _retryChannel = Channel.CreateBounded<RetryNameRequest>(retryChannelOptions);
            _retryWriter = _retryChannel.Writer;
            _retryReader = _retryChannel.Reader;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Master Display Name Service started");

            try
            {
                // Load cache on startup
                await LoadCacheAsync();
                
                // Start periodic processing timer (for refresh)
                _periodicTimer = new Timer(ProcessPeriodicRefresh, null, _periodicInterval, _periodicInterval);
                
                // Start cleanup timer to prevent unbounded dictionary growth (runs every 10 minutes)
                var cleanupTimer = new Timer(CleanupTrackingDictionaries, null, 
                    TimeSpan.FromMinutes(10), 
                    TimeSpan.FromMinutes(10));

                // Run both main queue and retry queue processing in parallel
                await Task.WhenAll(
                    ProcessRequestQueueAsync(stoppingToken),
                    ProcessRetryQueueAsync(stoppingToken)
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Master Display Name Service stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Master Display Name Service");
            }
        }

        #region Core Display Name Methods

        public async Task<string> GetDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? UnknownUser;
            }

            // Try global cache first (this includes in-memory, memory cache, and database checks)
            var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            if (cached != null && !IsInvalidNameValue(cached.DisplayNameValue))
            {
                // We have valid cached data
                var age = DateTime.UtcNow - cached.LastUpdated;
                
                // Check if we should refresh in background (but don't wait for it)
                if (age > _refreshAge)
                {
                    _ = Task.Run(() => QueueNameRequestAsync(avatarId));
                }
                
                return FormatDisplayName(cached, mode);
            }

            // Check if we have a pending request to avoid duplicate network calls
            if (_pendingRequests.ContainsKey(avatarId))
            {
                return fallbackName ?? LoadingPlaceholder;
            }

            // No valid cached data, queue for network request
            await QueueNameRequestAsync(avatarId);
            
            return fallbackName ?? LoadingPlaceholder;
        }

        public string GetDisplayNameSync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? UnknownUser;
            }

            // First try synchronous cache (memory only)
            var cached = _globalCache.GetCachedDisplayName(avatarId, mode);
            if (!string.IsNullOrEmpty(cached) && !IsInvalidNameValue(cached))
            {
                // We have valid cached data, check if we should refresh in background
                _ = Task.Run(async () =>
                {
                    var cachedObject = await _globalCache.GetCachedDisplayNameAsync(avatarId);
                    if (cachedObject != null && DateTime.UtcNow - cachedObject.LastUpdated > _refreshAge)
                    {
                        await QueueNameRequestAsync(avatarId);
                    }
                });
                
                return cached;
            }

            // No memory cache hit, try async database check (with short timeout for sync method)
            try
            {
                var dbCheckTask = _globalCache.GetCachedDisplayNameAsync(avatarId);
                if (dbCheckTask.Wait(TimeSpan.FromMilliseconds(100))) // Short timeout for sync method
                {
                    var cachedObject = dbCheckTask.Result;
                    if (cachedObject != null && !IsInvalidNameValue(cachedObject.DisplayNameValue))
                    {
                        return FormatDisplayName(cachedObject, mode);
                    }
                }
            }
            catch (AggregateException)
            {
                // Timeout or error - fall through to queue request
            }

            // Check if we have a pending request to avoid duplicates
            if (!_pendingRequests.ContainsKey(avatarId))
            {
                // Queue request for background processing
                _ = Task.Run(() => QueueNameRequestAsync(avatarId));
            }
            
            return fallbackName ?? LoadingPlaceholder;
        }

        public async Task<string> GetLegacyNameAsync(string avatarId, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? UnknownUser;
            }

            // Check cache (includes in-memory, memory cache, and database)
            var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            if (cached?.LegacyFullName != null && !IsInvalidNameValue(cached.LegacyFullName))
            {
                // Check if we should refresh in background
                if (DateTime.UtcNow - cached.LastUpdated > _refreshAge)
                {
                    _ = Task.Run(() => QueueNameRequestAsync(avatarId));
                }
                return cached.LegacyFullName;
            }

            // Check if we already have a pending request
            if (!_pendingRequests.ContainsKey(avatarId))
            {
                await QueueNameRequestAsync(avatarId);
            }
            
            return fallbackName ?? LoadingPlaceholder;
        }

        public async Task<string> GetUserNameAsync(string avatarId, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName?.ToLower().Replace(" ", ".") ?? "unknown.user";
            }

            // Check cache (includes in-memory, memory cache, and database)
            var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            if (cached?.UserName != null && !IsInvalidNameValue(cached.UserName))
            {
                // Check if we should refresh in background
                if (DateTime.UtcNow - cached.LastUpdated > _refreshAge)
                {
                    _ = Task.Run(() => QueueNameRequestAsync(avatarId));
                }
                return cached.UserName;
            }

            // Check if we already have a pending request
            if (!_pendingRequests.ContainsKey(avatarId))
            {
                await QueueNameRequestAsync(avatarId);
            }
            
            return fallbackName?.ToLower().Replace(" ", ".") ?? LoadingPlaceholder.ToLower();
        }

        private string FormatDisplayName(DisplayName displayName, NameDisplayMode mode)
        {
            return mode switch
            {
                NameDisplayMode.Standard => displayName.LegacyFullName ?? displayName.DisplayNameValue ?? LoadingPlaceholder,
                NameDisplayMode.OnlyDisplayName => displayName.DisplayNameValue ?? LoadingPlaceholder,
                NameDisplayMode.Smart => GetSmartDisplayName(displayName),
                NameDisplayMode.DisplayNameAndUserName => GetDisplayNameAndUserName(displayName),
                _ => displayName.LegacyFullName ?? displayName.DisplayNameValue ?? LoadingPlaceholder
            };
        }

        private string GetSmartDisplayName(DisplayName displayName)
        {
            var displayPart = displayName.DisplayNameValue;
            var legacyName = displayName.LegacyFullName;
            
            // If this is a default display name (no custom display name set by user),
            // just return the cleaned display name value
            if (displayName.IsDefaultDisplayName && !IsInvalidNameValue(displayPart))
            {
                return displayPart;
            }
            
            // If we have both display name and legacy name, and they're different, show both
            if (!IsInvalidNameValue(displayPart) && !IsInvalidNameValue(legacyName) && displayPart != legacyName)
            {
                return $"{displayPart} ({legacyName})";
            }
            
            // If we only have display name (no legacy), just return display name
            if (!IsInvalidNameValue(displayPart))
            {
                return displayPart;
            }
            
            // Fall back to legacy name if no valid display name
            if (!IsInvalidNameValue(legacyName))
            {
                return legacyName;
            }
            
            // If we reach here, we have neither display name nor legacy name
            // This should be extremely rare - queue for immediate retry with high priority
            _logger.LogWarning("Avatar {AvatarId} has no display name or legacy name - queuing for immediate retry", 
                displayName.AvatarId);
            
            // Queue for quick retry as this is a critical missing name scenario
            QueueForRetry(displayName.AvatarId, "No display name or legacy name available", FailureType.MissingDisplayName);
            
            // Final fallback
            return LoadingPlaceholder;
        }

        private string GetDisplayNameAndUserName(DisplayName displayName)
        {
            var displayPart = displayName.DisplayNameValue ?? displayName.LegacyFullName;
            var userName = displayName.UserName ?? displayName.LegacyFullName;

            if (string.IsNullOrEmpty(displayPart) || displayPart == LoadingPlaceholder)
                return LoadingPlaceholder;
                
            if (displayPart == userName)
                return displayPart;

            return $"{displayPart} ({userName})";
        }

        public async Task<string> RefreshDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? UnknownUser;
            }

            // Force a fresh database check first
            var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            
            // If we have recent valid data (less than 30 minutes old), return it
            if (cached != null && !IsInvalidNameValue(cached.DisplayNameValue))
            {
                var age = DateTime.UtcNow - cached.LastUpdated;
                if (age < TimeSpan.FromMinutes(30))
                {
                    return FormatDisplayName(cached, mode);
                }
            }

            // Force a network request for this avatar (remove from pending to allow duplicate)
            _pendingRequests.TryRemove(avatarId, out _);
            await QueueNameRequestAsync(avatarId);
            
            // Wait a brief moment for the request to potentially complete
            await Task.Delay(100);
            
            // Try to get the updated data
            var refreshed = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            if (refreshed != null && !IsInvalidNameValue(refreshed.DisplayNameValue))
            {
                return FormatDisplayName(refreshed, mode);
            }
            
            return fallbackName ?? LoadingPlaceholder;
        }

        #endregion

        #region Update Methods

        public async Task UpdateDisplayNamesAsync(Dictionary<UUID, AgentDisplayName> displayNames)
        {
            await _globalCache.UpdateDisplayNamesAsync(displayNames);
        }

        public async Task UpdateLegacyNamesAsync(Dictionary<UUID, string> legacyNames)
        {
            await _globalCache.UpdateLegacyNamesAsync(legacyNames);
        }

        public async Task PreloadDisplayNamesAsync(IEnumerable<string> avatarIds)
        {
            await _globalCache.PreloadDisplayNamesAsync(avatarIds);
            
            // Also ensure these are loaded into our tracking to prevent immediate re-requests
            foreach (var avatarId in avatarIds)
            {
                var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
                if (cached != null && !IsInvalidNameValue(cached.DisplayNameValue))
                {
                    // Mark as recently processed to prevent immediate network requests
                    _pendingRequests.TryAdd(avatarId, DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1)));
                }
            }
        }

        public async Task RequestDisplayNamesAsync(List<string> avatarIds, Guid requestingAccountId)
        {
            await _globalCache.RequestDisplayNamesAsync(avatarIds, requestingAccountId);
        }

        #endregion

        #region Request Processing

        private async Task QueueNameRequestAsync(string avatarId)
        {
            if (_isDisposing || !await _requestWriter.WaitToWriteAsync())
                return;

            // Check if we already have a pending request for this avatar
            var now = DateTime.UtcNow;
            if (_pendingRequests.TryGetValue(avatarId, out var pendingTime))
            {
                // If the pending request is still recent (within timeout), skip
                if (now - pendingTime < _pendingRequestTimeout)
                {
                    return;
                }
                // Otherwise remove the old pending request
                _pendingRequests.TryRemove(avatarId, out _);
            }

            try
            {
                // Mark as pending before queuing
                _pendingRequests.TryAdd(avatarId, now);
                
                await _requestWriter.WriteAsync(new NameRequest 
                { 
                    AvatarId = avatarId, 
                    RequestTime = now,
                    Priority = RequestPriority.Normal 
                });
            }
            catch (InvalidOperationException)
            {
                // Channel closed during shutdown - remove from pending
                _pendingRequests.TryRemove(avatarId, out _);
            }
        }

        private void QueueForRetry(string avatarId, string? reason = null, FailureType failureType = FailureType.MissingDisplayName)
        {
            if (_isDisposing)
                return;

            // Check current retry attempts
            var currentAttempts = _retryAttempts.GetOrAdd(avatarId, 0);
            if (currentAttempts >= MaxRetryAttempts)
            {
                _logger.LogDebug("Avatar {AvatarId} has reached maximum retry attempts ({MaxAttempts}), giving up", 
                    avatarId, MaxRetryAttempts);
                _retryAttempts.TryRemove(avatarId, out _);
                _pendingRequests.TryRemove(avatarId, out _);
                return;
            }

            // Increment retry count
            _retryAttempts.AddOrUpdate(avatarId, 1, (key, value) => value + 1);
            var attemptNumber = _retryAttempts[avatarId];

            try
            {
                // Determine delay based on attempt number (progressive delays)
                var delay = attemptNumber switch
                {
                    1 => _firstRetryDelay,  // 5 seconds
                    2 => _secondRetryDelay, // 30 seconds  
                    3 => _thirdRetryDelay,  // 2 minutes
                    _ => _thirdRetryDelay   // Fallback (shouldn't happen with max 3 attempts)
                };

                var retryRequest = new RetryNameRequest
                {
                    AvatarId = avatarId,
                    FirstAttempt = DateTime.UtcNow,
                    NextRetryTime = DateTime.UtcNow.Add(delay),
                    AttemptCount = attemptNumber,
                    Priority = RequestPriority.Normal,
                    LastErrorReason = reason ?? "Request failed",
                    FailureType = failureType
                };

                if (!_retryWriter.TryWrite(retryRequest))
                {
                    _logger.LogWarning("Failed to queue retry request for avatar {AvatarId}: retry queue full", avatarId);
                }
                else
                {
                    _logger.LogDebug("Queued avatar {AvatarId} for retry attempt {AttemptCount}/{MaxAttempts} (delay: {Delay}): {Reason}", 
                        avatarId, attemptNumber, MaxRetryAttempts, delay, reason);
                }
            }
            catch (InvalidOperationException)
            {
                // Channel closed during shutdown - clean up tracking
                _retryAttempts.TryRemove(avatarId, out _);
                _pendingRequests.TryRemove(avatarId, out _);
            }
        }

        private async Task ProcessRequestQueueAsync(CancellationToken cancellationToken)
        {
            var batchedRequests = new HashSet<string>();
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for first request
                    if (await _requestReader.WaitToReadAsync(cancellationToken))
                    {
                        // Collect batch within time window
                        var batchDeadline = DateTime.UtcNow.Add(_batchDelay);
                        
                        while (DateTime.UtcNow < batchDeadline && batchedRequests.Count < MaxBatchSize)
                        {
                            if (_requestReader.TryRead(out var request))
                            {
                                batchedRequests.Add(request.AvatarId);
                            }
                            else
                            {
                                await Task.Delay(5, cancellationToken);
                            }
                        }

                        if (batchedRequests.Count > 0)
                        {
                            await ProcessBatchedRequests(batchedRequests.ToList());
                            batchedRequests.Clear();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing name request queue");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private async Task ProcessBatchedRequests(List<string> avatarIds)
        {
            // Filter out avatars we already have recent data for
            var needsRequest = new List<string>();
            
            foreach (var avatarId in avatarIds)
            {
                var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
                if (cached == null || DateTime.UtcNow - cached.LastUpdated > _refreshAge)
                {
                    needsRequest.Add(avatarId);
                }
                else
                {
                    // We have recent data, clean up tracking
                    MarkRequestCompleted(avatarId);
                }
            }

            if (needsRequest.Count == 0)
                return;

            // Find an active grid client
            var gridClient = GetAvailableGridClient();
            if (gridClient?.Network?.Connected != true)
            {
                _logger.LogWarning("No connected grid client available for display name requests");
                // Mark as failed and queue for retry
                foreach (var avatarId in needsRequest)
                {
                    QueueForRetry(avatarId, "No connected grid client available", FailureType.NetworkError);
                }
                return;
            }

            await _requestSemaphore.WaitAsync();
            try
            {
                await RequestNamesFromGrid(gridClient, needsRequest);
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        private void MarkRequestCompleted(string avatarId)
        {
            // Clean up tracking for successful requests
            _pendingRequests.TryRemove(avatarId, out _);
            _retryAttempts.TryRemove(avatarId, out _);
        }

        private void CleanupExpiredPendingRequests(DateTime now)
        {
            var expiredRequests = new List<string>();
            
            foreach (var kvp in _pendingRequests)
            {
                if (now - kvp.Value > _pendingRequestTimeout)
                {
                    expiredRequests.Add(kvp.Key);
                }
            }
            
            foreach (var avatarId in expiredRequests)
            {
                _pendingRequests.TryRemove(avatarId, out _);
                _logger.LogDebug("Cleaned up expired pending request for avatar {AvatarId}", avatarId);
            }
            
            if (expiredRequests.Count > 0)
            {
                _logger.LogDebug("Cleaned up {Count} expired pending requests", expiredRequests.Count);
            }
        }

        /// <summary>
        /// Cleanup tracking dictionaries to prevent unbounded growth (memory leak prevention)
        /// </summary>
        private void CleanupTrackingDictionaries(object? state)
        {
            try
            {
                var now = DateTime.UtcNow;
                
                // Cleanup expired pending requests (older than timeout)
                CleanupExpiredPendingRequests(now);
                
                // Cleanup retry attempts for avatars that have succeeded or exceeded max retries
                var expiredRetries = _retryAttempts
                    .Where(kvp => kvp.Value >= MaxRetryAttempts)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var avatarId in expiredRetries)
                {
                    _retryAttempts.TryRemove(avatarId, out _);
                }
                
                if (expiredRetries.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} exceeded retry attempts", expiredRetries.Count);
                }
                
                // Log dictionary sizes for monitoring
                _logger.LogDebug("MasterDisplayNameService dictionary sizes: PendingRequests={PendingCount}, RetryAttempts={RetryCount}, GridClients={ClientCount}, RegisteredAccounts={AccountCount}",
                    _pendingRequests.Count, _retryAttempts.Count, _gridClients.Count, _registeredAccounts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during tracking dictionary cleanup in MasterDisplayNameService");
            }
        }

        private async Task RequestNamesFromGrid(GridClient client, List<string> avatarIds)
        {
            var uuidList = avatarIds.Where(id => UUID.TryParse(id, out _))
                                  .Select(id => new UUID(id))
                                  .ToList();

            if (uuidList.Count == 0)
                return;

            var successful = new HashSet<string>();
            var failed = new HashSet<string>(avatarIds); // Start with all as failed, remove as they succeed

            try
            {
                if (client.Avatars.DisplayNamesAvailable())
                {
                    // Use display names API
                    await client.Avatars.GetDisplayNames(uuidList, 
                        (success, names, badIDs) =>
                        {
                            _ = Task.Run(async () =>
                            {
                                if (success && names?.Length > 0)
                                {
                                    var displayNameDict = names.ToDictionary(n => n.ID, n => n);
                                    await UpdateDisplayNamesAsync(displayNameDict);
                                    
                                    // Mark as successful and clean up tracking
                                    foreach (var name in names)
                                    {
                                        var avatarId = name.ID.ToString();
                                        successful.Add(avatarId);
                                        failed.Remove(avatarId);
                                        MarkRequestCompleted(avatarId);
                                    }
                                }
                                
                                // Handle failed IDs - try legacy names first, then queue for retry
                                if (badIDs?.Length > 0)
                                {
                                    var legacyUuids = badIDs.ToList();
                                    EventHandler<UUIDNameReplyEventArgs> legacyHandler = null!;
                                    legacyHandler = (sender, e) =>
                                    {
                                        client.Avatars.UUIDNameReply -= legacyHandler;
                                        _ = Task.Run(async () =>
                                        {
                                            // Update successful legacy names
                                            await UpdateLegacyNamesAsync(e.Names);
                                            foreach (var kvp in e.Names)
                                            {
                                                var avatarId = kvp.Key.ToString();
                                                successful.Add(avatarId);
                                                failed.Remove(avatarId);
                                                MarkRequestCompleted(avatarId);
                                            }
                                            
                                            // Queue remaining failed avatars for retry
                                            foreach (var uuid in legacyUuids)
                                            {
                                                var avatarId = uuid.ToString();
                                                if (failed.Contains(avatarId))
                                                {
                                                    QueueForRetry(avatarId, "Display name and legacy name requests failed", FailureType.NetworkError);
                                                }
                                            }
                                        });
                                    };

                                    client.Avatars.UUIDNameReply += legacyHandler;
                                    client.Avatars.RequestAvatarNames(legacyUuids);
                                }
                                else
                                {
                                    // No badIDs, but check if we got everything we expected
                                    foreach (var avatarId in failed.ToList())
                                    {
                                        if (!successful.Contains(avatarId))
                                        {
                                            QueueForRetry(avatarId, "Avatar not included in display name response", FailureType.MissingDisplayName);
                                        }
                                    }
                                }
                            });
                        });
                }
                else
                {
                    // Fall back to legacy names only
                    RequestLegacyNamesWithRetryTracking(client, uuidList, failed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting names from grid for {Count} avatars", uuidList.Count);
                
                // Queue all avatars for retry on exception
                foreach (var avatarId in avatarIds)
                {
                    QueueForRetry(avatarId, $"Exception during request: {ex.Message}", FailureType.NetworkError);
                }
            }
        }

        private void RequestLegacyNames(GridClient client, List<UUID> uuidList)
        {
            EventHandler<UUIDNameReplyEventArgs> handler = null!;
            handler = (sender, e) =>
            {
                client.Avatars.UUIDNameReply -= handler;
                _ = Task.Run(() => UpdateLegacyNamesAsync(e.Names));
            };

            client.Avatars.UUIDNameReply += handler;
            client.Avatars.RequestAvatarNames(uuidList);
        }

        private void RequestLegacyNamesWithRetryTracking(GridClient client, List<UUID> uuidList, HashSet<string> failed)
        {
            EventHandler<UUIDNameReplyEventArgs> handler = null!;
            handler = (sender, e) =>
            {
                client.Avatars.UUIDNameReply -= handler;
                _ = Task.Run(async () =>
                {
                    // Update successful legacy names
                    await UpdateLegacyNamesAsync(e.Names);
                    
                    // Mark successful ones and clean up tracking
                    foreach (var kvp in e.Names)
                    {
                        var avatarId = kvp.Key.ToString();
                        failed.Remove(avatarId);
                        MarkRequestCompleted(avatarId);
                    }
                    
                    // Queue remaining failed avatars for retry
                    foreach (var uuid in uuidList)
                    {
                        var avatarId = uuid.ToString();
                        if (failed.Contains(avatarId))
                        {
                            QueueForRetry(avatarId, "Legacy name request failed or returned no data", FailureType.MissingDisplayName);
                        }
                    }
                });
            };

            client.Avatars.UUIDNameReply += handler;
            client.Avatars.RequestAvatarNames(uuidList);
        }

        private async Task ProcessRetryQueueAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Retry queue processor started");
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for retry requests
                    if (await _retryReader.WaitToReadAsync(cancellationToken))
                    {
                        var retryBatch = new List<RetryNameRequest>();
                        var now = DateTime.UtcNow;

                        // Collect retry requests that are ready to be processed
                        while (_retryReader.TryRead(out var retryRequest))
                        {
                            if (retryRequest.NextRetryTime <= now)
                            {
                                // Check if we still need to retry this avatar
                                var cached = await _globalCache.GetCachedDisplayNameAsync(retryRequest.AvatarId);
                                if (cached == null || IsInvalidNameValue(cached.DisplayNameValue))
                                {
                                    retryBatch.Add(retryRequest);
                                }
                                else
                                {
                                    _logger.LogDebug("Avatar {AvatarId} now has valid name, skipping retry attempt {AttemptCount}", 
                                        retryRequest.AvatarId, retryRequest.AttemptCount);
                                }
                            }
                            else
                            {
                                // Not ready yet, put it back (this is inefficient but simple)
                                await _retryWriter.WriteAsync(retryRequest, cancellationToken);
                            }

                            if (retryBatch.Count >= MaxBatchSize)
                                break;
                        }

                        if (retryBatch.Count > 0)
                        {
                            _logger.LogInformation("Processing retry batch of {Count} avatars", retryBatch.Count);
                            await ProcessRetryBatch(retryBatch, cancellationToken);
                        }
                        else
                        {
                            // No ready requests, wait a bit
                            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Retry queue processor stopped");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing retry queue");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private async Task ProcessRetryBatch(List<RetryNameRequest> retryRequests, CancellationToken cancellationToken)
        {
            var avatarIds = retryRequests.Select(r => r.AvatarId).ToList();
            
            _logger.LogDebug("Processing retry batch for {Count} avatars, attempts: {Attempts}", 
                avatarIds.Count, 
                string.Join(",", retryRequests.Select(r => r.AttemptCount)));

            // Find an active grid client
            var gridClient = GetAvailableGridClient();
            if (gridClient?.Network?.Connected != true)
            {
                _logger.LogWarning("No connected grid client available for retry requests");
                RequeueFailedRetries(retryRequests);
                return;
            }

            await _requestSemaphore.WaitAsync(cancellationToken);
            try
            {
                var successful = new HashSet<string>();
                await RequestNamesFromGridWithRetry(gridClient, avatarIds, successful);
                
                // Requeue failed requests with exponential backoff
                var failed = retryRequests.Where(r => !successful.Contains(r.AvatarId)).ToList();
                RequeueFailedRetries(failed);
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        private async Task RequestNamesFromGridWithRetry(GridClient client, List<string> avatarIds, HashSet<string> successful)
        {
            var uuidList = avatarIds.Where(id => UUID.TryParse(id, out _))
                                  .Select(id => new UUID(id))
                                  .ToList();

            if (uuidList.Count == 0)
                return;

            try
            {
                var completionSource = new TaskCompletionSource<bool>();
                var receivedCount = 0;
                var expectedCount = uuidList.Count;

                if (client.Avatars.DisplayNamesAvailable())
                {
                    // Use display names API
                    await client.Avatars.GetDisplayNames(uuidList, 
                        (success, names, badIDs) =>
                        {
                            _ = Task.Run(async () =>
                            {
                                if (success && names?.Length > 0)
                                {
                                    var displayNameDict = names.ToDictionary(n => n.ID, n => n);
                                    await UpdateDisplayNamesAsync(displayNameDict);
                                    
                                    // Mark as successful
                                    foreach (var name in names)
                                    {
                                        successful.Add(name.ID.ToString());
                                    }
                                }
                                
                                receivedCount++;
                                if (receivedCount >= expectedCount)
                                {
                                    completionSource.TrySetResult(true);
                                }
                            });
                        });
                }
                else
                {
                    // Fall back to legacy names
                    EventHandler<UUIDNameReplyEventArgs> handler = null!;
                    handler = (sender, e) =>
                    {
                        client.Avatars.UUIDNameReply -= handler;
                        _ = Task.Run(async () =>
                        {
                            await UpdateLegacyNamesAsync(e.Names);
                            foreach (var kvp in e.Names)
                            {
                                successful.Add(kvp.Key.ToString());
                            }
                            completionSource.TrySetResult(true);
                        });
                    };

                    client.Avatars.UUIDNameReply += handler;
                    client.Avatars.RequestAvatarNames(uuidList);
                }

                // Wait for completion with timeout
                await Task.WhenAny(
                    completionSource.Task,
                    Task.Delay(TimeSpan.FromSeconds(10))
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting names from grid for retry batch");
            }
        }

        private void RequeueFailedRetries(List<RetryNameRequest> failedRequests)
        {
            foreach (var request in failedRequests)
            {
                // Check if we should continue retrying this avatar
                if (request.AttemptCount >= MaxRetryAttempts)
                {
                    _logger.LogDebug("Giving up on avatar {AvatarId} after {Attempts} attempts", 
                        request.AvatarId, request.AttemptCount);
                    
                    // Clean up tracking for this avatar
                    _retryAttempts.TryRemove(request.AvatarId, out _);
                    _pendingRequests.TryRemove(request.AvatarId, out _);
                    continue;
                }

                // Calculate delay based on attempt number
                var delay = request.AttemptCount switch
                {
                    1 => _secondRetryDelay,  // 30 seconds for second attempt
                    2 => _thirdRetryDelay,   // 2 minutes for third attempt  
                    _ => _thirdRetryDelay    // Fallback
                };

                request.AttemptCount++;
                request.NextRetryTime = DateTime.UtcNow.Add(delay);
                request.LastErrorReason = "Previous request failed or incomplete";

                // Update retry tracking
                _retryAttempts.AddOrUpdate(request.AvatarId, request.AttemptCount, (key, value) => request.AttemptCount);

                if (!_retryWriter.TryWrite(request))
                {
                    _logger.LogWarning("Failed to requeue retry request for {AvatarId}", request.AvatarId);
                    // Clean up tracking if we can't requeue
                    _retryAttempts.TryRemove(request.AvatarId, out _);
                    _pendingRequests.TryRemove(request.AvatarId, out _);
                }
                else
                {
                    _logger.LogDebug("Requeued avatar {AvatarId} for attempt {AttemptCount}/{MaxAttempts} (delay: {Delay})", 
                        request.AvatarId, request.AttemptCount, MaxRetryAttempts, delay);
                }
            }
        }

        #endregion

        #region Periodic Processing

        private async void ProcessPeriodicRefresh(object? state)
        {
            if (_isDisposing)
                return;

            try
            {
                var avatarsToRefresh = new List<string>();
                var now = DateTime.UtcNow;

                // Clean up expired pending requests
                CleanupExpiredPendingRequests(now);

                // Collect avatars from all registered accounts
                foreach (var accountId in _registeredAccounts.Keys)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                        var instance = accountService.GetInstance(accountId);
                        
                        if (instance?.IsConnected == true)
                        {
                            var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                            foreach (var avatar in nearbyAvatars.Take(MaxPeriodicAvatars / Math.Max(_registeredAccounts.Count, 1)))
                            {
                                var cached = await _globalCache.GetCachedDisplayNameAsync(avatar.Id);
                                if (cached == null || now - cached.LastUpdated > _refreshAge)
                                {
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

                // Queue avatars for refresh
                foreach (var avatarId in avatarsToRefresh.Take(MaxPeriodicAvatars))
                {
                    await QueueNameRequestAsync(avatarId);
                }

                if (avatarsToRefresh.Count > 0)
                {
                    _logger.LogDebug("Periodic refresh queued {Count} avatars", avatarsToRefresh.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic display name refresh");
            }
        }

        #endregion

        #region Account Management

        public void RegisterGridClient(Guid accountId, GridClient client)
        {
            _gridClients.AddOrUpdate(accountId, client, (key, existing) => client);
            _globalCache.RegisterGridClient(accountId, client);
            
            // Also register this account for periodic processing
            RegisterAccount(accountId);
            
            _logger.LogDebug("Registered grid client for account {AccountId}", accountId);
        }

        public void UnregisterGridClient(Guid accountId)
        {
            _gridClients.TryRemove(accountId, out _);
            _globalCache.UnregisterGridClient(accountId);
            
            _logger.LogDebug("Unregistered grid client for account {AccountId}", accountId);
        }

        public void RegisterAccount(Guid accountId)
        {
            _registeredAccounts.TryAdd(accountId, true);
            _logger.LogDebug("Registered account {AccountId} for periodic processing", accountId);
        }

        public void UnregisterAccount(Guid accountId)
        {
            _registeredAccounts.TryRemove(accountId, out _);
            _logger.LogDebug("Unregistered account {AccountId} from periodic processing", accountId);
        }

        public void CleanupAccount(Guid accountId)
        {
            UnregisterAccount(accountId);
            UnregisterGridClient(accountId);
        }

        private GridClient? GetAvailableGridClient()
        {
            return _gridClients.Values.FirstOrDefault(client => client?.Network?.Connected == true);
        }

        #endregion

        #region Cache Management

        public async Task LoadCacheAsync()
        {
            await _globalCache.LoadCachedNamesAsync();
        }

        public async Task SaveCacheAsync()
        {
            await _globalCache.SaveCacheAsync();
        }

        public void CleanExpiredCache()
        {
            _globalCache.CleanExpiredCache();
        }

        public async Task<int> CleanupOldCachedNamesAsync(int keepDays = 60)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                using var context = scope.ServiceProvider.GetRequiredService<Data.RadegastDbContext>();
                
                var cutoffDate = DateTime.UtcNow.AddDays(-keepDays);
                
                // Use ExecuteDeleteAsync for efficient bulk deletion
                int deletedCount = await context.GlobalDisplayNames
                    .Where(n => n.CachedAt < cutoffDate)
                    .ExecuteDeleteAsync();
                
                if (deletedCount > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} old cached display names older than {Days} days", 
                        deletedCount, keepDays);
                        
                    // Clear memory cache for deleted entries to avoid stale data
                    _globalCache.CleanExpiredCache();
                }
                else
                {
                    _logger.LogDebug("No old cached display names found to cleanup (older than {Days} days)", keepDays);
                }
                
                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old cached display names");
                return 0;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets retry queue statistics for monitoring
        /// </summary>
        public (int PendingRetries, int ReadyForRetry, int TotalRequests, Dictionary<string, int> FailureTypes) GetQueueStatistics()
        {
            var requestCount = _requestChannel.Reader.Count;
            var retryCount = _retryChannel.Reader.Count;
            
            // Count failure types (this is approximate since we can't easily inspect channel contents)
            var failureTypes = new Dictionary<string, int>
            {
                ["MissingDisplayName"] = 0,
                ["NetworkError"] = 0,
                ["TemporaryFailure"] = 0
            };
            
            // Note: In a real implementation, you might want to maintain these counters separately
            // since we can't inspect the channel contents without consuming them
            
            return (retryCount, retryCount, requestCount + retryCount, failureTypes);
        }

        private static bool IsInvalidNameValue(string? nameValue)
        {
            return string.IsNullOrWhiteSpace(nameValue) || 
                   nameValue.Equals(LoadingPlaceholder, StringComparison.OrdinalIgnoreCase) ||
                   nameValue.Equals("???", StringComparison.OrdinalIgnoreCase) ||
                   nameValue.Equals(UnknownUser, StringComparison.OrdinalIgnoreCase) ||
                   nameValue.Equals("Resolving...", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Disposal

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _isDisposing = true;
            
            _periodicTimer?.Dispose();
            _cancellationTokenSource.Cancel();
            
            // Complete the request channel
            try
            {
                _requestWriter.Complete();
                _retryWriter.Complete();
            }
            catch (InvalidOperationException)
            {
                // Channel already completed
            }
            
            // Clear tracking dictionaries
            _pendingRequests.Clear();
            _retryAttempts.Clear();
            
            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            _isDisposing = true;
            
            _periodicTimer?.Dispose();
            _requestSemaphore?.Dispose();
            _cancellationTokenSource?.Dispose();
            
            // Cleanup all registered accounts
            foreach (var accountId in _registeredAccounts.Keys.ToList())
            {
                CleanupAccount(accountId);
            }
            
            // Clear tracking dictionaries
            _pendingRequests.Clear();
            _retryAttempts.Clear();
            
            base.Dispose();
        }

        #endregion
    }

    // Supporting classes
    internal class NameRequest
    {
        public string AvatarId { get; set; } = string.Empty;
        public DateTime RequestTime { get; set; }
        public RequestPriority Priority { get; set; }
    }

    internal class RetryNameRequest
    {
        public string AvatarId { get; set; } = string.Empty;
        public DateTime FirstAttempt { get; set; }
        public DateTime NextRetryTime { get; set; }
        public int AttemptCount { get; set; }
        public RequestPriority Priority { get; set; }
        public string? LastErrorReason { get; set; }
        public FailureType FailureType { get; set; } = FailureType.MissingDisplayName;
    }

    internal enum RequestPriority
    {
        Low,
        Normal,
        High
    }

    internal enum FailureType
    {
        MissingDisplayName,  // Avatar doesn't have a display name - use quick retries
        NetworkError,        // Network/server issue - use exponential backoff
        TemporaryFailure     // Temporary issue - moderate retry delay
    }
}