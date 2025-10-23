using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
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
        
        // Events
        event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
        
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
        private readonly TimeSpan _quickRetryDelay = TimeSpan.FromSeconds(3); // Quick retry for missing names
        private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(1); // Initial retry delay for network issues
        private readonly TimeSpan _maxRetryDelay = TimeSpan.FromMinutes(10); // Max retry delay
        private const int MaxBatchSize = 20;
        private const int MaxPeriodicAvatars = 20;
        private const int MaxRetryAttempts = 5;
        private const int MaxQuickRetryAttempts = 5; // Quick retries for missing display names
        
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
                
                // Start periodic processing timer
                _periodicTimer = new Timer(ProcessPeriodicRefresh, null, _periodicInterval, _periodicInterval);

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

            // Try global cache first
            var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            if (cached != null)
            {
                // Check if we should refresh (but don't wait for it)
                if (DateTime.UtcNow - cached.LastUpdated > _refreshAge)
                {
                    _ = Task.Run(() => QueueNameRequestAsync(avatarId));
                }
                
                return FormatDisplayName(cached, mode);
            }

            // Not in cache, queue for background request
            await QueueNameRequestAsync(avatarId);
            
            return fallbackName ?? LoadingPlaceholder;
        }

        public string GetDisplayNameSync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? UnknownUser;
            }

            // Use cached synchronous method for immediate response
            var cached = _globalCache.GetCachedDisplayName(avatarId, mode);
            if (cached != null)
            {
                // Queue refresh if needed (fire and forget)
                var cachedObject = _globalCache.GetCachedDisplayNameAsync(avatarId).Result;
                if (cachedObject != null && DateTime.UtcNow - cachedObject.LastUpdated > _refreshAge)
                {
                    _ = Task.Run(() => QueueNameRequestAsync(avatarId));
                }
                
                return cached;
            }

            // Queue request for background processing
            _ = Task.Run(() => QueueNameRequestAsync(avatarId));
            
            return fallbackName ?? LoadingPlaceholder;
        }

        public async Task<string> GetLegacyNameAsync(string avatarId, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? UnknownUser;
            }

            var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            if (cached?.LegacyFullName != null)
            {
                return cached.LegacyFullName;
            }

            await QueueNameRequestAsync(avatarId);
            return fallbackName ?? LoadingPlaceholder;
        }

        public async Task<string> GetUserNameAsync(string avatarId, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName?.ToLower().Replace(" ", ".") ?? "unknown.user";
            }

            var cached = await _globalCache.GetCachedDisplayNameAsync(avatarId);
            if (cached?.UserName != null)
            {
                return cached.UserName;
            }

            await QueueNameRequestAsync(avatarId);
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
                return legacyName;
                
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

            try
            {
                await _requestWriter.WriteAsync(new NameRequest 
                { 
                    AvatarId = avatarId, 
                    RequestTime = DateTime.UtcNow,
                    Priority = RequestPriority.Normal 
                });
            }
            catch (InvalidOperationException)
            {
                // Channel closed during shutdown
            }
        }

        private void QueueForRetry(string avatarId, string? reason = null, FailureType failureType = FailureType.MissingDisplayName)
        {
            if (_isDisposing)
                return;

            try
            {
                // Determine initial delay based on failure type
                var initialDelay = failureType switch
                {
                    FailureType.MissingDisplayName => _quickRetryDelay, // 3 seconds
                    FailureType.NetworkError => _retryDelay,          // 1 minute
                    FailureType.TemporaryFailure => TimeSpan.FromSeconds(10), // 10 seconds
                    _ => _quickRetryDelay
                };

                var retryRequest = new RetryNameRequest
                {
                    AvatarId = avatarId,
                    FirstAttempt = DateTime.UtcNow,
                    NextRetryTime = DateTime.UtcNow.Add(initialDelay),
                    AttemptCount = 1,
                    Priority = RequestPriority.Normal,
                    LastErrorReason = reason ?? "Initial request failed",
                    FailureType = failureType
                };

                if (!_retryWriter.TryWrite(retryRequest))
                {
                    _logger.LogWarning("Failed to queue retry request for avatar {AvatarId}: retry queue full", avatarId);
                }
                else
                {
                    _logger.LogDebug("Queued avatar {AvatarId} for retry ({FailureType}): {Reason}", avatarId, failureType, reason);
                }
            }
            catch (InvalidOperationException)
            {
                // Channel closed during shutdown
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
            }

            if (needsRequest.Count == 0)
                return;

            // Find an active grid client
            var gridClient = GetAvailableGridClient();
            if (gridClient?.Network?.Connected != true)
            {
                _logger.LogWarning("No connected grid client available for display name requests");
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
                                    
                                    // Mark as successful
                                    foreach (var name in names)
                                    {
                                        var avatarId = name.ID.ToString();
                                        successful.Add(avatarId);
                                        failed.Remove(avatarId);
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
                    
                    // Mark successful ones
                    foreach (var kvp in e.Names)
                    {
                        failed.Remove(kvp.Key.ToString());
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
                // Check retry limits based on failure type
                var maxAttempts = request.FailureType == FailureType.MissingDisplayName 
                    ? MaxQuickRetryAttempts 
                    : MaxRetryAttempts;

                if (request.AttemptCount >= maxAttempts)
                {
                    _logger.LogDebug("Giving up on avatar {AvatarId} after {Attempts} attempts (type: {FailureType})", 
                        request.AvatarId, request.AttemptCount, request.FailureType);
                    continue;
                }

                // Calculate delay based on failure type
                TimeSpan delay;
                switch (request.FailureType)
                {
                    case FailureType.MissingDisplayName:
                        // Quick retries: 3s, 3s, 3s, 3s, 3s (total: 15 seconds)
                        delay = _quickRetryDelay;
                        break;
                    
                    case FailureType.NetworkError:
                        // Exponential backoff: 1min, 2min, 4min, 8min, 10min(max)
                        delay = TimeSpan.FromMinutes(Math.Min(
                            Math.Pow(2, request.AttemptCount), 
                            _maxRetryDelay.TotalMinutes));
                        break;
                    
                    case FailureType.TemporaryFailure:
                        // Linear increase: 10s, 20s, 30s, 40s, 50s
                        delay = TimeSpan.FromSeconds(Math.Min(10 * (request.AttemptCount + 1), 60));
                        break;
                    
                    default:
                        delay = _quickRetryDelay;
                        break;
                }

                request.AttemptCount++;
                request.NextRetryTime = DateTime.UtcNow.Add(delay);
                request.LastErrorReason = "Previous request failed or incomplete";

                if (!_retryWriter.TryWrite(request))
                {
                    _logger.LogWarning("Failed to requeue retry request for {AvatarId}", request.AvatarId);
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
            }
            catch (InvalidOperationException)
            {
                // Channel already completed
            }
            
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