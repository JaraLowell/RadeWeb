namespace RadegastWeb.Services
{
    public interface IConnectionTrackingService
    {
        /// <summary>
        /// Track when a connection joins an account group
        /// </summary>
        void AddConnection(string connectionId, Guid accountId);
        
        /// <summary>
        /// Track when a connection leaves an account group
        /// </summary>
        void RemoveConnection(string connectionId, Guid accountId);
        
        /// <summary>
        /// Remove all tracking for a connection (on disconnect)
        /// </summary>
        void RemoveConnection(string connectionId);
        
        /// <summary>
        /// Check if there are any active connections for an account
        /// </summary>
        bool HasActiveConnections(Guid accountId);
        
        /// <summary>
        /// Get the count of active connections for an account
        /// </summary>
        int GetConnectionCount(Guid accountId);
    }
}