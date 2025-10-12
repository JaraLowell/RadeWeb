using Microsoft.AspNetCore.SignalR;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Hubs
{
    public class RadegastHub : Hub<IRadegastHubClient>
    {
        private readonly IAccountService _accountService;
        private readonly IChatHistoryService _chatHistoryService;
        private readonly IPresenceService _presenceService;
        private readonly ILogger<RadegastHub> _logger;

        public RadegastHub(IAccountService accountService, IChatHistoryService chatHistoryService, IPresenceService presenceService, ILogger<RadegastHub> logger)
        {
            _accountService = accountService;
            _chatHistoryService = chatHistoryService;
            _presenceService = presenceService;
            _logger = logger;
        }

        public async Task JoinAccountGroup(string accountId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{accountId}");
            _logger.LogInformation("Client {ConnectionId} joined account group {AccountId}", 
                Context.ConnectionId, accountId);
        }

        public async Task LeaveAccountGroup(string accountId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"account_{accountId}");
            _logger.LogInformation("Client {ConnectionId} left account group {AccountId}", 
                Context.ConnectionId, accountId);
        }

        public async Task SendChat(SendChatRequest request)
        {
            try
            {
                var success = await _accountService.SendChatAsync(
                    request.AccountId, 
                    request.Message, 
                    request.ChatType, 
                    request.Channel);

                if (!success)
                {
                    await Clients.Caller.ChatError("Failed to send message");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending chat via SignalR");
                await Clients.Caller.ChatError("Error sending message");
            }
        }

        public async Task SendIM(string accountId, string targetId, string message)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var success = await _accountService.SendIMAsync(accountGuid, targetId, message);
                    if (!success)
                    {
                        await Clients.Caller.ChatError("Failed to send IM");
                    }
                }
                else
                {
                    await Clients.Caller.ChatError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending IM via SignalR");
                await Clients.Caller.ChatError("Error sending IM");
            }
        }

        public async Task SendGroupIM(string accountId, string groupId, string message)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var success = await _accountService.SendGroupIMAsync(accountGuid, groupId, message);
                    if (!success)
                    {
                        await Clients.Caller.ChatError("Failed to send group IM");
                    }
                }
                else
                {
                    await Clients.Caller.ChatError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending group IM via SignalR");
                await Clients.Caller.ChatError("Error sending group IM");
            }
        }

        public async Task GetNearbyAvatars(string accountId)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var avatars = await _accountService.GetNearbyAvatarsAsync(accountGuid);
                    await Clients.Caller.NearbyAvatarsUpdated(avatars.ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting nearby avatars via SignalR");
            }
        }

        public async Task GetChatHistory(string accountId, string sessionId, int count = 50, int skip = 0)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var history = await _chatHistoryService.GetChatHistoryAsync(accountGuid, sessionId, count, skip);
                    await Clients.Caller.ChatHistoryLoaded(accountId, sessionId, history.ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat history via SignalR");
            }
        }

        public async Task GetRecentSessions(string accountId)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var sessions = await _chatHistoryService.GetRecentSessionsAsync(accountGuid);
                    await Clients.Caller.RecentSessionsLoaded(accountId, sessions.ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent sessions via SignalR");
            }
        }

        public async Task SetAwayStatus(string accountId, bool away)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    await _presenceService.SetAwayAsync(accountGuid, away);
                }
                else
                {
                    await Clients.Caller.PresenceError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting away status via SignalR");
                await Clients.Caller.PresenceError("Error setting away status");
            }
        }

        public async Task SetBusyStatus(string accountId, bool busy)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    await _presenceService.SetBusyAsync(accountGuid, busy);
                }
                else
                {
                    await Clients.Caller.PresenceError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting busy status via SignalR");
                await Clients.Caller.PresenceError("Error setting busy status");
            }
        }

        public async Task SetActiveAccount(string accountId)
        {
            try
            {
                if (string.IsNullOrEmpty(accountId))
                {
                    await _presenceService.SetActiveAccountAsync(null);
                }
                else if (Guid.TryParse(accountId, out var accountGuid))
                {
                    await _presenceService.SetActiveAccountAsync(accountGuid);
                }
                else
                {
                    await Clients.Caller.PresenceError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting active account via SignalR");
                await Clients.Caller.PresenceError("Error setting active account");
            }
        }

        public async Task HandleBrowserClose()
        {
            try
            {
                await _presenceService.HandleBrowserCloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling browser close via SignalR");
            }
        }

        public async Task AcknowledgeNotice(string accountId, string noticeId)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    await _accountService.AcknowledgeNoticeAsync(accountGuid, noticeId);
                }
                else
                {
                    await Clients.Caller.ChatError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acknowledging notice via SignalR");
                await Clients.Caller.ChatError("Error acknowledging notice");
            }
        }

        public async Task GetRecentNotices(string accountId, int count = 20)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var notices = await _accountService.GetRecentNoticesAsync(accountGuid, count);
                    await Clients.Caller.RecentNoticesLoaded(accountId, notices.ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent notices via SignalR");
            }
        }

        public async Task GetRegionStats(string accountId)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var regionStats = await _accountService.GetRegionStatsAsync(accountGuid);
                    if (regionStats != null)
                    {
                        await Clients.Caller.RegionStatsUpdated(regionStats);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region stats via SignalR");
            }
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }

    public interface IRadegastHubClient
    {
        Task ReceiveChat(ChatMessageDto chatMessage);
        Task AccountStatusChanged(AccountStatus status);
        Task ChatError(string error);
        Task PresenceError(string error);
        Task PresenceStatusChanged(string accountId, string status, string statusText);
        Task NearbyAvatarsUpdated(List<AvatarDto> avatars);
        Task AvatarUpdated(AvatarDto avatar); // New method for individual avatar updates
        Task RegionInfoUpdated(RegionInfoDto regionInfo);
        Task RegionStatsUpdated(RegionStatsDto regionStats); // New method for detailed region statistics
        Task IMSessionStarted(ChatSessionDto session);
        Task IMSessionUpdated(ChatSessionDto session);
        Task GroupSessionStarted(ChatSessionDto session);
        Task GroupSessionUpdated(ChatSessionDto session);
        Task ChatHistoryLoaded(string accountId, string sessionId, List<ChatMessageDto> messages);
        Task RecentSessionsLoaded(string accountId, List<ChatSessionDto> sessions);
        Task NoticeReceived(NoticeReceivedEventDto noticeEvent);
        Task RecentNoticesLoaded(string accountId, List<NoticeDto> notices);
    }
}