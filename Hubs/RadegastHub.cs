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
        private readonly IScriptDialogService _scriptDialogService;
        private readonly ITeleportRequestService _teleportRequestService;
        private readonly IConnectionTrackingService _connectionTrackingService;
        private readonly ILogger<RadegastHub> _logger;
        private readonly IHubContext<RadegastHub, IRadegastHubClient> _hubContext;

        public RadegastHub(IAccountService accountService, IChatHistoryService chatHistoryService, IPresenceService presenceService, IAuthenticationService authService, IScriptDialogService scriptDialogService, ITeleportRequestService teleportRequestService, IConnectionTrackingService connectionTrackingService, ILogger<RadegastHub> logger, IHubContext<RadegastHub, IRadegastHubClient> hubContext)
        {
            _accountService = accountService;
            _chatHistoryService = chatHistoryService;
            _presenceService = presenceService;
            _authService = authService;
            _scriptDialogService = scriptDialogService;
            _teleportRequestService = teleportRequestService;
            _connectionTrackingService = connectionTrackingService;
            _logger = logger;
            _hubContext = hubContext;
        }

        private bool IsAuthenticated()
        {
            return _authService.ValidateHttpContext(Context.GetHttpContext()!);
        }

        public async Task JoinAccountGroup(string accountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to join account group {AccountId} from {ConnectionId}", accountId, Context.ConnectionId);
                Context.Abort();
                return;
            }

            if (string.IsNullOrEmpty(accountId))
            {
                _logger.LogWarning("Empty account ID provided for JoinAccountGroup from {ConnectionId}", Context.ConnectionId);
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    // Clean up any potential stale connections first (especially important after long periods)
                    _connectionTrackingService.CleanupStaleConnections();
                    
                    // Get existing connections for this account to handle browser refresh scenario
                    var existingConnections = _connectionTrackingService.GetConnectionsForAccount(accountGuid);
                    var currentConnectionCount = existingConnections.Count();
                    
                    _logger.LogDebug("Account {AccountId} currently has {Count} connections before join: [{Connections}]", 
                        accountId, currentConnectionCount, string.Join(", ", existingConnections));

                    // Check if this connection is already in the account group to prevent duplicates
                    if (existingConnections.Contains(Context.ConnectionId))
                    {
                        _logger.LogDebug("Connection {ConnectionId} already in account group {AccountId}, skipping duplicate join", 
                            Context.ConnectionId, accountId);
                        return;
                    }

                    // Handle browser refresh scenario: if there are multiple connections, clean up potentially stale ones
                    if (currentConnectionCount > 0)
                    {
                        _logger.LogInformation("Detected {Count} existing connections for account {AccountId} during new join from {ConnectionId}. This may indicate a browser refresh scenario.", 
                            currentConnectionCount, accountId, Context.ConnectionId);
                        
                        // Clean up potentially stale connections (this will be detected by the next SignalR ping)
                        foreach (var existingConnectionId in existingConnections.ToList())
                        {
                            if (existingConnectionId != Context.ConnectionId)
                            {
                                _logger.LogDebug("Cleaning up potentially stale connection {ExistingConnectionId} for account {AccountId}", 
                                    existingConnectionId, accountId);
                                
                                // Try to remove from SignalR group (may fail if connection is truly stale)
                                try
                                {
                                    await Groups.RemoveFromGroupAsync(existingConnectionId, $"account_{accountId}");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Failed to remove potentially stale connection {ExistingConnectionId} from SignalR group (connection may already be dead)", existingConnectionId);
                                }
                                
                                // Remove from tracking
                                _connectionTrackingService.RemoveConnection(existingConnectionId, accountGuid);
                            }
                        }
                    }

                    // Force cleanup of this specific connection from all groups to prevent drift
                    _connectionTrackingService.ForceRemoveConnection(Context.ConnectionId);
                    
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{accountId}");
                    _connectionTrackingService.AddConnection(Context.ConnectionId, accountGuid);
                    
                    var finalConnectionCount = _connectionTrackingService.GetConnectionCount(accountGuid);
                    _logger.LogInformation("Client {ConnectionId} joined account group {AccountId}. Total connections: {Count}", 
                        Context.ConnectionId, accountId, finalConnectionCount);
                }
                else
                {
                    _logger.LogWarning("Invalid account ID format for JoinAccountGroup: {AccountId} from {ConnectionId}", accountId, Context.ConnectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error joining account group {AccountId} for connection {ConnectionId}", accountId, Context.ConnectionId);
                // Don't re-throw, just log the error to prevent client disconnection
            }
        }

        public async Task LeaveAccountGroup(string accountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to leave account group {AccountId} from {ConnectionId}", accountId, Context.ConnectionId);
                Context.Abort();
                return;
            }

            if (string.IsNullOrEmpty(accountId))
            {
                _logger.LogWarning("Empty account ID provided for LeaveAccountGroup from {ConnectionId}", Context.ConnectionId);
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"account_{accountId}");
                    _connectionTrackingService.RemoveConnection(Context.ConnectionId, accountGuid);
                    
                    var connectionCount = _connectionTrackingService.GetConnectionCount(accountGuid);
                    _logger.LogInformation("Client {ConnectionId} left account group {AccountId}. Remaining connections: {Count}", 
                        Context.ConnectionId, accountId, connectionCount);
                }
                else
                {
                    _logger.LogWarning("Invalid account ID format for LeaveAccountGroup: {AccountId} from {ConnectionId}", accountId, Context.ConnectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error leaving account group {AccountId} for connection {ConnectionId}", accountId, Context.ConnectionId);
                // Still try to clean up the connection tracking even if SignalR group removal failed
                try
                {
                    if (Guid.TryParse(accountId, out var accountGuid))
                    {
                        _connectionTrackingService.RemoveConnection(Context.ConnectionId, accountGuid);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(cleanupEx, "Error during cleanup for LeaveAccountGroup {AccountId} from {ConnectionId}", accountId, Context.ConnectionId);
                }
            }
        }

        public async Task SwitchAccountGroup(string fromAccountId, string toAccountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to switch account groups from {FromAccountId} to {ToAccountId} from {ConnectionId}", 
                    fromAccountId, toAccountId, Context.ConnectionId);
                Context.Abort();
                return;
            }

            _logger.LogInformation("Account group switch requested from {FromAccountId} to {ToAccountId} for connection {ConnectionId}", 
                fromAccountId, toAccountId, Context.ConnectionId);

            // Clean up any stale connections before switching
            _connectionTrackingService.CleanupStaleConnections();

            // Step 1: Remove from ALL existing SignalR groups first (comprehensive cleanup)
            var allExistingConnections = _connectionTrackingService.GetAllConnectionAccounts(Context.ConnectionId);
            foreach (var existingAccountId in allExistingConnections)
            {
                try
                {
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"account_{existingAccountId}");
                    _logger.LogDebug("Removed connection {ConnectionId} from SignalR group account_{ExistingAccountId}", 
                        Context.ConnectionId, existingAccountId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to remove connection {ConnectionId} from SignalR group account_{ExistingAccountId} (may not exist)", 
                        Context.ConnectionId, existingAccountId);
                }
            }

            // Step 2: Clean up connection tracking AFTER SignalR group removal
            try
            {
                _connectionTrackingService.ForceRemoveConnection(Context.ConnectionId);
                _logger.LogInformation("Force removed connection {ConnectionId} from {AccountCount} accounts", 
                    Context.ConnectionId, allExistingConnections.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup of existing group memberships for connection {ConnectionId}", Context.ConnectionId);
            }

            // Step 3: Join the new account group (if specified)
            if (!string.IsNullOrEmpty(toAccountId))
            {
                try
                {
                    if (Guid.TryParse(toAccountId, out var toAccountGuid))
                    {
                        // Add to SignalR group
                        await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{toAccountId}");
                        
                        // Add to connection tracking
                        _connectionTrackingService.AddConnection(Context.ConnectionId, toAccountGuid);
                        
                        var connectionCount = _connectionTrackingService.GetConnectionCount(toAccountGuid);
                        _logger.LogInformation("Client {ConnectionId} joined account group {AccountId}. Total connections: {Count}", 
                            Context.ConnectionId, toAccountId, connectionCount);
                    }
                    else
                    {
                        _logger.LogWarning("Invalid toAccountId format during switch: {ToAccountId}", toAccountId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error joining new account group {ToAccountId} during switch for connection {ConnectionId}", 
                        toAccountId, Context.ConnectionId);
                    throw; // Re-throw this one since it's critical for the new account
                }
            }

            _logger.LogInformation("Account group switch completed from {FromAccountId} to {ToAccountId} for connection {ConnectionId}", 
                fromAccountId, toAccountId, Context.ConnectionId);
        }

        public Task CleanupStaleConnections()
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to cleanup stale connections from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return Task.CompletedTask;
            }

            try
            {
                _connectionTrackingService.CleanupStaleConnections();
                _logger.LogInformation("Stale connections cleanup completed for connection {ConnectionId}", Context.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stale connections cleanup for connection {ConnectionId}", Context.ConnectionId);
            }
            
            return Task.CompletedTask;
        }

        public Task LeaveAllAccountGroups()
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to leave all account groups from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return Task.CompletedTask;
            }

            try
            {
                // Force remove this connection from all account groups
                _connectionTrackingService.ForceRemoveConnection(Context.ConnectionId);
                _logger.LogInformation("Connection {ConnectionId} removed from all account groups", Context.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error leaving all account groups for connection {ConnectionId}", Context.ConnectionId);
            }
            
            return Task.CompletedTask;
        }

        public Task PerformDeepConnectionCleanup()
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to perform deep connection cleanup from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return Task.CompletedTask;
            }

            try
            {
                _logger.LogInformation("Performing deep connection cleanup for long-running server instance");
                
                // Perform comprehensive cleanup - this helps after long periods of inactivity
                _connectionTrackingService.CleanupStaleConnections();
                
                // Also clean up this specific connection completely
                _connectionTrackingService.ForceRemoveConnection(Context.ConnectionId);
                
                _logger.LogInformation("Deep connection cleanup completed for connection {ConnectionId}", Context.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during deep connection cleanup for connection {ConnectionId}", Context.ConnectionId);
            }
            
            return Task.CompletedTask;
        }

        public async Task ValidateAndFixConnectionState(string accountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to validate connection state from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            if (string.IsNullOrEmpty(accountId))
            {
                _logger.LogWarning("Empty account ID provided for ValidateAndFixConnectionState from {ConnectionId}", Context.ConnectionId);
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var existingConnections = _connectionTrackingService.GetConnectionsForAccount(accountGuid).ToList();
                    var connectionCount = existingConnections.Count;
                    
                    _logger.LogInformation("Validating connection state for account {AccountId}: {Count} tracked connections [{Connections}]", 
                        accountId, connectionCount, string.Join(", ", existingConnections));

                    if (connectionCount > 1)
                    {
                        _logger.LogWarning("Multiple connections detected for account {AccountId}, cleaning up potentially stale connections", accountId);
                        
                        // Remove all existing connections from SignalR groups and tracking
                        foreach (var connectionId in existingConnections)
                        {
                            if (connectionId != Context.ConnectionId)
                            {
                                try
                                {
                                    await Groups.RemoveFromGroupAsync(connectionId, $"account_{accountId}");
                                    _logger.LogDebug("Removed stale connection {ConnectionId} from SignalR group for account {AccountId}", connectionId, accountId);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Expected failure removing stale connection {ConnectionId} from SignalR group", connectionId);
                                }
                            }
                        }
                        
                        // Use the replacement method to clean up tracking
                        _connectionTrackingService.ReplaceConnectionForAccount(accountGuid, Context.ConnectionId, existingConnections);
                        
                        // Re-join the current connection to the account group
                        await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{accountId}");
                        _connectionTrackingService.AddConnection(Context.ConnectionId, accountGuid);
                        
                        var finalCount = _connectionTrackingService.GetConnectionCount(accountGuid);
                        _logger.LogInformation("Connection state fixed for account {AccountId}: reduced from {OldCount} to {NewCount} connections", 
                            accountId, connectionCount, finalCount);
                    }
                    else if (!existingConnections.Contains(Context.ConnectionId))
                    {
                        _logger.LogInformation("Current connection {ConnectionId} not properly tracked for account {AccountId}, fixing...", Context.ConnectionId, accountId);
                        
                        // Clean up this connection and re-add properly
                        _connectionTrackingService.ForceRemoveConnection(Context.ConnectionId);
                        await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{accountId}");
                        _connectionTrackingService.AddConnection(Context.ConnectionId, accountGuid);
                        
                        _logger.LogInformation("Fixed connection tracking for {ConnectionId} on account {AccountId}", Context.ConnectionId, accountId);
                    }
                    else
                    {
                        _logger.LogDebug("Connection state for account {AccountId} is already valid", accountId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating/fixing connection state for account {AccountId}, connection {ConnectionId}", accountId, Context.ConnectionId);
            }
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

        public Task DebugConnectionState()
        {
            try
            {
                var allAccounts = _connectionTrackingService.GetAllConnectionAccounts(Context.ConnectionId);
                var accountCount = allAccounts.Count();
                
                _logger.LogInformation("Debug: Connection {ConnectionId} is associated with {AccountCount} accounts: [{Accounts}]",
                    Context.ConnectionId, accountCount, string.Join(", ", allAccounts));

                if (accountCount > 1)
                {
                    _logger.LogWarning("Debug: Connection {ConnectionId} has multiple account associations - this may cause radar sync issues",
                        Context.ConnectionId);
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error debugging connection state for {ConnectionId}", Context.ConnectionId);
                return Task.CompletedTask;
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

        public async Task RespondToScriptDialog(ScriptDialogResponseRequest request)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                var success = await _scriptDialogService.RespondToDialogAsync(request);
                if (!success)
                {
                    await Clients.Caller.ScriptDialogError("Failed to respond to script dialog");
                }
                else
                {
                    await Clients.Caller.ScriptDialogClosed(request.AccountId.ToString(), request.DialogId);
                    _logger.LogInformation("Script dialog response sent for account {AccountId}, dialog {DialogId}", 
                        request.AccountId, request.DialogId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to script dialog via SignalR");
                await Clients.Caller.ScriptDialogError("Error responding to script dialog");
            }
        }

        public async Task DismissScriptDialog(ScriptDialogDismissRequest request)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                var success = await _scriptDialogService.DismissDialogAsync(request);
                if (!success)
                {
                    await Clients.Caller.ScriptDialogError("Failed to dismiss script dialog");
                }
                else
                {
                    await Clients.Caller.ScriptDialogClosed(request.AccountId.ToString(), request.DialogId);
                    _logger.LogInformation("Script dialog dismissed for account {AccountId}, dialog {DialogId}", 
                        request.AccountId, request.DialogId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dismissing script dialog via SignalR");
                await Clients.Caller.ScriptDialogError("Error dismissing script dialog");
            }
        }

        public async Task RespondToScriptPermission(ScriptPermissionResponseRequest request)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                var success = await _scriptDialogService.RespondToPermissionAsync(request);
                if (!success)
                {
                    await Clients.Caller.ScriptDialogError("Failed to respond to script permission request");
                }
                else
                {
                    await Clients.Caller.ScriptPermissionClosed(request.AccountId.ToString(), request.RequestId);
                    _logger.LogInformation("Script permission response sent for account {AccountId}, request {RequestId}: {Grant}", 
                        request.AccountId, request.RequestId, request.Grant ? "Granted" : "Denied");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to script permission via SignalR");
                await Clients.Caller.ScriptDialogError("Error responding to script permission");
            }
        }

        public async Task GetActiveScriptDialogs(string accountId)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var dialogs = await _scriptDialogService.GetActiveDialogsAsync(accountGuid);
                    var permissions = await _scriptDialogService.GetActivePermissionsAsync(accountGuid);
                    
                    foreach (var dialog in dialogs)
                    {
                        await Clients.Caller.ScriptDialogReceived(dialog);
                    }
                    
                    foreach (var permission in permissions)
                    {
                        await Clients.Caller.ScriptPermissionReceived(permission);
                    }
                }
                else
                {
                    await Clients.Caller.ScriptDialogError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active script dialogs via SignalR");
                await Clients.Caller.ScriptDialogError("Error retrieving script dialogs");
            }
        }

        public async Task RespondToTeleportRequest(TeleportRequestResponseRequest request)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                var success = await _teleportRequestService.RespondToTeleportRequestAsync(request);
                
                if (!success)
                {
                    await Clients.Caller.TeleportRequestError("Failed to respond to teleport request");
                }
                else
                {
                    // Notify the caller that the request was handled
                    await Clients.Caller.TeleportRequestClosed(request.AccountId.ToString(), request.RequestId);
                    _logger.LogInformation("Teleport request response sent for account {AccountId}, request {RequestId}: {Accept}", 
                        request.AccountId, request.RequestId, request.Accept ? "Accepted" : "Declined");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to teleport request via SignalR");
                await Clients.Caller.TeleportRequestError("Error responding to teleport request");
            }
        }

        public async Task GetActiveTeleportRequests(string accountId)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    var requests = await _teleportRequestService.GetActiveTeleportRequestsAsync(accountGuid);
                    
                    foreach (var request in requests)
                    {
                        await Clients.Caller.TeleportRequestReceived(request);
                    }
                }
                else
                {
                    await Clients.Caller.TeleportRequestError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active teleport requests via SignalR");
                await Clients.Caller.TeleportRequestError("Error retrieving teleport requests");
            }
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current presence status for account {AccountId}", accountId);
                await Clients.Caller.PresenceError("Error retrieving presence status");
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var connectionId = Context.ConnectionId;
            
            if (exception != null)
            {
                _logger.LogWarning(exception, "Client disconnected with exception: {ConnectionId}", connectionId);
            }
            else
            {
                _logger.LogInformation("Client disconnected: {ConnectionId}", connectionId);
            }
            
            // Clean up connection tracking with error handling - use force removal to ensure cleanup
            try
            {
                _connectionTrackingService.ForceRemoveConnection(connectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up connection tracking for {ConnectionId}", connectionId);
            }
            
            // Handle browser close event (automatic status changes disabled)
            try
            {
                await _presenceService.HandleBrowserCloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling browser close event for {ConnectionId}", connectionId);
            }
            
            try
            {
                await base.OnDisconnectedAsync(exception);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in base OnDisconnectedAsync for {ConnectionId}", connectionId);
            }
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
        
        // Groups methods
        Task GroupsUpdated(string accountId, List<GroupDto> groups);
        
        // Sit/Stand methods
        Task SitStandSuccess(string message);
        Task SitStandError(string error);
        Task ObjectInfoReceived(ObjectInfo objectInfo);
        Task SittingStatusUpdated(object status);
        
        // Script Dialog methods
        Task ScriptDialogReceived(ScriptDialogDto dialog);
        Task ScriptDialogClosed(string accountId, string dialogId);
        Task ScriptPermissionReceived(ScriptPermissionDto permission);
        Task ScriptPermissionClosed(string accountId, string requestId);
        Task ScriptDialogError(string error);
        
        // Teleport Request methods
        Task TeleportRequestReceived(TeleportRequestDto request);
        Task TeleportRequestClosed(string accountId, string requestId);
        Task TeleportRequestError(string error);
    }
}