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

            // Initialize presence service
            using var scope = _serviceProvider.CreateScope();
            _presenceService = scope.ServiceProvider.GetRequiredService<IPresenceService>();
            _presenceService.PresenceStatusChanged += OnPresenceStatusChanged;

            // Initialize region info service
            _regionInfoService = scope.ServiceProvider.GetRequiredService<IRegionInfoService>();
            _regionInfoService.RegionStatsUpdated += OnRegionStatsUpdated;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessAccountEvents(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in RadegastBackgroundService");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            _logger.LogInformation("Radegast Background Service stopped");
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
                if (sender is not Core.WebRadegastInstance instance)
                    return;

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
                    GridUrl = instance.AccountInfo.GridUrl
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
                if (sender is not Core.WebRadegastInstance instance)
                    return;

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
                    GridUrl = instance.AccountInfo.GridUrl
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

        private async void OnChatSessionUpdated(object? sender, ChatSessionDto session)
        {
            try
            {
                if (sender is not Core.WebRadegastInstance instance)
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
                if (sender is not Core.WebRadegastInstance instance)
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
                if (sender is not Core.WebRadegastInstance instance)
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
                if (sender is not Core.WebRadegastInstance instance)
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
                if (sender is not Core.WebRadegastInstance instance)
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
                if (sender is not Core.WebRadegastInstance instance)
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

        /// <summary>
        /// Check if there are any active web connections for the specified account
        /// </summary>
        private Task<bool> HasActiveWebConnections(Guid accountId)
        {
            var hasConnections = _connectionTrackingService.HasActiveConnections(accountId);
            return Task.FromResult(hasConnections);
        }

        private async void OnScriptDialogReceived(object? sender, Models.ScriptDialogEventArgs e)
        {
            try
            {
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