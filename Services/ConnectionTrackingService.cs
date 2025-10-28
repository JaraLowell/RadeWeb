using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public class ConnectionTrackingService : IConnectionTrackingService, IDisposable
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> _accountConnections = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, bool>> _connectionAccounts = new();
        private readonly ConcurrentDictionary<string, DateTime> _connectionTimestamps = new();
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

                // Track connection timestamp
                _connectionTimestamps.AddOrUpdate(connectionId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);

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

            int remainingConnections = 0;
            _lock.EnterWriteLock();
            try
            {
                // Remove from account -> connections mapping
                if (_accountConnections.TryGetValue(accountId, out var accountConnections))
                {
                    accountConnections.TryRemove(connectionId, out _);
                    remainingConnections = accountConnections.Count; // Get count while we have the lock
                    
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
                        _connectionTimestamps.TryRemove(connectionId, out _);
                    }
                }

                _logger.LogDebug("Removed connection {ConnectionId} from account {AccountId}. Remaining connections for account: {Count}",
                    connectionId, accountId, remainingConnections);
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

        public IEnumerable<Guid> GetAllConnectionAccounts(string connectionId)
        {
            if (_disposed) return Enumerable.Empty<Guid>();

            _lock.EnterReadLock();
            try
            {
                if (_connectionAccounts.TryGetValue(connectionId, out var accounts))
                {
                    return accounts.Keys.ToList(); // Return a copy to avoid concurrent modification
                }
                return Enumerable.Empty<Guid>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting accounts for connection {ConnectionId}", connectionId);
                return Enumerable.Empty<Guid>();
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
                // Step 1: Clean up empty collections (existing logic)
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

                // Step 2: Clean up connections that are older than 5 minutes without activity
                var now = DateTime.UtcNow;
                var oldConnections = new List<string>();
                
                foreach (var kvp in _connectionTimestamps)
                {
                    if (now - kvp.Value > TimeSpan.FromMinutes(5))
                    {
                        oldConnections.Add(kvp.Key);
                        _logger.LogInformation("Marking connection {ConnectionId} as stale - last activity: {LastActivity:yyyy-MM-dd HH:mm:ss} UTC", 
                            kvp.Key, kvp.Value);
                    }
                }
                
                // Force remove old connections (inline to avoid recursion)
                foreach (var connectionId in oldConnections)
                {
                    // Remove from timestamp tracking
                    _connectionTimestamps.TryRemove(connectionId, out _);
                    
                    // Get all accounts this connection was associated with
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
                        
                        _logger.LogDebug("Cleaned up old connection {ConnectionId} from {AccountCount} accounts",
                            connectionId, accountIds.Count);
                    }
                }
                
                if (staleAccounts.Count > 0 || staleConnections.Count > 0 || oldConnections.Count > 0)
                {
                    _logger.LogInformation("Cleaned up {AccountCount} stale account entries, {ConnectionCount} stale connection entries, and {OldCount} old connections",
                        staleAccounts.Count, staleConnections.Count, oldConnections.Count);
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

        public void ForceRemoveConnection(string connectionId)
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
            {
                // Remove from timestamp tracking
                _connectionTimestamps.TryRemove(connectionId, out _);
                
                // Get all accounts this connection was associated with
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

                    _logger.LogInformation("Force removed connection {ConnectionId} from {AccountCount} accounts",
                        connectionId, accountIds.Count);
                }
                else
                {
                    _logger.LogDebug("Connection {ConnectionId} was not found in tracking (already cleaned up)", connectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error force removing connection {ConnectionId}", connectionId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void PerformDeepCleanup()
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
            {
                var totalAccountsBefore = _accountConnections.Count;
                var totalConnectionsBefore = _connectionAccounts.Count;
                
                _logger.LogInformation("Starting deep cleanup - Accounts: {AccountCount}, Connections: {ConnectionCount}", 
                    totalAccountsBefore, totalConnectionsBefore);

                // First pass: Remove all empty collections
                var emptyAccounts = new List<Guid>();
                foreach (var kvp in _accountConnections.ToList())
                {
                    if (kvp.Value.IsEmpty)
                    {
                        emptyAccounts.Add(kvp.Key);
                    }
                }

                var emptyConnections = new List<string>();
                foreach (var kvp in _connectionAccounts.ToList())
                {
                    if (kvp.Value.IsEmpty)
                    {
                        emptyConnections.Add(kvp.Key);
                    }
                }

                // Remove empty entries
                foreach (var accountId in emptyAccounts)
                {
                    _accountConnections.TryRemove(accountId, out _);
                }

                foreach (var connectionId in emptyConnections)
                {
                    _connectionAccounts.TryRemove(connectionId, out _);
                }

                // Second pass: Validate data consistency between both dictionaries
                var inconsistentConnections = new List<string>();
                
                foreach (var connectionEntry in _connectionAccounts.ToList())
                {
                    var connectionId = connectionEntry.Key;
                    var accountIds = connectionEntry.Value;
                    
                    foreach (var accountId in accountIds.Keys.ToList())
                    {
                        // Check if the reverse mapping exists
                        if (!_accountConnections.TryGetValue(accountId, out var accountConnections) || 
                            !accountConnections.ContainsKey(connectionId))
                        {
                            // Inconsistency detected - remove from connection mapping
                            accountIds.TryRemove(accountId, out _);
                            _logger.LogWarning("Removed inconsistent connection {ConnectionId} -> account {AccountId} mapping", 
                                connectionId, accountId);
                        }
                    }
                    
                    // If connection has no accounts left, mark for removal
                    if (accountIds.IsEmpty)
                    {
                        inconsistentConnections.Add(connectionId);
                    }
                }

                // Remove connections with no accounts
                foreach (var connectionId in inconsistentConnections)
                {
                    _connectionAccounts.TryRemove(connectionId, out _);
                }

                // Third pass: Clean up account mappings with invalid connections
                var inconsistentAccounts = new List<Guid>();
                
                foreach (var accountEntry in _accountConnections.ToList())
                {
                    var accountId = accountEntry.Key;
                    var connections = accountEntry.Value;
                    
                    foreach (var connectionId in connections.Keys.ToList())
                    {
                        // Check if the reverse mapping exists
                        if (!_connectionAccounts.TryGetValue(connectionId, out var connectionAccounts) || 
                            !connectionAccounts.ContainsKey(accountId))
                        {
                            // Inconsistency detected - remove from account mapping
                            connections.TryRemove(connectionId, out _);
                            _logger.LogWarning("Removed inconsistent account {AccountId} -> connection {ConnectionId} mapping", 
                                accountId, connectionId);
                        }
                    }
                    
                    // If account has no connections left, mark for removal
                    if (connections.IsEmpty)
                    {
                        inconsistentAccounts.Add(accountId);
                    }
                }

                // Remove accounts with no connections
                foreach (var accountId in inconsistentAccounts)
                {
                    _accountConnections.TryRemove(accountId, out _);
                }

                var totalAccountsAfter = _accountConnections.Count;
                var totalConnectionsAfter = _connectionAccounts.Count;
                
                _logger.LogInformation("Deep cleanup completed - Before: {AccountsBefore} accounts, {ConnectionsBefore} connections | After: {AccountsAfter} accounts, {ConnectionsAfter} connections | Cleaned: {EmptyAccounts} empty accounts, {EmptyConnections} empty connections, {InconsistentAccounts} inconsistent accounts, {InconsistentConnections} inconsistent connections", 
                    totalAccountsBefore, totalConnectionsBefore, totalAccountsAfter, totalConnectionsAfter,
                    emptyAccounts.Count, emptyConnections.Count, inconsistentAccounts.Count, inconsistentConnections.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during deep cleanup");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void ReplaceConnectionForAccount(Guid accountId, string newConnectionId, IEnumerable<string> oldConnectionIds)
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
            {
                // First remove all old connections for this account
                foreach (var oldConnectionId in oldConnectionIds)
                {
                    if (oldConnectionId != newConnectionId)
                    {
                        // Remove from connection -> accounts mapping
                        if (_connectionAccounts.TryGetValue(oldConnectionId, out var connectionAccounts))
                        {
                            connectionAccounts.TryRemove(accountId, out _);
                            if (connectionAccounts.IsEmpty)
                            {
                                _connectionAccounts.TryRemove(oldConnectionId, out _);
                            }
                        }

                        // Remove from account -> connections mapping
                        if (_accountConnections.TryGetValue(accountId, out var accountConnections))
                        {
                            accountConnections.TryRemove(oldConnectionId, out _);
                        }

                        _logger.LogInformation("Removed old connection {OldConnectionId} from account {AccountId} during replacement", 
                            oldConnectionId, accountId);
                    }
                }

                // Add the new connection if it's not already there
                var accountConnectionsForAdd = _accountConnections.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, bool>());
                var connectionAccountsForAdd = _connectionAccounts.GetOrAdd(newConnectionId, _ => new ConcurrentDictionary<Guid, bool>());
                
                accountConnectionsForAdd.TryAdd(newConnectionId, true);
                connectionAccountsForAdd.TryAdd(accountId, true);

                _logger.LogInformation("Replaced {OldCount} old connections with new connection {NewConnectionId} for account {AccountId}", 
                    oldConnectionIds.Count(), newConnectionId, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error replacing connections for account {AccountId}", accountId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Updates the last activity timestamp for a connection to prevent it from being cleaned up as stale
        /// </summary>
        public void UpdateConnectionActivity(string connectionId)
        {
            if (_disposed) return;

            _connectionTimestamps.AddOrUpdate(connectionId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
            // Reduced logging to avoid spam
            //_logger.LogDebug("Updated activity timestamp for connection {ConnectionId}", connectionId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            _lock?.Dispose();
        }
    }
}