using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenMetaverse;
using RadegastWeb.Core;
using RadegastWeb.Data;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for handling group invitations
    /// </summary>
    public class GroupInvitationService : IGroupInvitationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IAccountService _accountService;
        private readonly IConnectionTrackingService _connectionTrackingService;
        private readonly IOptions<InteractiveRequestsConfig> _config;
        private readonly ILogger<GroupInvitationService> _logger;
        
        // In-memory store for active group invitations (could be moved to database if needed)
        private readonly Dictionary<string, GroupInvitationDto> _activeInvitations = new();
        private readonly object _invitationsLock = new object();
        
        public event EventHandler<Models.GroupInvitationEventArgs>? GroupInvitationReceived;
        
        public GroupInvitationService(
            IServiceProvider serviceProvider,
            IAccountService accountService,
            IConnectionTrackingService connectionTrackingService,
            IOptions<InteractiveRequestsConfig> config,
            ILogger<GroupInvitationService> logger)
        {
            _serviceProvider = serviceProvider;
            _accountService = accountService;
            _connectionTrackingService = connectionTrackingService;
            _config = config;
            _logger = logger;
        }
        
        public Task<IEnumerable<GroupInvitationDto>> GetActiveGroupInvitationsAsync(Guid accountId)
        {
            lock (_invitationsLock)
            {
                var activeInvitations = _activeInvitations.Values
                    .Where(i => i.AccountId == accountId && !i.IsResponded && i.ExpiresAt > DateTime.UtcNow)
                    .ToList();
                    
                return Task.FromResult(activeInvitations.AsEnumerable());
            }
        }
        
        public async Task<bool> RespondToGroupInvitationAsync(GroupInvitationResponseRequest request)
        {
            try
            {
                GroupInvitationDto? groupInvitation;
                lock (_invitationsLock)
                {
                    if (!_activeInvitations.TryGetValue(request.InvitationId, out groupInvitation))
                    {
                        _logger.LogWarning("Group invitation {InvitationId} not found", request.InvitationId);
                        return false;
                    }
                    
                    if (groupInvitation.IsResponded)
                    {
                        _logger.LogWarning("Group invitation {InvitationId} already responded to", request.InvitationId);
                        return false;
                    }
                    
                    // Mark as responded
                    groupInvitation.IsResponded = true;
                }
                
                // Get the account instance
                var instance = _accountService.GetInstance(request.AccountId);
                if (instance == null || !instance.IsConnected)
                {
                    _logger.LogError("Account {AccountId} not found or not connected", request.AccountId);
                    return false;
                }
                
                // Parse the agent ID and session ID
                if (!UUID.TryParse(groupInvitation.FromAgentId, out var fromAgentId) ||
                    !UUID.TryParse(groupInvitation.SessionId, out var sessionId))
                {
                    _logger.LogError("Invalid agent ID or session ID in group invitation {InvitationId}", request.InvitationId);
                    return false;
                }
                
                // Send the response to Second Life (similar to Radegast implementation)
                var dialog = request.Accept 
                    ? InstantMessageDialog.GroupInvitationAccept 
                    : InstantMessageDialog.GroupInvitationDecline;
                
                instance.Client.Self.InstantMessage(
                    instance.Client.Self.Name,
                    fromAgentId,
                    string.Empty,
                    sessionId,
                    dialog,
                    InstantMessageOnline.Online,
                    Vector3.Zero,
                    UUID.Zero,
                    null);
                
                var actionText = request.Accept ? "Accepted" : "Declined";
                _logger.LogInformation("{Action} group invitation to {GroupName} from {FromAgentName} ({FromAgentId}) for account {AccountId}",
                    actionText, groupInvitation.GroupName, groupInvitation.FromAgentName, 
                    groupInvitation.FromAgentId, request.AccountId);
                
                // If accepted, refresh groups cache to show the new group in the UI
                if (request.Accept)
                {
                    _logger.LogDebug("Refreshing groups cache after accepting invitation to {GroupName} for account {AccountId}",
                        groupInvitation.GroupName, request.AccountId);
                    instance.RefreshGroupsCache();
                }
                
                // Update the interactive notice if one exists
                await UpdateInteractiveNoticeAsync(request.AccountId, request.InvitationId, request.Accept);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to group invitation {InvitationId} for account {AccountId}",
                    request.InvitationId, request.AccountId);
                return false;
            }
        }
        
        public Task<GroupInvitationDto> StoreGroupInvitationAsync(GroupInvitationDto invitation)
        {
            lock (_invitationsLock)
            {
                _activeInvitations[invitation.InvitationId] = invitation;
            }
            
            _logger.LogInformation("Stored group invitation {InvitationId} to {GroupName} from {FromAgentName} for account {AccountId}",
                invitation.InvitationId, invitation.GroupName, invitation.FromAgentName, invitation.AccountId);
                
            // Check if user is actively connected via web interface
            if (_config.Value.AutoDeclineWhenDisconnected && !_connectionTrackingService.HasActiveConnections(invitation.AccountId))
            {
                _logger.LogInformation("No active web connections for account {AccountId}, auto-declining group invitation to {GroupName} from {FromAgentName}",
                    invitation.AccountId, invitation.GroupName, invitation.FromAgentName);
                
                // Auto-decline in background after configured delay to ensure SL server is ready
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_config.Value.AutoDeclineDelaySeconds * 1000);
                    await AutoDeclineGroupInvitationAsync(invitation);
                });
            }
            else
            {
                // Fire the event only if user is connected or auto-decline is disabled
                GroupInvitationReceived?.Invoke(this, new Models.GroupInvitationEventArgs(invitation));
            }
            
            return Task.FromResult(invitation);
        }
        
        public Task CleanupExpiredInvitationsAsync()
        {
            lock (_invitationsLock)
            {
                var expiredInvitations = _activeInvitations
                    .Where(kvp => kvp.Value.ExpiresAt <= DateTime.UtcNow)
                    .Select(kvp => kvp.Key)
                    .ToList();
                    
                foreach (var invitationId in expiredInvitations)
                {
                    _activeInvitations.Remove(invitationId);
                    _logger.LogDebug("Removed expired group invitation {InvitationId}", invitationId);
                }
            }
            
            return Task.CompletedTask;
        }
        
        private async Task AutoDeclineGroupInvitationAsync(GroupInvitationDto invitation)
        {
            try
            {
                // Auto-decline the group invitation
                var declineRequest = new GroupInvitationResponseRequest
                {
                    AccountId = invitation.AccountId,
                    InvitationId = invitation.InvitationId,
                    Accept = false
                };
                
                await RespondToGroupInvitationAsync(declineRequest);
                _logger.LogInformation("Auto-declined group invitation {InvitationId} to {GroupName} from {FromAgentName} for disconnected account {AccountId}",
                    invitation.InvitationId, invitation.GroupName, invitation.FromAgentName, invitation.AccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error auto-declining group invitation {InvitationId} for account {AccountId}",
                    invitation.InvitationId, invitation.AccountId);
            }
        }
        
        private async Task UpdateInteractiveNoticeAsync(Guid accountId, string invitationId, bool accepted)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                using var context = scope.ServiceProvider.GetRequiredService<RadegastDbContext>();
                
                var notice = await context.Notices
                    .FirstOrDefaultAsync(n => n.AccountId == accountId && 
                                            n.ExternalRequestId == invitationId && 
                                            n.Type == "GroupInvitation");
                
                if (notice != null)
                {
                    notice.HasResponse = true;
                    notice.AcceptedResponse = accepted;
                    notice.RespondedAt = DateTime.UtcNow;
                    notice.IsRead = true; // Mark as read when responded
                    
                    await context.SaveChangesAsync();
                    _logger.LogDebug("Updated interactive notice for group invitation {InvitationId}", invitationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating interactive notice for group invitation {InvitationId}", invitationId);
            }
        }
    }
}