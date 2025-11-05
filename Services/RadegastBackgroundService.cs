using Microsoft.AspNetCore.SignalR;
using RadegastWeb.Core;
using RadegastWeb.Hubs;
using RadegastWeb.Models;
using RadegastWeb.Services;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public class RadegastBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<RadegastBackgroundService> _logger;
        private readonly IHubContext<RadegastHub, IRadegastHubClient> _hubContext;
        private readonly IConnectionTrackingService _connectionTrackingService;
        private IPresenceService? _presenceService;
        private IRegionInfoService? _regionInfoService;
        private IGroupService? _groupService;
        private volatile bool _isShuttingDown = false;
        private DateTime _lastDayCheck = DateTime.MinValue; // Will be initialized properly in ExecuteAsync
        
        // Track which instances we've already subscribed to avoid multiple subscriptions
        private readonly ConcurrentDictionary<Guid, bool> _subscribedInstances = new();
        private readonly ConcurrentDictionary<Guid, DateTime> _lastSubscriptionTime = new();
        private readonly ConcurrentDictionary<Guid, WeakReference<WebRadegastInstance>> _instanceReferences = new();
        private readonly object _subscriptionLock = new();
        
        // Avatar update debouncing to prevent rapid-fire broadcasts
        private readonly ConcurrentDictionary<Guid, DateTime> _lastAvatarUpdateBroadcast = new();
        private readonly ConcurrentDictionary<Guid, Timer> _avatarUpdateTimers = new();
        private readonly TimeSpan _avatarUpdateDebounceInterval = TimeSpan.FromMilliseconds(500); // 500ms debounce

        public RadegastBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<RadegastBackgroundService> logger,
            IHubContext<RadegastHub, IRadegastHubClient> hubContext,
            IConnectionTrackingService connectionTrackingService)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _hubContext = hubContext;
            _connectionTrackingService = connectionTrackingService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Radegast Background Service started");

            try
            {
                // Initialize services
                using var scope = _serviceProvider.CreateScope();
                _presenceService = scope.ServiceProvider.GetRequiredService<IPresenceService>();
                _presenceService.PresenceStatusChanged += OnPresenceStatusChanged;

                _regionInfoService = scope.ServiceProvider.GetRequiredService<IRegionInfoService>();
                _regionInfoService.RegionStatsUpdated += OnRegionStatsUpdated;

                _groupService = scope.ServiceProvider.GetRequiredService<IGroupService>();
                _groupService.GroupsUpdated += OnGroupsUpdated;

                // Initialize _lastDayCheck with current SLT date
                var sltTimeService = scope.ServiceProvider.GetRequiredService<ISLTimeService>();
                _lastDayCheck = sltTimeService.GetCurrentSLT().Date;
                
                var lastPeriodicRecording = DateTime.UtcNow;

                while (!stoppingToken.IsCancellationRequested && !_isShuttingDown)
                {
                    try
                    {
                        await ProcessAccountEvents(stoppingToken);
                        
                        // Check for day boundary transitions
                        await CheckDayBoundaryAsync(stoppingToken);
                        
                        // Periodic avatar recording (every 10 minutes) to ensure existing avatars get counted
                        var now = DateTime.UtcNow;
                        if (now - lastPeriodicRecording >= TimeSpan.FromMinutes(10))
                        {
                            await TriggerPeriodicAvatarRecordingAsync(stoppingToken);
                            lastPeriodicRecording = now;
                        }
                        
                        // Periodic cleanup of disconnected accounts (every 5 minutes) to ensure proper status
                        await CleanupDisconnectedAccountsAsync(stoppingToken);
                        
                        // Periodic avatar state broadcast (every 10 minutes) to ensure all clients have current data
                        await PeriodicAvatarStateBroadcastAsync(stoppingToken);
                        
                        // More frequent stale connection cleanup (every 2 minutes)
                        await PeriodicStaleConnectionCleanupAsync(stoppingToken);
                        
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancellation is requested
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Service provider disposed during shutdown
                        _logger.LogDebug("Service provider disposed, stopping background service");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in RadegastBackgroundService");
                        
                        if (!stoppingToken.IsCancellationRequested && !_isShuttingDown)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        }
                    }
                }
            }
            finally
            {
                _isShuttingDown = true;
                
                // Cleanup event subscriptions
                if (_presenceService != null)
                {
                    _presenceService.PresenceStatusChanged -= OnPresenceStatusChanged;
                }
                if (_regionInfoService != null)
                {
                    _regionInfoService.RegionStatsUpdated -= OnRegionStatsUpdated;
                }
                if (_groupService != null)
                {
                    _groupService.GroupsUpdated -= OnGroupsUpdated;
                }
                
                // Clear subscription tracking
                _subscribedInstances.Clear();
                _lastSubscriptionTime.Clear();
                
                _logger.LogInformation("Radegast Background Service stopped");
            }
        }

        private async Task ProcessAccountEvents(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();

            var accounts = await accountService.GetAccountsAsync();
            var currentAccountIds = accounts.Select(a => a.Id).ToHashSet();
            
            // Clean up subscriptions for instances that no longer exist
            var obsoleteInstances = _subscribedInstances.Keys
                .Where(id => !currentAccountIds.Contains(id))
                .ToList();
            
            foreach (var obsoleteId in obsoleteInstances)
            {
                _subscribedInstances.TryRemove(obsoleteId, out _);
                _lastSubscriptionTime.TryRemove(obsoleteId, out _);
                _instanceReferences.TryRemove(obsoleteId, out _);
                _logger.LogDebug("Cleaned up obsolete instance subscription for account {AccountId}", obsoleteId);
            }
            
            foreach (var account in accounts)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var instance = accountService.GetInstance(account.Id);
                if (instance != null)
                {
                    // Always ensure proper subscription - check if we need to (re)subscribe
                    var needsSubscription = false;
                    
                    if (!_subscribedInstances.ContainsKey(account.Id))
                    {
                        needsSubscription = true;
                        _logger.LogDebug("New instance detected for account {AccountId}, subscribing to events", account.Id);
                    }
                    else
                    {
                        // Check if subscription needs refresh based on time and connection activity
                        var hasActiveConnections = _connectionTrackingService.HasActiveConnections(account.Id);
                        var now = DateTime.UtcNow;
                        
                        if (hasActiveConnections && instance.IsConnected)
                        {
                            // Get last subscription time for this account
                            var lastSubscriptionTime = _lastSubscriptionTime.GetValueOrDefault(account.Id, DateTime.MinValue);
                            var timeSinceLastSubscription = now - lastSubscriptionTime;
                            
                            // Refresh subscriptions less aggressively to avoid interference:
                            // - Every 15 minutes for accounts with active web connections
                            // - Every 10 minutes if we've never recorded a subscription time (safety net)
                            var refreshInterval = lastSubscriptionTime == DateTime.MinValue 
                                ? TimeSpan.FromMinutes(10) 
                                : TimeSpan.FromMinutes(15);
                            
                            if (timeSinceLastSubscription >= refreshInterval)
                            {
                                needsSubscription = true;
                                _logger.LogInformation("Performing scheduled event subscription refresh for account {AccountId} (last refresh: {LastRefresh:yyyy-MM-dd HH:mm:ss} UTC, interval: {Interval})", 
                                    account.Id, lastSubscriptionTime, refreshInterval);
                                
                                // Remove from tracking to force refresh
                                _subscribedInstances.TryRemove(account.Id, out _);
                            }
                        }
                        else if (!hasActiveConnections)
                        {
                            // For accounts without active connections, refresh much less frequently (every 30 minutes)
                            // This keeps the subscriptions alive but doesn't waste resources
                            var lastSubscriptionTime = _lastSubscriptionTime.GetValueOrDefault(account.Id, DateTime.MinValue);
                            var timeSinceLastSubscription = now - lastSubscriptionTime;
                            
                            if (timeSinceLastSubscription >= TimeSpan.FromMinutes(30))
                            {
                                needsSubscription = true;
                                _logger.LogDebug("Performing low-priority event subscription refresh for account {AccountId} (no active connections)", account.Id);
                                
                                // Remove from tracking to force refresh
                                _subscribedInstances.TryRemove(account.Id, out _);
                            }
                        }
                    }
                    
                    if (needsSubscription)
                    {
                        _logger.LogInformation("Subscribing to events for account instance {AccountId}", account.Id);
                        
                        // First, safely unsubscribe to prevent double subscriptions
                        SafeUnsubscribeFromInstance(account.Id, instance);
                        
                        // Now subscribe
                        instance.ChatReceived += OnChatReceived;
                        instance.StatusChanged += OnStatusChanged;
                        instance.ConnectionChanged += OnConnectionChanged;
                        instance.ChatSessionUpdated += OnChatSessionUpdated;
                        instance.AvatarAdded += OnAvatarAdded;
                        instance.AvatarRemoved += OnAvatarRemoved;
                        instance.AvatarUpdated += OnAvatarUpdated;
                        instance.RegionChanged += OnRegionChanged;
                        instance.NoticeReceived += OnNoticeReceived;
                        instance.ScriptDialogReceived += OnScriptDialogReceived;
                        instance.ScriptPermissionReceived += OnScriptPermissionReceived;
                        instance.TeleportRequestReceived += OnTeleportRequestReceived;
                        
                        // Track instance with weak reference to prevent GC issues
                        _instanceReferences.AddOrUpdate(account.Id, 
                            new WeakReference<WebRadegastInstance>(instance),
                            (key, old) => new WeakReference<WebRadegastInstance>(instance));
                        
                        // Mark as subscribed and record the time
                        _subscribedInstances.TryAdd(account.Id, true);
                        _lastSubscriptionTime.AddOrUpdate(account.Id, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
                    }
                }
                else if (_subscribedInstances.ContainsKey(account.Id))
                {
                    // Instance no longer exists but we have it tracked - clean up
                    _subscribedInstances.TryRemove(account.Id, out _);
                    _lastSubscriptionTime.TryRemove(account.Id, out _);
                    _instanceReferences.TryRemove(account.Id, out _);
                    _logger.LogDebug("Cleaned up subscription for disappeared instance {AccountId}", account.Id);
                }
            }
        }

        /// <summary>
        /// Force refresh all event subscriptions for all active accounts
        /// This helps when SignalR reconnects but subscriptions are lost
        /// </summary>
        public Task RefreshAllAccountSubscriptionsAsync()
        {
            try
            {
                _logger.LogInformation("Forcing refresh of all account event subscriptions");
                
                // Clear all existing subscriptions to force complete refresh
                _subscribedInstances.Clear();
                _lastSubscriptionTime.Clear();
                _instanceReferences.Clear();
                
                _logger.LogInformation("Cleared all existing event subscription tracking - next ProcessAccountEvents cycle will re-subscribe all active accounts");
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing all account event subscriptions");
                return Task.FromException(ex);
            }
        }

        /// <summary>
        /// Force refresh event subscriptions for a specific account
        /// This helps when avatar events stop flowing to the web client despite radar working
        /// </summary>
        public Task RefreshAccountSubscriptionAsync(Guid accountId)
        {
            try
            {
                _logger.LogInformation("Forcing event subscription refresh for account {AccountId}", accountId);
                
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                
                var instance = accountService.GetInstance(accountId);
                if (instance == null)
                {
                    _logger.LogWarning("Cannot refresh events for account {AccountId} - instance not found", accountId);
                    return Task.CompletedTask;
                }

                // Remove from subscription tracking to force re-subscription
                // Always safely unsubscribe first to prevent accumulation
                SafeUnsubscribeFromInstance(accountId, instance);
                
                // Remove from tracking
                _subscribedInstances.TryRemove(accountId, out _);
                _instanceReferences.TryRemove(accountId, out _);

                // Re-subscribe to all events
                _logger.LogInformation("Re-subscribing to events for account {AccountId}", accountId);
                
                instance.ChatReceived += OnChatReceived;
                instance.StatusChanged += OnStatusChanged;
                instance.ConnectionChanged += OnConnectionChanged;
                instance.ChatSessionUpdated += OnChatSessionUpdated;
                instance.AvatarAdded += OnAvatarAdded;
                instance.AvatarRemoved += OnAvatarRemoved;
                instance.AvatarUpdated += OnAvatarUpdated;
                instance.RegionChanged += OnRegionChanged;
                instance.NoticeReceived += OnNoticeReceived;
                instance.ScriptDialogReceived += OnScriptDialogReceived;
                instance.ScriptPermissionReceived += OnScriptPermissionReceived;
                instance.TeleportRequestReceived += OnTeleportRequestReceived;
                
                // Track instance with weak reference
                _instanceReferences.AddOrUpdate(accountId, 
                    new WeakReference<WebRadegastInstance>(instance),
                    (key, old) => new WeakReference<WebRadegastInstance>(instance));
                
                // Track the subscription
                _subscribedInstances.TryAdd(accountId, true);
                _lastSubscriptionTime.AddOrUpdate(accountId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
                
                _logger.LogInformation("Event subscription refresh completed for account {AccountId} - avatar events should now flow to web client", accountId);
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing event subscriptions for account {AccountId}", accountId);
                return Task.FromException(ex);
            }
        }

        private void OnChatReceived(object? sender, ChatMessageDto chatMessage)
        {
            try
            {
                // The unified ChatProcessingService now handles database saving, broadcasting, AI, and Corrade processing
                // This event handler is kept for any additional processing or backwards compatibility
                // The actual processing is now done in WebRadegastInstance using ChatProcessingService
                
                _logger.LogDebug("Chat message received via event (processing handled by ChatProcessingService): {SenderName} in {ChatType}", 
                    chatMessage.SenderName, chatMessage.ChatType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in chat received event handler");
            }
        }

        private async void OnStatusChanged(object? sender, string status)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                // Get service status information
                (bool hasAiBotActive, bool hasCorradeActive) = await GetServiceStatusAsync(Guid.Parse(instance.AccountId));

                var accountStatus = new AccountStatus
                {
                    AccountId = Guid.Parse(instance.AccountId),
                    FirstName = instance.AccountInfo.FirstName,
                    LastName = instance.AccountInfo.LastName,
                    DisplayName = instance.AccountInfo.DisplayName,
                    IsConnected = instance.IsConnected,
                    Status = status,
                    CurrentRegion = instance.AccountInfo.CurrentRegion,
                    LastLoginAt = instance.AccountInfo.LastLoginAt,
                    AvatarUuid = instance.AccountInfo.AvatarUuid,
                    AvatarRelayUuid = instance.AccountInfo.AvatarRelayUuid,
                    GridUrl = instance.AccountInfo.GridUrl,
                    HasAiBotActive = hasAiBotActive,
                    HasCorradeActive = hasCorradeActive
                };

                await _hubContext.Clients
                    .Group($"account_{instance.AccountId}")
                    .AccountStatusChanged(accountStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting status change");
            }
        }

        private async void OnConnectionChanged(object? sender, bool isConnected)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                // Get service status information
                (bool hasAiBotActive, bool hasCorradeActive) = await GetServiceStatusAsync(Guid.Parse(instance.AccountId));

                var accountStatus = new AccountStatus
                {
                    AccountId = Guid.Parse(instance.AccountId),
                    FirstName = instance.AccountInfo.FirstName,
                    LastName = instance.AccountInfo.LastName,
                    DisplayName = instance.AccountInfo.DisplayName,
                    IsConnected = isConnected,
                    Status = instance.Status,
                    CurrentRegion = instance.AccountInfo.CurrentRegion,
                    LastLoginAt = instance.AccountInfo.LastLoginAt,
                    AvatarUuid = instance.AccountInfo.AvatarUuid,
                    AvatarRelayUuid = instance.AccountInfo.AvatarRelayUuid,
                    GridUrl = instance.AccountInfo.GridUrl,
                    HasAiBotActive = hasAiBotActive,
                    HasCorradeActive = hasCorradeActive
                };

                await _hubContext.Clients
                    .Group($"account_{instance.AccountId}")
                    .AccountStatusChanged(accountStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting connection change");
            }
        }

        /// <summary>
        /// Gets the service status (AI Bot and Corrade) for a specific account
        /// </summary>
        /// <param name="accountId">The account ID to check</param>
        /// <returns>Tuple containing (hasAiBotActive, hasCorradeActive)</returns>
        private async Task<(bool hasAiBotActive, bool hasCorradeActive)> GetServiceStatusAsync(Guid accountId)
        {
            var hasAiBotActive = false;
            var hasCorradeActive = false;

            // Early return if shutting down
            if (_isShuttingDown)
            {
                _logger.LogDebug("Service is shutting down, returning default service status for account {AccountId}", accountId);
                return (hasAiBotActive, hasCorradeActive);
            }

            try
            {
                // Check if the service provider is disposed
                if (_serviceProvider == null)
                {
                    _logger.LogDebug("Service provider is null, returning default service status for account {AccountId}", accountId);
                    return (hasAiBotActive, hasCorradeActive);
                }

                using var scope = _serviceProvider.CreateScope();
                
                // Check AI Bot status
                var aiChatService = scope.ServiceProvider.GetService<IAiChatService>();
                if (aiChatService?.IsEnabled == true)
                {
                    var aiConfig = aiChatService.GetConfiguration();
                    if (aiConfig != null)
                    {
                        // If LinkedAccountId is null/empty, AI bot is active for all accounts (legacy behavior)
                        // Otherwise, only active for the specifically linked account
                        hasAiBotActive = string.IsNullOrWhiteSpace(aiConfig.LinkedAccountId) || 
                                         aiConfig.LinkedAccountId.Equals(accountId.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                }

                // Check Corrade status
                var corradeService = scope.ServiceProvider.GetService<ICorradeService>();
                if (corradeService?.IsEnabled == true)
                {
                    hasCorradeActive = await corradeService.ShouldProcessWhispersForAccountAsync(accountId);
                }
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug("Service provider disposed during service status check for account {AccountId}: {Message}", accountId, ex.Message);
                _isShuttingDown = true; // Mark as shutting down if we encounter this
                // Return false for both services if the service provider is disposed (likely during shutdown)
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting service status for account {AccountId}", accountId);
                // Return false for both services if there's an error
            }

            return (hasAiBotActive, hasCorradeActive);
        }

        private async void OnChatSessionUpdated(object? sender, ChatSessionDto session)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                if (session.ChatType == "Group")
                {
                    await _hubContext.Clients
                        .Group($"account_{instance.AccountId}")
                        .GroupSessionUpdated(session);
                }
                else if (session.ChatType == "IM")
                {
                    await _hubContext.Clients
                        .Group($"account_{instance.AccountId}")
                        .IMSessionUpdated(session);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting chat session update");
            }
        }

        private async void OnAvatarAdded(object? sender, AvatarDto avatar)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                // Get all nearby avatars with display names and broadcast the updated list
                var nearbyAvatars = (await instance.GetNearbyAvatarsAsync()).ToList();
                
                await _hubContext.Clients
                    .Group($"account_{instance.AccountId}")
                    .NearbyAvatarsUpdated(nearbyAvatars);
                    
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting avatar added for account {AccountId}", 
                    sender is Core.WebRadegastInstance inst ? inst.AccountId : "unknown");
            }
        }

        private async void OnAvatarUpdated(object? sender, AvatarDto avatar)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                var accountId = Guid.Parse(instance.AccountId);
                var now = DateTime.UtcNow;

                // Check if we recently broadcasted for this account
                var lastBroadcast = _lastAvatarUpdateBroadcast.GetValueOrDefault(accountId, DateTime.MinValue);
                var timeSinceLastBroadcast = now - lastBroadcast;

                // If we're within the debounce interval, set up a timer to broadcast later
                if (timeSinceLastBroadcast < _avatarUpdateDebounceInterval)
                {
                    // Cancel any existing timer for this account
                    if (_avatarUpdateTimers.TryRemove(accountId, out var existingTimer))
                    {
                        existingTimer.Dispose();
                    }

                    // Create a new timer to broadcast after the debounce interval
                    var debounceTimer = new Timer(async _ =>
                    {
                        try
                        {
                            await BroadcastSingleAvatarUpdate(instance, avatar);
                            _avatarUpdateTimers.TryRemove(accountId, out var timer);
                            timer?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in debounced avatar update broadcast for account {AccountId}", accountId);
                        }
                    }, null, _avatarUpdateDebounceInterval, Timeout.InfiniteTimeSpan);

                    _avatarUpdateTimers.TryAdd(accountId, debounceTimer);
                    
                    _logger.LogDebug("Avatar update for account {AccountId} debounced (last broadcast: {TimeSince}ms ago)", 
                        accountId, timeSinceLastBroadcast.TotalMilliseconds);
                    return;
                }

                // Broadcast immediately if enough time has passed
                await BroadcastSingleAvatarUpdate(instance, avatar);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting avatar update for account {AccountId}", 
                    sender is Core.WebRadegastInstance inst ? inst.AccountId : "unknown");
            }
        }

        private async Task BroadcastSingleAvatarUpdate(Core.WebRadegastInstance instance, AvatarDto avatar)
        {
            var accountId = Guid.Parse(instance.AccountId);
            
            // Update the last broadcast time
            _lastAvatarUpdateBroadcast.AddOrUpdate(accountId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);

            // Broadcast the individual avatar update (more efficient than full list)
            await _hubContext.Clients
                .Group($"account_{instance.AccountId}")
                .AvatarUpdated(avatar);

            _logger.LogDebug("Broadcasted single avatar update for account {AccountId}: {AvatarName}", 
                accountId, avatar.Name);
        }

        private async void OnAvatarRemoved(object? sender, string avatarId)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                // Get all nearby avatars with display names and broadcast the updated list
                var nearbyAvatars = (await instance.GetNearbyAvatarsAsync()).ToList();
                
                await _hubContext.Clients
                    .Group($"account_{instance.AccountId}")
                    .NearbyAvatarsUpdated(nearbyAvatars);
                    
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting avatar removed for account {AccountId}", 
                    sender is Core.WebRadegastInstance inst ? inst.AccountId : "unknown");
            }
        }

        private async void OnRegionChanged(object? sender, RegionInfoDto regionInfo)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                await _hubContext.Clients
                    .Group($"account_{instance.AccountId}")
                    .RegionInfoUpdated(regionInfo);

                // When region changes, clear and refresh nearby avatars with display names
                var nearbyAvatars = (await instance.GetNearbyAvatarsAsync()).ToList();
                await _hubContext.Clients
                    .Group($"account_{instance.AccountId}")
                    .NearbyAvatarsUpdated(nearbyAvatars);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting region change");
            }
        }

        private async void OnNoticeReceived(object? sender, NoticeReceivedEventArgs e)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                // Convert to DTO for SignalR transmission
                var noticeEventDto = new NoticeReceivedEventDto
                {
                    Notice = e.Notice,
                    SessionId = e.SessionId,
                    DisplayMessage = e.DisplayMessage
                };

                await _hubContext.Clients
                    .Group($"account_{e.Notice.AccountId}")
                    .NoticeReceived(noticeEventDto);

                _logger.LogInformation("Broadcasted {NoticeType} notice from {FromName} for account {AccountId}", 
                    e.Notice.Type, e.Notice.FromName, e.Notice.AccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting notice");
            }
        }

        private async void OnRegionStatsUpdated(object? sender, RegionStatsUpdatedEventArgs e)
        {
            try
            {
                if (_isShuttingDown)
                    return;
                await _hubContext.Clients
                    .Group($"account_{e.AccountId}")
                    .RegionStatsUpdated(e.Stats);

                _logger.LogDebug("Broadcasted region stats update for account {AccountId}: {RegionName} (Dilation: {TimeDilation:F3})", 
                    e.AccountId, e.Stats.RegionName, e.Stats.TimeDilation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting region stats update");
            }
        }

        private async void OnPresenceStatusChanged(object? sender, PresenceStatusChangedEventArgs e)
        {
            try
            {
                if (_isShuttingDown)
                    return;

                await _hubContext.Clients
                    .Group($"account_{e.AccountId}")
                    .PresenceStatusChanged(e.AccountId.ToString(), e.Status.ToString(), e.StatusText);
                    
                _logger.LogDebug("Broadcasted presence status change for account {AccountId}: {Status}", 
                    e.AccountId, e.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting presence status change");
            }
        }

        private async void OnGroupsUpdated(object? sender, GroupsUpdatedEventArgs e)
        {
            try
            {
                if (_isShuttingDown)
                    return;
                var groupsList = e.Groups.ToList();
                
                await _hubContext.Clients
                    .Group($"account_{e.AccountId}")
                    .GroupsUpdated(e.AccountId.ToString(), groupsList);
                    
                _logger.LogDebug("Broadcasted groups update for account {AccountId}: {Count} groups (with ignore status)", 
                    e.AccountId, groupsList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting groups update");
            }
        }

        /// <summary>
        /// Check if there are any active web connections for the specified account
        /// </summary>
        private Task<bool> HasActiveWebConnections(Guid accountId)
        {
            var hasConnections = _connectionTrackingService.HasActiveConnections(accountId);
            return Task.FromResult(hasConnections);
        }

        /// <summary>
        /// Checks for day boundary transitions and triggers bulk avatar recording when a new day starts
        /// OPTIMIZED: Now includes memory pressure monitoring and GC management during maintenance
        /// </summary>
        private async Task CheckDayBoundaryAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Use SLT date for day boundary detection to match how stats are stored
                using var timeScope = _serviceProvider.CreateScope();
                var sltTimeService = timeScope.ServiceProvider.GetRequiredService<ISLTimeService>();
                var currentSLTDay = sltTimeService.GetCurrentSLT().Date;
                
                // Check if we've crossed into a new SLT day
                if (currentSLTDay > _lastDayCheck)
                {
                    // Log memory usage before maintenance
                    var memoryBefore = GC.GetTotalMemory(false);
                    _logger.LogInformation("SLT day boundary detected: {PreviousDay} -> {CurrentDay} (SLT), triggering maintenance. Memory before: {MemoryMB}MB", 
                        _lastDayCheck, currentSLTDay, memoryBefore / (1024 * 1024));
                    
                    _lastDayCheck = currentSLTDay;
                    
                    // Trigger bulk recording across all connected accounts
                    await TriggerPeriodicAvatarRecordingAsync(cancellationToken);
                    
                    // Pause briefly to let bulk recording settle and allow GC to clean up temporary objects
                    await Task.Delay(2000, cancellationToken);
                    
                    // Also trigger cleanup of old records (daily maintenance)
                    using var scope = _serviceProvider.CreateScope();
                    var statsService = scope.ServiceProvider.GetService<IStatsService>();
                    var noticeService = scope.ServiceProvider.GetService<INoticeService>();
                    
                    var maintenanceTasks = new List<Task>();
                    
                    // Clean up old visitor records (keep last 90 days)
                    if (statsService != null)
                    {
                        maintenanceTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                _logger.LogInformation("Starting daily cleanup of old visitor records");
                                await statsService.CleanupOldRecordsAsync(90);
                                
                                // Force garbage collection after database cleanup to free up memory
                                var memoryAfterStats = GC.GetTotalMemory(false);
                                GC.Collect(2, GCCollectionMode.Optimized, true);
                                GC.WaitForPendingFinalizers();
                                var memoryAfterGC = GC.GetTotalMemory(true);
                                
                                _logger.LogInformation("Stats cleanup completed. Memory before GC: {BeforeMB}MB, after GC: {AfterMB}MB", 
                                    memoryAfterStats / (1024 * 1024), memoryAfterGC / (1024 * 1024));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error during daily cleanup of old visitor records");
                            }
                        }, cancellationToken));
                    }
                    
                    // Clean up old notices (keep last 7 days)
                    if (noticeService != null)
                    {
                        maintenanceTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                _logger.LogInformation("Starting daily cleanup of old notices");
                                var deletedCount = await noticeService.CleanupOldNoticesAsync(7);
                                if (deletedCount > 0)
                                {
                                    _logger.LogInformation("Daily maintenance: cleaned up {DeletedCount} old notices", deletedCount);
                                    
                                    // Force garbage collection after notice cleanup if significant deletions
                                    if (deletedCount > 100)
                                    {
                                        GC.Collect(1, GCCollectionMode.Optimized, true);
                                        _logger.LogDebug("Triggered GC after deleting {Count} notices", deletedCount);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error during daily cleanup of old notices");
                            }
                        }, cancellationToken));
                    }
                    
                    // Wait for all maintenance tasks to complete with timeout
                    try
                    {
                        await Task.WhenAll(maintenanceTasks).WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Daily maintenance tasks timed out after 5 minutes");
                    }
                    
                    // Final comprehensive memory cleanup and reporting
                    var memoryAfterMaintenance = GC.GetTotalMemory(false);
                    GC.Collect(2, GCCollectionMode.Aggressive, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, true); // Second pass to clean up finalizers
                    var finalMemory = GC.GetTotalMemory(true);
                    
                    _logger.LogInformation("Daily maintenance completed. Memory: Before={BeforeMB}MB, After={AfterMB}MB, Final={FinalMB}MB, Saved={SavedMB}MB", 
                        memoryBefore / (1024 * 1024), 
                        memoryAfterMaintenance / (1024 * 1024),
                        finalMemory / (1024 * 1024),
                        (memoryBefore - finalMemory) / (1024 * 1024));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking day boundary");
            }
        }

        /// <summary>
        /// Triggers recording of all currently present avatars across all connected accounts
        /// </summary>
        private async Task TriggerPeriodicAvatarRecordingAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                
                var accounts = await accountService.GetAccountsAsync();
                var recordingTasks = new List<Task>();
                
                foreach (var account in accounts)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var instance = accountService.GetInstance(account.Id);
                    if (instance?.IsConnected == true)
                    {
                        // Add task to record all present avatars for this account
                        recordingTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                // Add small random delay to reduce race conditions during bulk recording
                                var delay = new Random().Next(0, 2000); // 0-2 seconds
                                await Task.Delay(delay, cancellationToken);
                                
                                await instance.RecordAllPresentAvatarsAsync();
                                _logger.LogDebug("Triggered periodic avatar recording for account {AccountId} (delayed {Delay}ms)", account.Id, delay);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error triggering periodic avatar recording for account {AccountId}", account.Id);
                            }
                        }, cancellationToken));
                    }
                }
                
                // Wait for all recording tasks to complete (with timeout)
                if (recordingTasks.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(recordingTasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                        _logger.LogInformation("Completed periodic avatar recording for {Count} connected accounts", recordingTasks.Count);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Periodic avatar recording timed out after 30 seconds");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering periodic avatar recording");
            }
        }

        private DateTime _lastAccountCleanup = DateTime.MinValue;
        private DateTime _lastAvatarBroadcast = DateTime.MinValue;
        private DateTime _lastStaleConnectionCleanup = DateTime.MinValue;

        /// <summary>
        /// Periodic cleanup of accounts that may have disconnected unexpectedly
        /// </summary>
        private async Task CleanupDisconnectedAccountsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                
                // Only run cleanup every 5 minutes to avoid excessive processing
                var now = DateTime.UtcNow;
                
                if (now - _lastAccountCleanup < TimeSpan.FromMinutes(5))
                {
                    return; // Skip this cleanup cycle
                }
                
                _lastAccountCleanup = now;
                
                await accountService.CleanupDisconnectedAccountsAsync();
                
                _logger.LogDebug("Completed periodic cleanup of disconnected accounts");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic cleanup of disconnected accounts");
            }
        }

        /// <summary>
        /// Periodically broadcast complete avatar state to all connected clients
        /// This ensures clients eventually get the correct state even if they missed individual updates
        /// </summary>
        private async Task PeriodicAvatarStateBroadcastAsync(CancellationToken cancellationToken)
        {
            try
            {
                var now = DateTime.UtcNow;
                
                // Only run avatar state broadcast every 10 minutes to avoid excessive traffic
                if (now - _lastAvatarBroadcast < TimeSpan.FromMinutes(10))
                {
                    return; // Skip this broadcast cycle
                }
                
                _lastAvatarBroadcast = now;
                
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                
                var accounts = await accountService.GetAccountsAsync();
                var broadcastTasks = new List<Task>();
                
                foreach (var account in accounts)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Only broadcast for accounts that have active web connections
                    var hasActiveConnections = _connectionTrackingService.HasActiveConnections(account.Id);
                    if (!hasActiveConnections)
                        continue;
                    
                    var instance = accountService.GetInstance(account.Id);
                    if (instance?.IsConnected == true)
                    {
                        broadcastTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                // Get current avatar state
                                var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                                var avatarList = nearbyAvatars.ToList();
                                
                                // Broadcast to all connections for this account
                                await _hubContext.Clients
                                    .Group($"account_{account.Id}")
                                    .NearbyAvatarsUpdated(avatarList);
                                
                                _logger.LogDebug("Periodic broadcast: sent {Count} avatars to group account_{AccountId}", 
                                    avatarList.Count, account.Id);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Error during periodic avatar broadcast for account {AccountId}", account.Id);
                            }
                        }, cancellationToken));
                    }
                }
                
                // Wait for all broadcast tasks to complete (with timeout)
                if (broadcastTasks.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(broadcastTasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                        _logger.LogDebug("Completed periodic avatar state broadcast for {Count} accounts with active connections", 
                            broadcastTasks.Count);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Periodic avatar state broadcast timed out after 10 seconds");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic avatar state broadcast");
            }
        }

        /// <summary>
        /// Periodically clean up stale connections that haven't sent heartbeats
        /// </summary>
        private Task PeriodicStaleConnectionCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                var now = DateTime.UtcNow;
                
                // Only run stale connection cleanup every 2 minutes
                if (now - _lastStaleConnectionCleanup < TimeSpan.FromMinutes(2))
                {
                    return Task.CompletedTask; // Skip this cleanup cycle
                }
                
                _lastStaleConnectionCleanup = now;
                
                _logger.LogDebug("Running periodic stale connection cleanup");
                _connectionTrackingService.CleanupStaleConnections();
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic stale connection cleanup");
                return Task.CompletedTask;
            }
        }

        private async void OnScriptDialogReceived(object? sender, Models.ScriptDialogEventArgs e)
        {
            try
            {
                if (_isShuttingDown)
                    return;

                var groupName = $"account_{e.Dialog.AccountId}";
                
                // Check if there are any active connections in the account group
                var hasActiveConnections = await HasActiveWebConnections(e.Dialog.AccountId);
                
                if (!hasActiveConnections)
                {
                    // No web clients connected - auto-dismiss the dialog
                    _logger.LogInformation("Auto-dismissing script dialog for account {AccountId} - no active web connections. Dialog from object {ObjectName} ({ObjectId})", 
                        e.Dialog.AccountId, e.Dialog.ObjectName, e.Dialog.ObjectId);
                    
                    // Create a dismiss request and handle it
                    using var scope = _serviceProvider.CreateScope();
                    var scriptDialogService = scope.ServiceProvider.GetRequiredService<IScriptDialogService>();
                    
                    var dismissRequest = new ScriptDialogDismissRequest
                    {
                        AccountId = e.Dialog.AccountId,
                        DialogId = e.Dialog.DialogId
                    };
                    
                    await scriptDialogService.DismissDialogAsync(dismissRequest);
                    return;
                }

                // Broadcast to connected web clients
                await _hubContext.Clients
                    .Group(groupName)
                    .ScriptDialogReceived(e.Dialog);

                _logger.LogInformation("Broadcasted script dialog for account {AccountId} from object {ObjectName} ({ObjectId})", 
                    e.Dialog.AccountId, e.Dialog.ObjectName, e.Dialog.ObjectId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting script dialog");
            }
        }

        private async void OnScriptPermissionReceived(object? sender, Models.ScriptPermissionEventArgs e)
        {
            try
            {
                if (_isShuttingDown)
                    return;

                var groupName = $"account_{e.Permission.AccountId}";
                
                // Check if there are any active connections in the account group
                var hasActiveConnections = await HasActiveWebConnections(e.Permission.AccountId);
                
                if (!hasActiveConnections)
                {
                    // No web clients connected - auto-deny the permission request
                    _logger.LogInformation("Auto-denying script permission request for account {AccountId} - no active web connections. Request from object {ObjectName} ({ObjectId}): {Permissions}", 
                        e.Permission.AccountId, e.Permission.ObjectName, e.Permission.ObjectId, e.Permission.PermissionsDescription);
                    
                    // Create a deny request and handle it
                    using var scope = _serviceProvider.CreateScope();
                    var scriptDialogService = scope.ServiceProvider.GetRequiredService<IScriptDialogService>();
                    
                    var denyRequest = new ScriptPermissionResponseRequest
                    {
                        AccountId = e.Permission.AccountId,
                        RequestId = e.Permission.RequestId,
                        Grant = false, // Deny the permission
                        Mute = false
                    };
                    
                    await scriptDialogService.RespondToPermissionAsync(denyRequest);
                    return;
                }

                // Broadcast to connected web clients
                await _hubContext.Clients
                    .Group(groupName)
                    .ScriptPermissionReceived(e.Permission);

                _logger.LogInformation("Broadcasted script permission request for account {AccountId} from object {ObjectName} ({ObjectId}): {Permissions}", 
                    e.Permission.AccountId, e.Permission.ObjectName, e.Permission.ObjectId, e.Permission.PermissionsDescription);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting script permission request");
            }
        }

        private async void OnTeleportRequestReceived(object? sender, TeleportRequestEventArgs e)
        {
            try
            {
                if (_isShuttingDown)
                    return;
                var groupName = $"account_{e.Request.AccountId}";
                
                // Note: Connection checking is now done at the WebRadegastInstance level
                // If we receive this event, it means there are active web connections
                
                // Broadcast to connected web clients
                await _hubContext.Clients
                    .Group(groupName)
                    .TeleportRequestReceived(e.Request);

                _logger.LogInformation("Broadcasted teleport request for account {AccountId} from {FromAgentName} ({FromAgentId}): {Message}", 
                    e.Request.AccountId, e.Request.FromAgentName, e.Request.FromAgentId, e.Request.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting teleport request");
            }
        }

        /// <summary>
        /// Safely unsubscribe from all events for a specific account instance
        /// This prevents memory leaks from accumulated event handlers
        /// </summary>
        private void SafeUnsubscribeFromInstance(Guid accountId, WebRadegastInstance instance)
        {
            try
            {
                // Unsubscribe from all events - this MUST match the subscription list exactly
                instance.ChatReceived -= OnChatReceived;
                instance.StatusChanged -= OnStatusChanged;
                instance.ConnectionChanged -= OnConnectionChanged;
                instance.ChatSessionUpdated -= OnChatSessionUpdated;
                instance.AvatarAdded -= OnAvatarAdded;
                instance.AvatarRemoved -= OnAvatarRemoved;
                instance.AvatarUpdated -= OnAvatarUpdated;
                instance.RegionChanged -= OnRegionChanged;
                instance.NoticeReceived -= OnNoticeReceived;
                instance.ScriptDialogReceived -= OnScriptDialogReceived;
                instance.ScriptPermissionReceived -= OnScriptPermissionReceived;
                instance.TeleportRequestReceived -= OnTeleportRequestReceived;

                _logger.LogDebug("Successfully unsubscribed from all events for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error unsubscribing from events for account {AccountId}", accountId);
            }
        }

        /// <summary>
        /// Clean up all event subscriptions and references when service is disposed
        /// This is critical to prevent memory leaks
        /// </summary>
        public override void Dispose()
        {
            _isShuttingDown = true;
            
            try
            {
                // Unsubscribe from all tracked instances
                foreach (var kvp in _instanceReferences.ToList())
                {
                    if (kvp.Value.TryGetTarget(out var instance))
                    {
                        SafeUnsubscribeFromInstance(kvp.Key, instance);
                    }
                }
                
                // Dispose and clear avatar update timers
                foreach (var timer in _avatarUpdateTimers.Values)
                {
                    timer?.Dispose();
                }
                _avatarUpdateTimers.Clear();

                // Clear all tracking dictionaries
                _subscribedInstances.Clear();
                _lastSubscriptionTime.Clear();
                _instanceReferences.Clear();
                _lastAvatarUpdateBroadcast.Clear();
                
                _logger.LogInformation("RadegastBackgroundService disposed and cleaned up all event subscriptions");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during RadegastBackgroundService disposal");
            }
            finally
            {
                base.Dispose();
            }
        }
    }
}