using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public class ConnectionTrackingService : IConnectionTrackingService, IDisposable
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> _accountConnections = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, bool>> _connectionAccounts = new();
        private readonly ILogger<ConnectionTrackingService> _logger;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private volatile bool _disposed = false;

        public ConnectionTrackingService(ILogger<ConnectionTrackingService> logger)
        {
            _logger = logger;
        }

        public void AddConnection(string connectionId, Guid accountId)
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
            {
                // Add to account -> connections mapping
                var accountConnections = _accountConnections.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, bool>());
                accountConnections.TryAdd(connectionId, true);

                // Add to connection -> accounts mapping
                var connectionAccounts = _connectionAccounts.GetOrAdd(connectionId, _ => new ConcurrentDictionary<Guid, bool>());
                connectionAccounts.TryAdd(accountId, true);

                _logger.LogDebug("Added connection {ConnectionId} to account {AccountId}. Total connections for account: {Count}",
                    connectionId, accountId, accountConnections.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding connection {ConnectionId} to account {AccountId}", connectionId, accountId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void RemoveConnection(string connectionId, Guid accountId)
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
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
                    connectionAccounts.TryRemove(accountId, out _);
                    
                    // Clean up if this was the last account for this connection
                    if (connectionAccounts.IsEmpty)
                    {
                        _connectionAccounts.TryRemove(connectionId, out _);
                    }
                }

                _logger.LogDebug("Removed connection {ConnectionId} from account {AccountId}. Remaining connections for account: {Count}",
                    connectionId, accountId, GetConnectionCount(accountId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing connection {ConnectionId} from account {AccountId}", connectionId, accountId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void RemoveConnection(string connectionId)
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
            {
                if (_connectionAccounts.TryRemove(connectionId, out var accountIds))
                {
                    // Remove this connection from all account mappings
                    foreach (var accountId in accountIds.Keys)
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing connection {ConnectionId} from all accounts", connectionId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public bool HasActiveConnections(Guid accountId)
        {
            if (_disposed) return false;

            _lock.EnterReadLock();
            try
            {
                return _accountConnections.TryGetValue(accountId, out var connections) && !connections.IsEmpty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking active connections for account {AccountId}", accountId);
                return false;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public int GetConnectionCount(Guid accountId)
        {
            if (_disposed) return 0;

            _lock.EnterReadLock();
            try
            {
                return _accountConnections.TryGetValue(accountId, out var connections) ? connections.Count : 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting connection count for account {AccountId}", accountId);
                return 0;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public IEnumerable<string> GetConnectionsForAccount(Guid accountId)
        {
            if (_disposed) return Enumerable.Empty<string>();

            _lock.EnterReadLock();
            try
            {
                if (_accountConnections.TryGetValue(accountId, out var connections))
                {
                    return connections.Keys.ToList(); // Return a copy to avoid concurrent modification
                }
                return Enumerable.Empty<string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting connections for account {AccountId}", accountId);
                return Enumerable.Empty<string>();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void CleanupStaleConnections()
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
            {
                var staleAccounts = new List<Guid>();
                
                foreach (var kvp in _accountConnections)
                {
                    if (kvp.Value.IsEmpty)
                    {
                        staleAccounts.Add(kvp.Key);
                    }
                }
                
                foreach (var accountId in staleAccounts)
                {
                    _accountConnections.TryRemove(accountId, out _);
                }
                
                var staleConnections = new List<string>();
                
                foreach (var kvp in _connectionAccounts)
                {
                    if (kvp.Value.IsEmpty)
                    {
                        staleConnections.Add(kvp.Key);
                    }
                }
                
                foreach (var connectionId in staleConnections)
                {
                    _connectionAccounts.TryRemove(connectionId, out _);
                }
                
                if (staleAccounts.Count > 0 || staleConnections.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {AccountCount} stale account entries and {ConnectionCount} stale connection entries",
                        staleAccounts.Count, staleConnections.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup of stale connections");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            _lock?.Dispose();
        }
    }
}