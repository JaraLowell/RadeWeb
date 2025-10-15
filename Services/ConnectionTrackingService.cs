using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public class ConnectionTrackingService : IConnectionTrackingService
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> _accountConnections = new();
        private readonly ConcurrentDictionary<string, HashSet<Guid>> _connectionAccounts = new();
        private readonly ILogger<ConnectionTrackingService> _logger;
        private readonly object _lock = new object();

        public ConnectionTrackingService(ILogger<ConnectionTrackingService> logger)
        {
            _logger = logger;
        }

        public void AddConnection(string connectionId, Guid accountId)
        {
            lock (_lock)
            {
                // Add to account -> connections mapping
                var accountConnections = _accountConnections.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, bool>());
                accountConnections.TryAdd(connectionId, true);

                // Add to connection -> accounts mapping
                var connectionAccounts = _connectionAccounts.GetOrAdd(connectionId, _ => new HashSet<Guid>());
                connectionAccounts.Add(accountId);

                _logger.LogDebug("Added connection {ConnectionId} to account {AccountId}. Total connections for account: {Count}",
                    connectionId, accountId, accountConnections.Count);
            }
        }

        public void RemoveConnection(string connectionId, Guid accountId)
        {
            lock (_lock)
            {
                // Remove from account -> connections mapping
                if (_accountConnections.TryGetValue(accountId, out var accountConnections))
                {
                    accountConnections.TryRemove(connectionId, out _);
                    
                    // Clean up empty account entries
                    if (accountConnections.IsEmpty)
                    {
                        _accountConnections.TryRemove(accountId, out _);
                    }
                }

                // Remove from connection -> accounts mapping
                if (_connectionAccounts.TryGetValue(connectionId, out var connectionAccounts))
                {
                    connectionAccounts.Remove(accountId);
                    
                    // Clean up if this was the last account for this connection
                    if (connectionAccounts.Count == 0)
                    {
                        _connectionAccounts.TryRemove(connectionId, out _);
                    }
                }

                _logger.LogDebug("Removed connection {ConnectionId} from account {AccountId}. Remaining connections for account: {Count}",
                    connectionId, accountId, GetConnectionCount(accountId));
            }
        }

        public void RemoveConnection(string connectionId)
        {
            lock (_lock)
            {
                if (_connectionAccounts.TryRemove(connectionId, out var accountIds))
                {
                    // Remove this connection from all account mappings
                    foreach (var accountId in accountIds)
                    {
                        if (_accountConnections.TryGetValue(accountId, out var accountConnections))
                        {
                            accountConnections.TryRemove(connectionId, out _);
                            
                            // Clean up empty account entries
                            if (accountConnections.IsEmpty)
                            {
                                _accountConnections.TryRemove(accountId, out _);
                            }
                        }
                    }

                    _logger.LogDebug("Removed connection {ConnectionId} from {AccountCount} accounts",
                        connectionId, accountIds.Count);
                }
            }
        }

        public bool HasActiveConnections(Guid accountId)
        {
            return _accountConnections.TryGetValue(accountId, out var connections) && !connections.IsEmpty;
        }

        public int GetConnectionCount(Guid accountId)
        {
            return _accountConnections.TryGetValue(accountId, out var connections) ? connections.Count : 0;
        }
    }
}