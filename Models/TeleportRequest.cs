using OpenMetaverse;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Represents a teleport request (offer) received from another avatar
    /// </summary>
    public class TeleportRequestDto
    {
        /// <summary>
        /// Unique identifier for this teleport request
        /// </summary>
        public string RequestId { get; set; } = string.Empty;
        
        /// <summary>
        /// Account this request belongs to
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// The UUID of the avatar offering the teleport
        /// </summary>
        public string FromAgentId { get; set; } = string.Empty;
        
        /// <summary>
        /// Name of the avatar offering the teleport
        /// </summary>
        public string FromAgentName { get; set; } = string.Empty;
        
        /// <summary>
        /// The teleport offer message
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
        /// When the request expires (teleport offers expire after a few minutes)
        /// </summary>
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(5);
    }
    
    /// <summary>
    /// Request to respond to a teleport offer
    /// </summary>
    public class TeleportRequestResponseRequest
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
        /// Whether to accept the teleport offer
        /// </summary>
        public bool Accept { get; set; }
    }
    
    /// <summary>
    /// Event args for teleport request events
    /// </summary>
    public class TeleportRequestEventArgs : EventArgs
    {
        public TeleportRequestDto Request { get; set; }
        
        public TeleportRequestEventArgs(TeleportRequestDto request)
        {
            Request = request;
        }
    }
}