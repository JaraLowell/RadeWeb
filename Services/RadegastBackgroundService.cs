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
        private IPresenceService? _presenceService;
        private IRegionInfoService? _regionInfoService;

        public RadegastBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<RadegastBackgroundService> logger,
            IHubContext<RadegastHub, IRadegastHubClient> hubContext)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _hubContext = hubContext;
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
                    
                    instance.ChatReceived += OnChatReceived;
                    instance.StatusChanged += OnStatusChanged;
                    instance.ConnectionChanged += OnConnectionChanged;
                    instance.ChatSessionUpdated += OnChatSessionUpdated;
                    instance.AvatarAdded += OnAvatarAdded;
                    instance.AvatarRemoved += OnAvatarRemoved;
                    instance.AvatarUpdated += OnAvatarUpdated;
                    instance.RegionChanged += OnRegionChanged;
                    instance.NoticeReceived += OnNoticeReceived;
                }
            }
        }

        private async void OnChatReceived(object? sender, ChatMessageDto chatMessage)
        {
            try
            {
                // Save chat message to database using scoped service
                using var scope = _serviceProvider.CreateScope();
                var chatHistoryService = scope.ServiceProvider.GetRequiredService<IChatHistoryService>();
                await chatHistoryService.SaveChatMessageAsync(chatMessage);
                
                // Broadcast to connected clients
                await _hubContext.Clients
                    .Group($"account_{chatMessage.AccountId}")
                    .ReceiveChat(chatMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting chat message");
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
    }
}