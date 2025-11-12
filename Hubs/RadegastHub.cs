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
                    
                    // Get existing connections for this account
                    var existingConnections = _connectionTrackingService.GetConnectionsForAccount(accountGuid);
                    var currentConnectionCount = existingConnections.Count();
                    
                    _logger.LogDebug("Account {AccountId} currently has {Count} connections before join: [{Connections}]", 
                        accountId, currentConnectionCount, string.Join(", ", existingConnections));

                    // Check if this connection is already in the account group to prevent duplicates
                    if (existingConnections.Contains(Context.ConnectionId))
                    {
                        _logger.LogDebug("Connection {ConnectionId} already in account group {AccountId}, skipping duplicate join", 
                            Context.ConnectionId, accountId);
                        
                        // Even if already joined, still refresh the event subscriptions to ensure they're working
                        await RefreshEventSubscriptionsForReconnection(accountGuid);
                        return;
                    }

                    // Only clean up connections if we have an excessive number (likely indicates stale connections)
                    // Allow up to 3 simultaneous connections per account for legitimate multi-tab usage
                    const int maxAllowedConnections = 3;
                    
                    if (currentConnectionCount >= maxAllowedConnections)
                    {
                        _logger.LogWarning("Account {AccountId} has {Count} connections (>= {Max}), cleaning up oldest connections to prevent memory leaks", 
                            accountId, currentConnectionCount, maxAllowedConnections);
                        
                        // Remove oldest connections, keeping the most recent ones
                        var connectionsToRemove = existingConnections.Take(currentConnectionCount - maxAllowedConnections + 1);
                        
                        foreach (var existingConnectionId in connectionsToRemove)
                        {
                            _logger.LogDebug("Removing old connection {ExistingConnectionId} for account {AccountId} due to connection limit", 
                                existingConnectionId, accountId);
                            
                            // Try to remove from SignalR group (may fail if connection is truly stale)
                            try
                            {
                                await Groups.RemoveFromGroupAsync(existingConnectionId, $"account_{accountId}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to remove old connection {ExistingConnectionId} from SignalR group (connection may already be dead)", existingConnectionId);
                            }
                            
                            // Remove from tracking
                            _connectionTrackingService.RemoveConnection(existingConnectionId, accountGuid);
                        }
                    }
                    else if (currentConnectionCount > 0)
                    {
                        _logger.LogDebug("Account {AccountId} has {Count} existing connections, allowing multiple connections for multi-tab usage", 
                            accountId, currentConnectionCount);
                    }

                    // Clean up this specific connection from any other groups to prevent cross-contamination
                    _connectionTrackingService.ForceRemoveConnection(Context.ConnectionId);
                    
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{accountId}");
                    _connectionTrackingService.AddConnection(Context.ConnectionId, accountGuid);
                    
                    var finalConnectionCount = _connectionTrackingService.GetConnectionCount(accountGuid);
                    _logger.LogInformation("Client {ConnectionId} joined account group {AccountId}. Total connections: {Count}", 
                        Context.ConnectionId, accountId, finalConnectionCount);

                    // Force refresh avatar events to ensure data flows to web client
                    await RefreshEventSubscriptionsForReconnection(accountGuid);
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

        /// <summary>
        /// Helper method to refresh event subscriptions and send current data when a client (re)connects
        /// This addresses the core issue where events stop flowing after reconnection
        /// </summary>
        private async Task RefreshEventSubscriptionsForReconnection(Guid accountGuid)
        {
            try
            {
                var backgroundService = Context.GetHttpContext()?.RequestServices.GetService<RadegastBackgroundService>();
                if (backgroundService != null)
                {
                    await backgroundService.RefreshAccountSubscriptionAsync(accountGuid);
                    _logger.LogInformation("Refreshed event subscriptions for account {AccountId} after client connection", accountGuid);

                    // Immediately send current data to the (re)connected client
                    var instance = _accountService.GetInstance(accountGuid);
                    if (instance?.IsConnected == true)
                    {
                        // Send nearby avatars - ALWAYS send the update, even if empty
                        // This ensures the client gets a consistent state and clears old data
                        var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                        var avatarList = nearbyAvatars.ToList();
                        
                        await Clients.Caller.NearbyAvatarsUpdated(avatarList);
                        _logger.LogInformation("Sent {Count} nearby avatars to (re)connected client {ConnectionId} for account {AccountId}", 
                            avatarList.Count, Context.ConnectionId, accountGuid);
                        
                        // Also broadcast to the group to ensure all connections for this account get the update
                        await Clients.Group($"account_{accountGuid}").NearbyAvatarsUpdated(avatarList);
                        _logger.LogDebug("Also broadcasted {Count} avatars to group account_{AccountId}", avatarList.Count, accountGuid);

                        // Send current presence status
                        var presenceStatus = _presenceService.GetAccountStatus(accountGuid);
                        var statusText = presenceStatus switch
                        {
                            PresenceStatus.Away => "Away",
                            PresenceStatus.Busy => "Busy",
                            _ => "Online"
                        };
                        
                        await Clients.Caller.PresenceStatusChanged(accountGuid.ToString(), presenceStatus.ToString(), statusText);
                        
                        // Request fresh groups data
                        await Clients.Caller.GroupsUpdated(accountGuid.ToString(), new List<GroupDto>());
                        
                        // Trigger a fresh groups load
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var groups = await instance.GetGroupsAsync();
                                if (groups.Any())
                                {
                                    await Clients.Caller.GroupsUpdated(accountGuid.ToString(), groups.ToList());
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to send groups data to reconnected client");
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh event subscriptions for account {AccountId} - avatar updates may not flow properly", accountGuid);
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
                        
                        // Immediately send current data to the newly joined connection to prevent delays
                        try
                        {
                            await RefreshEventSubscriptionsForReconnection(toAccountGuid);
                            _logger.LogInformation("Sent immediate data refresh to connection {ConnectionId} for account {AccountId}", 
                                Context.ConnectionId, toAccountId);
                        }
                        catch (Exception refreshEx)
                        {
                            _logger.LogWarning(refreshEx, "Failed to send immediate data refresh during account switch");
                        }
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

                    // Skip the ping test - it may be causing interference with normal operations
                    // Instead, just ensure the current connection is properly tracked
                    _logger.LogDebug("Skipping connection ping test to avoid interference with avatar data flow");

                    // Ensure current connection is properly tracked
                    if (!existingConnections.Contains(Context.ConnectionId))
                    {
                        _logger.LogInformation("Current connection {ConnectionId} not properly tracked for account {AccountId}, adding...", Context.ConnectionId, accountId);
                        
                        await Groups.AddToGroupAsync(Context.ConnectionId, $"account_{accountId}");
                        _connectionTrackingService.AddConnection(Context.ConnectionId, accountGuid);
                        
                        _logger.LogInformation("Added connection tracking for {ConnectionId} on account {AccountId}", Context.ConnectionId, accountId);
                    }

                    var finalCount = _connectionTrackingService.GetConnectionCount(accountGuid);
                    _logger.LogDebug("Connection state validation completed for account {AccountId} - {Count} active connections", accountId, finalCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating/fixing connection state for account {AccountId}, connection {ConnectionId}", accountId, Context.ConnectionId);
            }
        }

        /// <summary>
        /// Heartbeat method for clients to indicate they are still active
        /// </summary>
        public Task Heartbeat()
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return Task.CompletedTask;
            }

            // Just receiving this call indicates the connection is alive
            // Update the connection activity timestamp to prevent stale cleanup
            _connectionTrackingService.UpdateConnectionActivity(Context.ConnectionId);
            // Reduced logging to avoid spam
            //_logger.LogDebug("Heartbeat received from connection {ConnectionId}", Context.ConnectionId);
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Force a complete state synchronization for the specified account
        /// This sends all current data (avatars, groups, presence, etc.) to the client
        /// Use this when the client suspects it has stale data
        /// </summary>
        public async Task ForceStateSynchronization(string accountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to force state sync from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogError("Invalid account ID for state sync: {AccountId}", accountId);
                    await Clients.Caller.ChatError("Invalid account ID");
                    return;
                }

                _logger.LogInformation("Force state synchronization requested for account {AccountId} by connection {ConnectionId}", accountId, Context.ConnectionId);
                
                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    _logger.LogError("No account instance found for state sync: {AccountId}", accountId);
                    await Clients.Caller.ChatError($"Account instance not found for {accountId}");
                    return;
                }

                // Send current avatar state (always send, even if empty)
                var nearbyAvatars = instance.IsConnected ? await instance.GetNearbyAvatarsAsync() : Enumerable.Empty<AvatarDto>();
                var avatarList = nearbyAvatars.ToList();
                await Clients.Caller.NearbyAvatarsUpdated(avatarList);
                _logger.LogInformation("State sync: sent {Count} nearby avatars for account {AccountId}", avatarList.Count, accountId);

                if (instance.IsConnected)
                {
                    // Send current presence status
                    var presenceStatus = _presenceService.GetAccountStatus(accountGuid);
                    var statusText = presenceStatus switch
                    {
                        PresenceStatus.Away => "Away",
                        PresenceStatus.Busy => "Busy",
                        _ => "Online"
                    };
                    await Clients.Caller.PresenceStatusChanged(accountId, presenceStatus.ToString(), statusText);
                    
                    // Send groups data
                    var groups = await instance.GetGroupsAsync();
                    await Clients.Caller.GroupsUpdated(accountId, groups.ToList());
                    
                    // Send region stats if available
                    try
                    {
                        var regionStats = await _accountService.GetRegionStatsAsync(accountGuid);
                        if (regionStats != null)
                        {
                            await Clients.Caller.RegionStatsUpdated(regionStats);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not get region stats during state sync for account {AccountId}", accountId);
                    }
                }
                
                _logger.LogInformation("Force state synchronization completed for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during force state synchronization for account {AccountId}", accountId);
                await Clients.Caller.ChatError($"Error during state synchronization: {ex.Message}");
            }
        }

        /// <summary>
        /// Enhanced method to diagnose and fix avatar event flow issues
        /// This combines multiple recovery strategies to ensure avatar data reaches the web client
        /// </summary>
        public async Task DiagnoseAndFixAvatarEvents(string accountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to diagnose avatar events from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogError("Invalid account ID for avatar event diagnosis: {AccountId}", accountId);
                    await Clients.Caller.ChatError("Invalid account ID");
                    return;
                }

                _logger.LogInformation("Starting comprehensive avatar events diagnosis and fix for account {AccountId} requested by connection {ConnectionId}", accountId, Context.ConnectionId);
                
                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    _logger.LogError("No account instance found for {AccountId}", accountId);
                    await Clients.Caller.ChatError($"Account instance not found for {accountId}");
                    return;
                }

                if (!instance.IsConnected)
                {
                    _logger.LogWarning("Account {AccountId} is not connected to SL during avatar event diagnosis", accountId);
                    await Clients.Caller.ChatError($"Account {accountId} is not connected to Second Life");
                    return;
                }

                // Step 1: Force refresh of event subscriptions
                var backgroundService = Context.GetHttpContext()?.RequestServices.GetService<RadegastBackgroundService>();
                if (backgroundService != null)
                {
                    await backgroundService.RefreshAccountSubscriptionAsync(accountGuid);
                    _logger.LogInformation("Refreshed background service event subscriptions for account {AccountId}", accountId);
                }

                // Step 2: Validate and fix connection state
                await ValidateAndFixConnectionState(accountId);

                // Step 3: Force refresh of nearby avatars and display names
                await instance.RefreshNearbyAvatarDisplayNamesAsync();
                
                // Step 4: Get current nearby avatars and broadcast them immediately
                var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                var avatarList = nearbyAvatars.ToList();
                
                _logger.LogInformation("Broadcasting {Count} avatars after comprehensive diagnosis for account {AccountId}", avatarList.Count, accountId);
                
                // Step 5: Broadcast to both caller and group to ensure delivery
                await Clients.Caller.NearbyAvatarsUpdated(avatarList);
                await Clients.Group($"account_{accountId}").NearbyAvatarsUpdated(avatarList);
                
                // Step 6: Send diagnostic summary
                var diagnosticMessage = $"Avatar events diagnosed and fixed - broadcasting {avatarList.Count} nearby avatars. Event subscriptions refreshed.";
                await Clients.Caller.ChatError(diagnosticMessage);
                
                _logger.LogInformation("Comprehensive avatar event diagnosis completed for account {AccountId} - sent {Count} avatars to web client", accountId, avatarList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during comprehensive avatar events diagnosis for account {AccountId}", accountId);
                await Clients.Caller.ChatError($"Error during avatar events diagnosis: {ex.Message}");
            }
        }

        /// <summary>
        /// Force refresh avatar event subscriptions and immediately broadcast current avatar data
        /// Call this when radar data stops flowing to the web client despite working radar
        /// </summary>
        public async Task RefreshAvatarEvents(string accountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to refresh avatar events from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogError("Invalid account ID for avatar event refresh: {AccountId}", accountId);
                    await Clients.Caller.ChatError("Invalid account ID");
                    return;
                }

                _logger.LogInformation("Refreshing avatar events for account {AccountId} requested by connection {ConnectionId}", accountId, Context.ConnectionId);
                
                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    _logger.LogError("No account instance found for {AccountId}", accountId);
                    await Clients.Caller.ChatError($"Account instance not found for {accountId}");
                    return;
                }

                if (!instance.IsConnected)
                {
                    _logger.LogWarning("Account {AccountId} is not connected to SL during avatar event refresh", accountId);
                    await Clients.Caller.ChatError($"Account {accountId} is not connected to Second Life");
                    return;
                }

                // Get the background service to force re-subscription of events
                var backgroundService = Context.GetHttpContext()?.RequestServices.GetService<RadegastBackgroundService>();
                if (backgroundService != null)
                {
                    // Force refresh of event subscriptions for this account
                    await backgroundService.RefreshAccountSubscriptionAsync(accountGuid);
                    _logger.LogInformation("Refreshed background service event subscriptions for account {AccountId}", accountId);
                }

                // Force refresh of nearby avatars and display names
                await instance.RefreshNearbyAvatarDisplayNamesAsync();
                
                // Get current nearby avatars and broadcast them immediately
                var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                var avatarList = nearbyAvatars.ToList();
                
                _logger.LogInformation("Broadcasting {Count} avatars after event refresh for account {AccountId}", avatarList.Count, accountId);
                
                // Validate and fix connection state first
                try
                {
                    await ValidateAndFixConnectionState(accountId);
                    _logger.LogDebug("Validated connection state for account {AccountId} during avatar event refresh", accountId);
                }
                catch (Exception validateEx)
                {
                    _logger.LogWarning(validateEx, "Connection state validation failed during avatar event refresh");
                }
                
                // Broadcast to both caller and group to ensure delivery
                await Clients.Caller.NearbyAvatarsUpdated(avatarList);
                await Clients.Group($"account_{accountId}").NearbyAvatarsUpdated(avatarList);
                
                await Clients.Caller.ChatError($"Avatar events refreshed - broadcasting {avatarList.Count} nearby avatars");
                
                _logger.LogInformation("Avatar event refresh completed for account {AccountId} - sent {Count} avatars to web client", accountId, avatarList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing avatar events for account {AccountId}", accountId);
                await Clients.Caller.ChatError($"Error refreshing avatar events: {ex.Message}");
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
                _logger.LogInformation("GetNearbyAvatars called for account {AccountId} by connection {ConnectionId}", accountId, Context.ConnectionId);
                
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogWarning("Invalid accountId format in GetNearbyAvatars: {AccountId}", accountId);
                    return;
                }

                // Check if the connection is in the right group
                var connectionAccounts = _connectionTrackingService.GetAllConnectionAccounts(Context.ConnectionId);
                var isInGroup = connectionAccounts.Contains(accountGuid);
                _logger.LogInformation("Connection {ConnectionId} in account group {AccountId}: {IsInGroup}", Context.ConnectionId, accountId, isInGroup);
                
                // Get account instance first
                _logger.LogDebug("Getting account instance for {AccountId}...", accountId);
                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    _logger.LogWarning("No account instance found for {AccountId} in GetNearbyAvatars", accountId);
                    await Clients.Caller.NearbyAvatarsUpdated(new List<AvatarDto>());
                    return;
                }
                
                _logger.LogDebug("Account instance found for {AccountId}, connected: {IsConnected}", accountId, instance.IsConnected);
                
                if (!instance.IsConnected)
                {
                    _logger.LogInformation("Account {AccountId} is not connected to SL, sending empty avatar list", accountId);
                    await Clients.Caller.NearbyAvatarsUpdated(new List<AvatarDto>());
                    return;
                }
                
                // Get avatars with timeout and detailed logging to identify bottlenecks
                _logger.LogDebug("Requesting nearby avatars for account {AccountId}...", accountId);
                List<AvatarDto> avatarList;
                
                try
                {
                    // Try direct instance method first to bypass AccountService layer
                    var avatars = await instance.GetNearbyAvatarsAsync().WaitAsync(TimeSpan.FromSeconds(3)); // 3 second timeout
                    avatarList = avatars.ToList();
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("GetNearbyAvatarsAsync timed out after 3 seconds for account {AccountId}, trying simple fallback", accountId);
                    
                    // Fallback: try to get avatars without display name processing
                    try
                    {
                        var simpleAvatars = instance.GetNearbyAvatars(); // Synchronous version
                        avatarList = simpleAvatars.ToList();
                        _logger.LogInformation("Fallback: Retrieved {Count} nearby avatars for account {AccountId} using simple method", 
                            avatarList.Count, accountId);
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "Fallback avatar retrieval also failed for account {AccountId}", accountId);
                        avatarList = new List<AvatarDto>();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving avatars for account {AccountId}", accountId);
                    avatarList = new List<AvatarDto>();
                }
                
                // Always send the response, even if empty
                await Clients.Caller.NearbyAvatarsUpdated(avatarList);
                
                // Also broadcast to the group if this connection is properly in the group
                if (isInGroup)
                {
                    await Clients.Group($"account_{accountId}").NearbyAvatarsUpdated(avatarList);
                    _logger.LogDebug("Also broadcasted {Count} avatars to group account_{AccountId}", avatarList.Count, accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting nearby avatars via SignalR for account {AccountId}, connection {ConnectionId}", accountId, Context.ConnectionId);
                // Send empty list on error to prevent client hanging
                try
                {
                    await Clients.Caller.NearbyAvatarsUpdated(new List<AvatarDto>());
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "Failed to send empty avatar list after error for account {AccountId}", accountId);
                }
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

        /// <summary>
        /// Load recent chat history for any session from database (last 48 hours or 1000 messages)
        /// MEMORY FIX: Query database instead of storing everything in memory
        /// Works for all chat types: local-chat, group-{groupId}, im-{avatarId}
        /// </summary>
        public async Task LoadRecentChatForSession(string accountId, string sessionId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to load chat for session {SessionId}, account {AccountId}", sessionId, accountId);
                Context.Abort();
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    IEnumerable<ChatMessageDto> recentMessages;
                    
                    // Handle local chat specially since it can have null/empty session IDs
                    if (sessionId == "local-chat")
                    {
                        recentMessages = await _chatHistoryService.GetRecentLocalChatAsync(accountGuid);
                        await Clients.Caller.RecentLocalChatLoaded(accountId, recentMessages.ToList());
                    }
                    else
                    {
                        recentMessages = await _chatHistoryService.GetRecentChatForSessionAsync(accountGuid, sessionId);
                        await Clients.Caller.ChatHistoryLoaded(accountId, sessionId, recentMessages.ToList());
                    }
                    
                    _logger.LogInformation("Loaded {MessageCount} recent chat messages for session {SessionId}, account {AccountId}", 
                        recentMessages.Count(), sessionId, accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading recent chat for session {SessionId} via SignalR for account {AccountId}", sessionId, accountId);
            }
        }

        /// <summary>
        /// Load recent local chat history from database (last 48 hours or 1000 messages)
        /// MEMORY FIX: Query database instead of storing everything in memory
        /// </summary>
        public async Task LoadRecentLocalChat(string accountId)
        {
            await LoadRecentChatForSession(accountId, "local-chat");
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
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to set away status from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogInformation("Setting away status for account {AccountId} to {Away} via SignalR", accountId, away);
                    
                    // Verify the account instance exists and is connected
                    var instance = _accountService.GetInstance(accountGuid);
                    if (instance == null)
                    {
                        _logger.LogError("Cannot set away status - account instance not found for {AccountId}", accountId);
                        await Clients.Caller.PresenceError("Account not found or not connected");
                        return;
                    }
                    
                    if (!instance.IsConnected)
                    {
                        _logger.LogError("Cannot set away status - account {AccountId} is not connected to SL", accountId);
                        await Clients.Caller.PresenceError("Account is not connected to Second Life");
                        return;
                    }
                    
                    await _presenceService.SetAwayAsync(accountGuid, away);
                    _logger.LogInformation("Successfully set away status for account {AccountId} to {Away}", accountId, away);
                }
                else
                {
                    _logger.LogError("Invalid account ID format for SetAwayStatus: {AccountId}", accountId);
                    await Clients.Caller.PresenceError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting away status for account {AccountId} to {Away} via SignalR", accountId, away);
                await Clients.Caller.PresenceError($"Error setting away status: {ex.Message}");
            }
        }

        public async Task SetBusyStatus(string accountId, bool busy)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to set busy status from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            try
            {
                if (Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogInformation("Setting busy status for account {AccountId} to {Busy} via SignalR", accountId, busy);
                    
                    // Verify the account instance exists and is connected
                    var instance = _accountService.GetInstance(accountGuid);
                    if (instance == null)
                    {
                        _logger.LogError("Cannot set busy status - account instance not found for {AccountId}", accountId);
                        await Clients.Caller.PresenceError("Account not found or not connected");
                        return;
                    }
                    
                    if (!instance.IsConnected)
                    {
                        _logger.LogError("Cannot set busy status - account {AccountId} is not connected to SL", accountId);
                        await Clients.Caller.PresenceError("Account is not connected to Second Life");
                        return;
                    }
                    
                    await _presenceService.SetBusyAsync(accountGuid, busy);
                    _logger.LogInformation("Successfully set busy status for account {AccountId} to {Busy}", accountId, busy);
                }
                else
                {
                    _logger.LogError("Invalid account ID format for SetBusyStatus: {AccountId}", accountId);
                    await Clients.Caller.PresenceError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting busy status for account {AccountId} to {Busy} via SignalR", accountId, busy);
                await Clients.Caller.PresenceError($"Error setting busy status: {ex.Message}");
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

        public async Task DebugRadarSync(string accountId)
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to debug radar sync from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            try
            {
                if (!Guid.TryParse(accountId, out var accountGuid))
                {
                    _logger.LogError("Invalid account ID for radar debug: {AccountId}", accountId);
                    await Clients.Caller.ChatError("Invalid account ID for radar debug");
                    return;
                }

                _logger.LogInformation("=== RADAR SYNC DEBUG START for Account {AccountId} ===", accountId);
                
                // Check connection tracking
                var trackedConnections = _connectionTrackingService.GetConnectionsForAccount(accountGuid);
                var connectionCount = trackedConnections.Count();
                _logger.LogInformation("Tracked connections for account {AccountId}: {Count} [{Connections}]",
                    accountId, connectionCount, string.Join(", ", trackedConnections));

                // Check if current connection is tracked
                var isCurrentConnectionTracked = trackedConnections.Contains(Context.ConnectionId);
                _logger.LogInformation("Is current connection {ConnectionId} tracked for account {AccountId}: {IsTracked}",
                    Context.ConnectionId, accountId, isCurrentConnectionTracked);

                // Get account instance and check its state
                var instance = _accountService.GetInstance(accountGuid);
                if (instance == null)
                {
                    _logger.LogError("No account instance found for {AccountId}", accountId);
                    await Clients.Caller.ChatError($"No account instance found for {accountId}");
                    return;
                }

                _logger.LogInformation("Account instance found for {AccountId}. Connected: {IsConnected}, Status: {Status}", 
                    accountId, instance.IsConnected, instance.Status);

                if (instance.IsConnected)
                {
                    // Get detailed radar statistics first
                    var radarStats = instance.GetRadarStats();
                    _logger.LogInformation("Radar Stats for {AccountId}: DetailedAvatars={Detailed}, CoarseAvatars={Coarse}, SimAvatars={Sim}, TotalUnique={Unique}",
                        accountId, radarStats.DetailedAvatarCount, radarStats.CoarseLocationAvatarCount, 
                        radarStats.SimAvatarCount, radarStats.TotalUniqueAvatars);

                    // Check if SL client network events are working
                    var clientConnected = instance.Client.Network.Connected;
                    var currentSim = instance.Client.Network.CurrentSim;
                    _logger.LogInformation("SL Client Status for {AccountId}: NetworkConnected={NetworkConnected}, CurrentSim={SimName}, SimAvatars={SimAvatarCount}",
                        accountId, clientConnected, currentSim?.Name ?? "null", currentSim?.AvatarPositions?.Count ?? 0);

                    // Check region info from current sim
                    if (currentSim != null)
                    {
                        _logger.LogInformation("Current Region for {AccountId}: {RegionName} at {X},{Y}",
                            accountId, currentSim.Name, currentSim.Handle >> 32, currentSim.Handle & 0xFFFF);
                    }

                    // Try to get nearby avatars
                    var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                    var avatarList = nearbyAvatars.ToList();
                    _logger.LogInformation("GetNearbyAvatarsAsync returned {Count} avatars for account {AccountId}", 
                        avatarList.Count, accountId);

                    if (avatarList.Count > 0)
                    {
                        foreach (var avatar in avatarList.Take(5)) // Log first 5 for debugging
                        {
                            _logger.LogInformation("  Avatar: {Name} ({Id}) - AccountId: {AvatarAccountId}, Distance: {Distance}m",
                                avatar.Name, avatar.Id, avatar.AccountId, avatar.Distance);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No nearby avatars returned - checking raw SL client data...");
                        
                        // Check raw SL client data
                        if (currentSim != null)
                        {
                            var simAvatars = currentSim.ObjectsAvatars.Values.Where(a => a.ID != instance.Client.Self.AgentID);
                            _logger.LogInformation("Raw SL Client ObjectsAvatars count: {Count}", simAvatars.Count());
                            
                            foreach (var rawAvatar in simAvatars.Take(3))
                            {
                                _logger.LogInformation("  Raw Avatar: {Name} ({Id}) - Pos: {Position}",
                                    rawAvatar.Name ?? "Unknown", rawAvatar.ID, rawAvatar.Position);
                            }
                            
                            var coarseAvatars = currentSim.AvatarPositions;
                            _logger.LogInformation("Raw SL Client AvatarPositions (coarse) count: {Count}", coarseAvatars?.Count ?? 0);
                        }
                    }

                    // Test direct broadcast to this connection
                    _logger.LogInformation("Testing direct broadcast to connection {ConnectionId}", Context.ConnectionId);
                    await Clients.Caller.NearbyAvatarsUpdated(avatarList);
                    
                    // Test group broadcast
                    _logger.LogInformation("Testing group broadcast to account_{AccountId}", accountId);
                    await Clients.Group($"account_{accountId}").NearbyAvatarsUpdated(avatarList);
                    
                    // Force refresh of display names and try again
                    _logger.LogInformation("Forcing display name refresh and avatar update...");
                    await instance.RefreshNearbyAvatarDisplayNamesAsync();
                    
                    // Get updated list after refresh
                    var refreshedAvatars = await instance.GetNearbyAvatarsAsync();
                    var refreshedList = refreshedAvatars.ToList();
                    _logger.LogInformation("After refresh: {Count} avatars", refreshedList.Count);
                    
                    if (refreshedList.Count != avatarList.Count)
                    {
                        _logger.LogWarning("Avatar count changed after refresh: {Before} -> {After}", 
                            avatarList.Count, refreshedList.Count);
                        
                        // Broadcast the updated list
                        await Clients.Group($"account_{accountId}").NearbyAvatarsUpdated(refreshedList);
                    }
                }
                else
                {
                    _logger.LogError("Account {AccountId} is not connected to Second Life - radar cannot work without SL connection", accountId);
                    await Clients.Caller.ChatError($"Account {accountId} is not connected to Second Life");
                }

                _logger.LogInformation("=== RADAR SYNC DEBUG END ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during radar sync debug for account {AccountId}", accountId);
                await Clients.Caller.ChatError($"Error during radar sync debug: {ex.Message}");
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

        /// <summary>
        /// Comprehensive recovery method for when SignalR connections are restored after network issues
        /// This should be called by the web client after reconnection to ensure all data flows properly
        /// </summary>
        public async Task RecoverConnection()
        {
            if (!IsAuthenticated())
            {
                _logger.LogWarning("Unauthenticated attempt to recover connection from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            try
            {
                _logger.LogInformation("Starting connection recovery for {ConnectionId}", Context.ConnectionId);
                
                // Get the background service to refresh all event subscriptions
                var backgroundService = Context.GetHttpContext()?.RequestServices.GetService<RadegastBackgroundService>();
                if (backgroundService != null)
                {
                    // Force refresh of ALL account subscriptions to ensure nothing is missed
                    await backgroundService.RefreshAllAccountSubscriptionsAsync();
                    _logger.LogInformation("Refreshed all account event subscriptions during connection recovery for {ConnectionId}", Context.ConnectionId);
                }
                
                // Clean up any stale connection tracking
                _connectionTrackingService.CleanupStaleConnections();
                
                _logger.LogInformation("Connection recovery completed for {ConnectionId}", Context.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during connection recovery for {ConnectionId}", Context.ConnectionId);
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

        public async Task RespondToFriendshipRequest(FriendshipRequestResponseRequest request)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                var friendshipService = Context.GetHttpContext()?.RequestServices.GetService<IFriendshipRequestService>();
                if (friendshipService == null)
                {
                    await Clients.Caller.FriendshipRequestError("Service not available");
                    return;
                }

                var success = await friendshipService.RespondToFriendshipRequestAsync(request);
                if (!success)
                {
                    await Clients.Caller.FriendshipRequestError("Failed to respond to friendship request");
                }
                else
                {
                    await Clients.Caller.FriendshipRequestClosed(request.AccountId.ToString(), request.RequestId);
                    _logger.LogInformation("Friendship request response sent for account {AccountId}, request {RequestId}: {Accept}", 
                        request.AccountId, request.RequestId, request.Accept ? "Accepted" : "Declined");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to friendship request via SignalR");
                await Clients.Caller.FriendshipRequestError("Error responding to friendship request");
            }
        }

        public async Task GetActiveFriendshipRequests(string accountId)
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
                    var friendshipService = Context.GetHttpContext()?.RequestServices.GetService<IFriendshipRequestService>();
                    if (friendshipService != null)
                    {
                        var requests = await friendshipService.GetActiveFriendshipRequestsAsync(accountGuid);
                        
                        foreach (var request in requests)
                        {
                            await Clients.Caller.FriendshipRequestReceived(request);
                        }
                    }
                }
                else
                {
                    await Clients.Caller.FriendshipRequestError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active friendship requests via SignalR");
                await Clients.Caller.FriendshipRequestError("Error retrieving friendship requests");
            }
        }

        public async Task RespondToGroupInvitation(GroupInvitationResponseRequest request)
        {
            if (!IsAuthenticated())
            {
                Context.Abort();
                return;
            }

            try
            {
                var groupInvitationService = Context.GetHttpContext()?.RequestServices.GetService<IGroupInvitationService>();
                if (groupInvitationService == null)
                {
                    await Clients.Caller.GroupInvitationError("Service not available");
                    return;
                }

                var success = await groupInvitationService.RespondToGroupInvitationAsync(request);
                if (!success)
                {
                    await Clients.Caller.GroupInvitationError("Failed to respond to group invitation");
                }
                else
                {
                    await Clients.Caller.GroupInvitationClosed(request.AccountId.ToString(), request.InvitationId);
                    _logger.LogInformation("Group invitation response sent for account {AccountId}, invitation {InvitationId}: {Accept}", 
                        request.AccountId, request.InvitationId, request.Accept ? "Accepted" : "Declined");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to group invitation via SignalR");
                await Clients.Caller.GroupInvitationError("Error responding to group invitation");
            }
        }

        public async Task GetActiveGroupInvitations(string accountId)
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
                    var groupInvitationService = Context.GetHttpContext()?.RequestServices.GetService<IGroupInvitationService>();
                    if (groupInvitationService != null)
                    {
                        var invitations = await groupInvitationService.GetActiveGroupInvitationsAsync(accountGuid);
                        
                        foreach (var invitation in invitations)
                        {
                            await Clients.Caller.GroupInvitationReceived(invitation);
                        }
                    }
                }
                else
                {
                    await Clients.Caller.GroupInvitationError("Invalid account ID");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active group invitations via SignalR");
                await Clients.Caller.GroupInvitationError("Error retrieving group invitations");
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
        Task RecentLocalChatLoaded(string accountId, List<ChatMessageDto> messages);
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
        
        // Friendship Request methods
        Task FriendshipRequestReceived(FriendshipRequestDto request);
        Task FriendshipRequestClosed(string accountId, string requestId);
        Task FriendshipRequestError(string error);
        
        // Group Invitation methods
        Task GroupInvitationReceived(GroupInvitationDto invitation);
        Task GroupInvitationClosed(string accountId, string invitationId);
        Task GroupInvitationError(string error);
    }
}