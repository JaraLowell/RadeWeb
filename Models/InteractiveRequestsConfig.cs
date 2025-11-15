namespace RadegastWeb.Models
{
    /// <summary>
    /// Configuration for interactive requests (friendship offers and group invitations)
    /// </summary>
    public class InteractiveRequestsConfig
    {
        /// <summary>
        /// Whether to automatically decline requests when user is not connected to the web interface
        /// Default: true
        /// </summary>
        public bool AutoDeclineWhenDisconnected { get; set; } = true;
        
        /// <summary>
        /// Delay in seconds before auto-declining a request when user is disconnected
        /// This gives the SL server time to process the request before we respond
        /// Default: 2 seconds
        /// </summary>
        public int AutoDeclineDelaySeconds { get; set; } = 2;
        
        /// <summary>
        /// How long friendship requests remain active before cleanup (in minutes)
        /// Default: 24 hours (1440 minutes)
        /// </summary>
        public int FriendshipRequestTimeoutMinutes { get; set; } = 1440;
        
        /// <summary>
        /// How long group invitations remain active before cleanup (in minutes)
        /// Default: 24 hours (1440 minutes)
        /// </summary>
        public int GroupInvitationTimeoutMinutes { get; set; } = 1440;
    }
}