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
        
        /// <summary>
        /// Get all connections for an account (for cleanup purposes)
        /// </summary>
        IEnumerable<string> GetConnectionsForAccount(Guid accountId);
        
        /// <summary>
        /// Get all account IDs associated with a connection
        /// </summary>
        IEnumerable<Guid> GetAllConnectionAccounts(string connectionId);
        
        /// <summary>
        /// Clean up any stale connection tracking data
        /// </summary>
        void CleanupStaleConnections();
        
        /// <summary>
        /// Force remove a connection and clean up all its associated data
        /// </summary>
        void ForceRemoveConnection(string connectionId);
        
        /// <summary>
        /// Perform deep validation and cleanup of connection tracking state
        /// Use this after long periods of server uptime to prevent state drift
        /// </summary>
        void PerformDeepCleanup();
        
        /// <summary>
        /// Replace old connections for an account with a new connection (handles browser refresh)
        /// </summary>
        void ReplaceConnectionForAccount(Guid accountId, string newConnectionId, IEnumerable<string> oldConnectionIds);
    }
}