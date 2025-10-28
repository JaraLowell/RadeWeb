using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Represents a group invitation from another avatar
    /// </summary>
    public class GroupInvitationDto
    {
        /// <summary>
        /// Unique identifier for this group invitation
        /// </summary>
        public string InvitationId { get; set; } = string.Empty;
        
        /// <summary>
        /// Account this invitation belongs to
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// The UUID of the avatar sending the invitation
        /// </summary>
        public string FromAgentId { get; set; } = string.Empty;
        
        /// <summary>
        /// Name of the avatar sending the invitation
        /// </summary>
        public string FromAgentName { get; set; } = string.Empty;
        
        /// <summary>
        /// The group UUID being invited to
        /// </summary>
        public string GroupId { get; set; } = string.Empty;
        
        /// <summary>
        /// The group name
        /// </summary>
        public string GroupName { get; set; } = string.Empty;
        
        /// <summary>
        /// The invitation message
        /// </summary>
        public string Message { get; set; } = string.Empty;
        
        /// <summary>
        /// IM Session ID for the response
        /// </summary>
        public string SessionId { get; set; } = string.Empty;
        
        /// <summary>
        /// When the invitation was received
        /// </summary>
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Whether the invitation has been responded to
        /// </summary>
        public bool IsResponded { get; set; }
        
        /// <summary>
        /// When the invitation expires (group invitations don't typically expire, but we set a reasonable time)
        /// </summary>
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
        
        // SLT formatted timestamps for display
        public string? SLTReceivedAt { get; set; } // MMM dd, HH:mm:ss format
        public string? SLTExpiresAt { get; set; } // MMM dd, HH:mm:ss format
    }
    
    /// <summary>
    /// Request to respond to a group invitation
    /// </summary>
    public class GroupInvitationResponseRequest
    {
        /// <summary>
        /// Account ID
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// Invitation ID being responded to
        /// </summary>
        public string InvitationId { get; set; } = string.Empty;
        
        /// <summary>
        /// Whether to accept the group invitation
        /// </summary>
        public bool Accept { get; set; }
    }
    
    /// <summary>
    /// Event args for group invitation events
    /// </summary>
    public class GroupInvitationEventArgs : EventArgs
    {
        public GroupInvitationDto Invitation { get; set; }
        
        public GroupInvitationEventArgs(GroupInvitationDto invitation)
        {
            Invitation = invitation;
        }
    }
}