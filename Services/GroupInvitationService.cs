using Microsoft.EntityFrameworkCore;
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
        private readonly ILogger<GroupInvitationService> _logger;
        
        // In-memory store for active group invitations (could be moved to database if needed)
        private readonly Dictionary<string, GroupInvitationDto> _activeInvitations = new();
        private readonly object _invitationsLock = new object();
        
        public event EventHandler<Models.GroupInvitationEventArgs>? GroupInvitationReceived;
        
        public GroupInvitationService(
            IServiceProvider serviceProvider,
            IAccountService accountService,
            ILogger<GroupInvitationService> logger)
        {
            _serviceProvider = serviceProvider;
            _accountService = accountService;
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
                
            // Fire the event
            GroupInvitationReceived?.Invoke(this, new Models.GroupInvitationEventArgs(invitation));
            
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