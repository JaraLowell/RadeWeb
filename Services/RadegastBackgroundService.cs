using Microsoft.AspNetCore.SignalR;
using RadegastWeb.Core;
using RadegastWeb.Hubs;
using RadegastWeb.Models;
using RadegastWeb.Services;

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
                // Initialize presence service
                using var scope = _serviceProvider.CreateScope();
                _presenceService = scope.ServiceProvider.GetRequiredService<IPresenceService>();
                _presenceService.PresenceStatusChanged += OnPresenceStatusChanged;

                // Initialize region info service
                _regionInfoService = scope.ServiceProvider.GetRequiredService<IRegionInfoService>();
                _regionInfoService.RegionStatsUpdated += OnRegionStatsUpdated;

                // Initialize group service
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
                
                _logger.LogInformation("Radegast Background Service stopped");
            }
        }

        private async Task ProcessAccountEvents(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();

            var accounts = await accountService.GetAccountsAsync();
            
            foreach (var account in accounts)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var instance = accountService.GetInstance(account.Id);
                if (instance != null)
                {
                    // Subscribe to events if not already subscribed
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
                }
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
                _logger.LogError(ex, "Error broadcasting avatar added");
            }
        }

        private async void OnAvatarUpdated(object? sender, AvatarDto avatar)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance || _isShuttingDown)
                    return;

                // Broadcast the individual avatar update (more efficient than full list)
                await _hubContext.Clients
                    .Group($"account_{instance.AccountId}")
                    .AvatarUpdated(avatar);
                    
                _logger.LogDebug("Broadcasted avatar update for {AvatarId} on account {AccountId}", 
                    avatar.Id, instance.AccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting avatar update");
            }
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
                _logger.LogError(ex, "Error broadcasting avatar removed");
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
                    _logger.LogInformation("SLT day boundary detected: {PreviousDay} -> {CurrentDay} (SLT), triggering bulk avatar recording", 
                        _lastDayCheck, currentSLTDay);
                    
                    _lastDayCheck = currentSLTDay;
                    
                    // Trigger bulk recording across all connected accounts
                    await TriggerPeriodicAvatarRecordingAsync(cancellationToken);
                    
                    // Also trigger cleanup of old visitor records (keep last 90 days)
                    using var scope = _serviceProvider.CreateScope();
                    var statsService = scope.ServiceProvider.GetService<IStatsService>();
                    if (statsService != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await statsService.CleanupOldRecordsAsync(90);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error during daily cleanup of old visitor records");
                            }
                        }, cancellationToken);
                    }
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
                                await instance.RecordAllPresentAvatarsAsync();
                                _logger.LogDebug("Triggered periodic avatar recording for account {AccountId}", account.Id);
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
    }
}