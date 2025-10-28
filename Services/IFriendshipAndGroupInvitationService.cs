using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for handling friendship requests
    /// </summary>
    public interface IFriendshipRequestService
    {
        /// <summary>
        /// Get active friendship requests for an account
        /// </summary>
        Task<IEnumerable<FriendshipRequestDto>> GetActiveFriendshipRequestsAsync(Guid accountId);
        
        /// <summary>
        /// Respond to a friendship request
        /// </summary>
        Task<bool> RespondToFriendshipRequestAsync(FriendshipRequestResponseRequest request);
        
        /// <summary>
        /// Store a new friendship request
        /// </summary>
        Task<FriendshipRequestDto> StoreFriendshipRequestAsync(FriendshipRequestDto request);
        
        /// <summary>
        /// Clean up expired requests
        /// </summary>
        Task CleanupExpiredRequestsAsync();
        
        /// <summary>
        /// Event fired when a friendship request is received
        /// </summary>
        event EventHandler<FriendshipRequestEventArgs>? FriendshipRequestReceived;
    }
    
    /// <summary>
    /// Service for handling group invitations
    /// </summary>
    public interface IGroupInvitationService
    {
        /// <summary>
        /// Get active group invitations for an account
        /// </summary>
        Task<IEnumerable<GroupInvitationDto>> GetActiveGroupInvitationsAsync(Guid accountId);
        
        /// <summary>
        /// Respond to a group invitation
        /// </summary>
        Task<bool> RespondToGroupInvitationAsync(GroupInvitationResponseRequest request);
        
        /// <summary>
        /// Store a new group invitation
        /// </summary>
        Task<GroupInvitationDto> StoreGroupInvitationAsync(GroupInvitationDto invitation);
        
        /// <summary>
        /// Clean up expired invitations
        /// </summary>
        Task CleanupExpiredInvitationsAsync();
        
        /// <summary>
        /// Event fired when a group invitation is received
        /// </summary>
        event EventHandler<Models.GroupInvitationEventArgs>? GroupInvitationReceived;
    }
}