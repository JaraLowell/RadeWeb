using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Represents a friendship offer from another avatar
    /// </summary>
    public class FriendshipRequestDto
    {
        /// <summary>
        /// Unique identifier for this friendship request
        /// </summary>
        public string RequestId { get; set; } = string.Empty;
        
        /// <summary>
        /// Account this request belongs to
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// The UUID of the avatar offering friendship
        /// </summary>
        public string FromAgentId { get; set; } = string.Empty;
        
        /// <summary>
        /// Name of the avatar offering friendship
        /// </summary>
        public string FromAgentName { get; set; } = string.Empty;
        
        /// <summary>
        /// The friendship offer message
        /// </summary>
        public string Message { get; set; } = string.Empty;
        
        /// <summary>
        /// IM Session ID for the response
        /// </summary>
        public string SessionId { get; set; } = string.Empty;
        
        /// <summary>
        /// When the request was received
        /// </summary>
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Whether the request has been responded to
        /// </summary>
        public bool IsResponded { get; set; }
        
        /// <summary>
        /// When the request expires (friendship offers don't typically expire, but we set a reasonable time)
        /// </summary>
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
        
        // SLT formatted timestamps for display
        public string? SLTReceivedAt { get; set; } // MMM dd, HH:mm:ss format
        public string? SLTExpiresAt { get; set; } // MMM dd, HH:mm:ss format
    }
    
    /// <summary>
    /// Request to respond to a friendship offer
    /// </summary>
    public class FriendshipRequestResponseRequest
    {
        /// <summary>
        /// Account ID
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// Request ID being responded to
        /// </summary>
        public string RequestId { get; set; } = string.Empty;
        
        /// <summary>
        /// Whether to accept the friendship offer
        /// </summary>
        public bool Accept { get; set; }
    }
    
    /// <summary>
    /// Event args for friendship request events
    /// </summary>
    public class FriendshipRequestEventArgs : EventArgs
    {
        public FriendshipRequestDto Request { get; set; }
        
        public FriendshipRequestEventArgs(FriendshipRequestDto request)
        {
            Request = request;
        }
    }
}