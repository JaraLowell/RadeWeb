using Microsoft.AspNetCore.SignalR;
using OpenMetaverse;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Hubs
{
    public class RadegastHub : Hub<IRadegastHubClient>
    {
        private readonly IAccountService _accountService;
        private readonly IChatHistoryService _chatHistoryService;
        private readonly IPresenceService _presenceService;
        private readonly IAuthenticationService _authService;
        private readonly ILogger<RadegastHub> _logger;

        public RadegastHub(IAccountService accountService, IChatHistoryService chatHistoryService, IPresenceService presenceService, IAuthenticationService authService, ILogger<RadegastHub> logger)
        {
            _accountService = accountService;
            _chatHistoryService = chatHistoryService;
            _presenceService = presenceService;
            _authService = authService;
            _logger = logger;
        }

        private bool IsAuthenticated()
        {
            return _authService.ValidateHttpContext(Context.GetHttpContext()!);
        }

        public async Task JoinAccountGroup(string accountId)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{accountId}");
            _logger.LogInformation("Client {ConnectionId} joined account group {AccountId}", 
                Context.ConnectionId, accountId);
        }

        public async Task LeaveAccountGroup(string accountId)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"account_{accountId}");
            _logger.LogInformation("Client {ConnectionId} left account group {AccountId}", 
                Context.ConnectionId, accountId);
        }

        public async Task SendChat(SendChatRequest request)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

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

        public async Task ClearChatHistory(string accountId, string sessionId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to clear chat history for account {AccountId}, session {SessionId}", accountId, sessionId);
                Context.Abort();
                return;
            }

            try
            {
                _logger.LogInformation("ClearChatHistory request received for account {AccountId}, session {SessionId}", accountId, sessionId);
                
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogDebug("Parsed account ID successfully: {AccountGuid}", accountGuid);
                    
                    var success = await _chatHistoryService.ClearChatHistoryAsync(accountGuid, sessionId);
                    if (success)
                    {
                        await Clients.Caller.ChatHistoryCleared(accountId, sessionId);
                        _logger.LogInformation("Chat history cleared successfully for account {AccountId}, session {SessionId}", accountId, sessionId);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to clear chat history for account {AccountId}, session {SessionId}", accountId, sessionId);
                        await Clients.Caller.ChatError("Failed to clear chat history");
                    }
                }
                else
                {
                    _logger.LogError("Invalid account ID format: {AccountId}", accountId);
                    await Clients.Caller.ChatError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing chat history via SignalR for account {AccountId}, session {SessionId}", accountId, sessionId);
                await Clients.Caller.ChatError("Error clearing chat history");
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

        public async Task HandleBrowserReturn()
        {
            try
            {
                await _presenceService.HandleBrowserReturnAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling browser return via SignalR");
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

        public async Task DismissNotice(string accountId, string noticeId)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    await _accountService.DismissNoticeAsync(accountGuid, noticeId);
                }
                else
                {
                    await Clients.Caller.ChatError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dismissing notice via SignalR");
                await Clients.Caller.ChatError("Error dismissing notice");
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

        public async Task GetUnreadNoticesCount(string accountId)
        {
            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var count = await _accountService.GetUnreadNoticesCountAsync(accountGuid);
                    await Clients.Caller.UnreadNoticesCountLoaded(accountId, count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting unread notices count via SignalR");
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

        public async Task SitOnObject(string accountId, string? objectId)
        {
            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    await Clients.Caller.SitStandError("Invalid account ID");
                    return;
                }

                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    await Clients.Caller.SitStandError("Account not found");
                    return;
                }

                if (!instance.IsConnected)
                {
                    await Clients.Caller.SitStandError("Account is not connected");
                    return;
                }

                UUID targetObject = UUID.Zero;

                // Validate and parse object ID if provided
                if (!string.IsNullOrEmpty(objectId))
                {
                    if (!UUID.TryParse(objectId, out targetObject))
                    {
                        await Clients.Caller.SitStandError("Invalid object ID format");
                        return;
                    }

                    // Check if object exists in region
                    if (!instance.IsObjectInRegion(targetObject))
                    {
                        await Clients.Caller.SitStandError("Object not found in current region");
                        return;
                    }
                }

                // Attempt to sit
                var success = instance.SetSitting(true, targetObject);
                if (!success)
                {
                    await Clients.Caller.SitStandError("Failed to initiate sitting");
                    return;
                }

                var message = targetObject == UUID.Zero ? "Sitting on ground" : $"Sitting on object {targetObject}";
                await Clients.Caller.SitStandSuccess(message);

                _logger.LogInformation("Avatar sitting initiated for account {AccountId} on {ObjectId}", 
                    accountId, objectId ?? "ground");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sitting on object via SignalR");
                await Clients.Caller.SitStandError("Error initiating sit");
            }
        }

        public async Task StandUp(string accountId)
        {
            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    await Clients.Caller.SitStandError("Invalid account ID");
                    return;
                }

                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    await Clients.Caller.SitStandError("Account not found");
                    return;
                }

                if (!instance.IsConnected)
                {
                    await Clients.Caller.SitStandError("Account is not connected");
                    return;
                }

                // Attempt to stand
                var success = instance.SetSitting(false);
                if (!success)
                {
                    await Clients.Caller.SitStandError("Failed to stand up");
                    return;
                }

                await Clients.Caller.SitStandSuccess("Standing up");

                _logger.LogInformation("Avatar standing up for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error standing up via SignalR");
                await Clients.Caller.SitStandError("Error standing up");
            }
        }

        public async Task GetObjectInfo(string accountId, string objectId)
        {
            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    await Clients.Caller.SitStandError("Invalid account ID");
                    return;
                }

                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    await Clients.Caller.SitStandError("Account not found");
                    return;
                }

                if (!instance.IsConnected)
                {
                    await Clients.Caller.SitStandError("Account is not connected");
                    return;
                }

                if (!UUID.TryParse(objectId, out var uuid))
                {
                    await Clients.Caller.SitStandError("Invalid object ID format");
                    return;
                }

                var objectInfo = instance.GetObjectInfo(uuid);
                if (objectInfo == null)
                {
                    await Clients.Caller.SitStandError("Object not found in current region");
                    return;
                }

                await Clients.Caller.ObjectInfoReceived(objectInfo);

                _logger.LogInformation("Object info retrieved for account {AccountId}, object {ObjectId}", 
                    accountId, objectId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting object info via SignalR");
                await Clients.Caller.SitStandError("Error retrieving object info");
            }
        }

        public async Task GetSittingStatus(string accountId)
        {
            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    await Clients.Caller.SitStandError("Invalid account ID");
                    return;
                }

                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    await Clients.Caller.SitStandError("Account not found");
                    return;
                }

                if (!instance.IsConnected)
                {
                    await Clients.Caller.SitStandError("Account is not connected");
                    return;
                }

                var isSitting = instance.IsSitting;
                var sittingOnLocalId = instance.SittingOnLocalId;
                
                await Clients.Caller.SittingStatusUpdated(new
                {
                    isSitting = isSitting,
                    sittingOnLocalId = sittingOnLocalId,
                    sittingOnGround = instance.Client.Self.Movement.SitOnGround
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sitting status via SignalR");
                await Clients.Caller.SitStandError("Error retrieving sitting status");
            }
        }

        public override async Task OnConnectedAsync()
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated connection attempt from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public async Task GetCurrentPresenceStatus(string accountId)
        {
            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    await Clients.Caller.PresenceError("Invalid account ID");
                    return;
                }

                // Get the status from PresenceService, which will check the SL client if needed
                var status = _presenceService.GetAccountStatus(accountGuid);
                var statusText = status switch
                {
                    PresenceStatus.Away => "Away",
                    PresenceStatus.Busy => "Busy",
                    _ => "Online"
                };

                await Clients.Caller.PresenceStatusChanged(accountId, status.ToString(), statusText);
                _logger.LogInformation("Retrieved and synced current presence status for account {AccountId}: {Status}", 
                    accountId, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current presence status for account {AccountId}", accountId);
                await Clients.Caller.PresenceError("Error retrieving presence status");
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
            
            // Handle browser close event (automatic status changes disabled)
            try
            {
                await _presenceService.HandleBrowserCloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling browser close event");
            }
            
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
        Task UnreadNoticesCountLoaded(string accountId, int count);
        Task ChatHistoryCleared(string accountId, string sessionId);
        
        // Sit/Stand methods
        Task SitStandSuccess(string message);
        Task SitStandError(string error);
        Task ObjectInfoReceived(ObjectInfo objectInfo);
        Task SittingStatusUpdated(object status);
    }
}