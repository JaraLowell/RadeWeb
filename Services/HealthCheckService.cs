using Microsoft.AspNetCore.SignalR;
using RadegastWeb.Hubs;
using RadegastWeb.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service to monitor and recover from degraded service states after extended runtime
    /// </summary>
    public interface IHealthCheckService
    {
        Task StartHealthChecksAsync();
        Task StopHealthChecksAsync();
        Task RunDiagnosticsAsync(Guid accountId);
        Task<HealthCheckResult> GetHealthStatusAsync();
        void RecordAvatarUpdate(Guid accountId);
        void RecordPresenceUpdate(Guid accountId);
    }

    public class HealthCheckResult
    {
        public DateTime LastCheck { get; set; }
        public bool IsHealthy { get; set; }
        public List<string> Issues { get; set; } = new();
        public List<string> RecoveryActions { get; set; } = new();
        public Dictionary<Guid, AccountHealthStatus> AccountStatuses { get; set; } = new();
    }

    public class AccountHealthStatus
    {
        public Guid AccountId { get; set; }
        public bool IsConnected { get; set; }
        public bool RadarWorking { get; set; }
        public bool PresenceWorking { get; set; }
        public bool SignalRWorking { get; set; }
        public DateTime LastAvatarUpdate { get; set; }
        public DateTime LastPresenceUpdate { get; set; }
        public int AvatarCount { get; set; }
        public List<string> Issues { get; set; } = new();
    }

    public class HealthCheckService : IHealthCheckService, IDisposable
    {
        private readonly IAccountService _accountService;
        private readonly IPresenceService _presenceService;
        private readonly IConnectionTrackingService _connectionTrackingService;
        private readonly IHubContext<RadegastHub, IRadegastHubClient> _hubContext;
        private readonly ILogger<HealthCheckService> _logger;
        
        private readonly ConcurrentDictionary<Guid, AccountHealthStatus> _accountHealthStatuses = new();
        private readonly ConcurrentDictionary<Guid, DateTime> _lastAvatarUpdates = new();
        private readonly ConcurrentDictionary<Guid, DateTime> _lastPresenceUpdates = new();
        
        private Timer? _healthCheckTimer;
        private bool _disposed = false;
        private readonly object _lockObject = new();
        
        // Health check intervals
        private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan AvatarUpdateTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PresenceUpdateTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ExtendedRuntimeThreshold = TimeSpan.FromHours(1);

        public HealthCheckService(
            IAccountService accountService,
            IPresenceService presenceService,
            IConnectionTrackingService connectionTrackingService,
            IHubContext<RadegastHub, IRadegastHubClient> hubContext,
            ILogger<HealthCheckService> logger)
        {
            _accountService = accountService;
            _presenceService = presenceService;
            _connectionTrackingService = connectionTrackingService;
            _hubContext = hubContext;
            _logger = logger;
        }

        public Task StartHealthChecksAsync()
        {
            if (_disposed) return Task.CompletedTask;

            lock (_lockObject)
            {
                if (_healthCheckTimer == null)
                {
                    _healthCheckTimer = new Timer(
                        PerformHealthCheck,
                        null,
                        HealthCheckInterval,
                        HealthCheckInterval);
                    
                    _logger.LogInformation("Health check service started with {Interval} minute intervals", 
                        HealthCheckInterval.TotalMinutes);
                }
            }

            return Task.CompletedTask;
        }

        public Task StopHealthChecksAsync()
        {
            lock (_lockObject)
            {
                _healthCheckTimer?.Dispose();
                _healthCheckTimer = null;
                _logger.LogInformation("Health check service stopped");
            }

            return Task.CompletedTask;
        }

        private async void PerformHealthCheck(object? state)
        {
            if (_disposed) return;

            try
            {
                _logger.LogDebug("Starting periodic health check");
                
                var accounts = await _accountService.GetAccountsAsync();
                var now = DateTime.UtcNow;
                var recoveryActions = new List<string>();

                foreach (var account in accounts)
                {
                    if (_disposed) return;

                    var accountHealth = await CheckAccountHealthAsync(account.Id, now);
                    _accountHealthStatuses.AddOrUpdate(account.Id, accountHealth, (k, v) => accountHealth);

                    // Apply recovery actions if needed
                    if (accountHealth.Issues.Any())
                    {
                        var actions = await AttemptRecoveryAsync(account.Id, accountHealth);
                        recoveryActions.AddRange(actions);
                    }
                }

                if (recoveryActions.Any())
                {
                    _logger.LogWarning("Health check completed with recovery actions: {Actions}", 
                        string.Join(", ", recoveryActions));
                }
                else
                {
                    _logger.LogDebug("Health check completed - all systems healthy");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic health check");
            }
        }

        private async Task<AccountHealthStatus> CheckAccountHealthAsync(Guid accountId, DateTime now)
        {
            var healthStatus = new AccountHealthStatus
            {
                AccountId = accountId,
                LastAvatarUpdate = _lastAvatarUpdates.GetValueOrDefault(accountId, DateTime.MinValue),
                LastPresenceUpdate = _lastPresenceUpdates.GetValueOrDefault(accountId, DateTime.MinValue)
            };

            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null)
                {
                    healthStatus.Issues.Add("Instance not found");
                    return healthStatus;
                }

                healthStatus.IsConnected = instance.IsConnected;
                
                if (!instance.IsConnected)
                {
                    healthStatus.Issues.Add("Account not connected to SL");
                    return healthStatus;
                }

                // Get comprehensive radar statistics
                var radarStats = instance.GetRadarStats();
                var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                var avatarList = nearbyAvatars.ToList();
                healthStatus.AvatarCount = avatarList.Count;

                // Enhanced radar functionality checks
                var currentSim = instance.Client.Network.CurrentSim;
                var simAvatarCount = currentSim?.ObjectsAvatars?.Count ?? 0;
                var coarseAvatarCount = currentSim?.AvatarPositions?.Count ?? 0;

                // Check if SL client has avatar data but we're not processing it
                if (simAvatarCount > 0 && healthStatus.AvatarCount == 0)
                {
                    healthStatus.RadarWorking = false;
                    healthStatus.Issues.Add($"SL client detects {simAvatarCount} avatars but radar shows 0 - data flow problem");
                }
                else if (coarseAvatarCount > healthStatus.AvatarCount + 5) // Allow some variance for position differences
                {
                    healthStatus.RadarWorking = false;
                    healthStatus.Issues.Add($"SL coarse location shows {coarseAvatarCount} avatars but radar shows {healthStatus.AvatarCount} - potential detection issue");
                }
                else if (healthStatus.LastAvatarUpdate != DateTime.MinValue)
                {
                    // Check if avatar updates are flowing
                    var timeSinceLastAvatarUpdate = now - healthStatus.LastAvatarUpdate;
                    if (timeSinceLastAvatarUpdate > AvatarUpdateTimeout)
                    {
                        healthStatus.RadarWorking = false;
                        healthStatus.Issues.Add($"No avatar updates for {timeSinceLastAvatarUpdate.TotalMinutes:F1} minutes");
                    }
                    else
                    {
                        healthStatus.RadarWorking = true;
                    }
                }
                else
                {
                    // No avatar update recorded yet - check if we should have had one by now
                    var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime;
                    if (uptime > TimeSpan.FromMinutes(5) && (simAvatarCount > 0 || coarseAvatarCount > 0))
                    {
                        healthStatus.RadarWorking = false;
                        healthStatus.Issues.Add("No avatar updates recorded despite SL client having avatar data");
                    }
                    else
                    {
                        // Still early in startup or truly no avatars around
                        healthStatus.RadarWorking = true;
                    }
                }

                // Check presence functionality
                var timeSinceLastPresenceUpdate = now - healthStatus.LastPresenceUpdate;
                if (healthStatus.LastPresenceUpdate != DateTime.MinValue && 
                    timeSinceLastPresenceUpdate > PresenceUpdateTimeout)
                {
                    healthStatus.PresenceWorking = false;
                    healthStatus.Issues.Add($"No presence updates for {timeSinceLastPresenceUpdate.TotalMinutes:F1} minutes");
                }
                else
                {
                    healthStatus.PresenceWorking = true;
                }

                // Check SignalR connections
                var connections = _connectionTrackingService.GetConnectionsForAccount(accountId);
                healthStatus.SignalRWorking = connections.Any();
                
                if (!healthStatus.SignalRWorking)
                {
                    healthStatus.Issues.Add("No active SignalR connections");
                }

                // Additional checks for extended runtime issues
                var serverUptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime;
                if (serverUptime > ExtendedRuntimeThreshold)
                {
                    // Check for signs of degradation specific to long-running instances
                    
                    // Check if event subscriptions might have been lost
                    if (healthStatus.RadarWorking && healthStatus.AvatarCount == 0 && 
                        serverUptime > TimeSpan.FromHours(6) && coarseAvatarCount > 0)
                    {
                        healthStatus.Issues.Add($"Long runtime ({serverUptime.TotalHours:F1}h) with no radar data despite coarse location data - possible event subscription loss");
                    }

                    // Check for memory leaks or performance degradation
                    if (connections.Count() > 10)
                    {
                        healthStatus.Issues.Add($"Excessive SignalR connections ({connections.Count()}) - possible connection leak");
                    }
                }

                _logger.LogDebug("Health check for account {AccountId}: Connected={Connected}, Radar={Radar}, " +
                    "AvatarCount={AvatarCount}, SimAvatars={SimAvatars}, CoarseAvatars={CoarseAvatars}, " +
                    "LastUpdate={LastUpdate}, Issues={IssueCount}",
                    accountId, healthStatus.IsConnected, healthStatus.RadarWorking, 
                    healthStatus.AvatarCount, simAvatarCount, coarseAvatarCount, 
                    healthStatus.LastAvatarUpdate, healthStatus.Issues.Count);
            }
            catch (Exception ex)
            {
                healthStatus.Issues.Add($"Health check error: {ex.Message}");
                _logger.LogError(ex, "Error checking health for account {AccountId}", accountId);
            }

            return healthStatus;
        }

        private async Task<List<string>> AttemptRecoveryAsync(Guid accountId, AccountHealthStatus healthStatus)
        {
            var actions = new List<string>();

            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance == null) return actions;

                // Recovery for radar issues
                if (!healthStatus.RadarWorking && healthStatus.IsConnected)
                {
                    _logger.LogWarning("Attempting radar recovery for account {AccountId}", accountId);
                    
                    // Check if this might be an event subscription issue
                    var currentSim = instance.Client.Network.CurrentSim;
                    var simHasAvatars = currentSim?.ObjectsAvatars?.Count > 0 || currentSim?.AvatarPositions?.Count > 0;
                    
                    if (simHasAvatars && healthStatus.AvatarCount == 0)
                    {
                        _logger.LogWarning("Detected potential event subscription loss for account {AccountId} - attempting to refresh", accountId);
                        
                        // Log the detection for diagnostic purposes
                        _logger.LogWarning("Account {AccountId} has {SimAvatars} sim avatars but radar shows {RadarAvatars} - potential event subscription issue", 
                            accountId, currentSim?.ObjectsAvatars?.Count ?? 0, healthStatus.AvatarCount);
                        actions.Add($"Detected event subscription issue for {accountId}");
                    }
                    
                    // Force refresh of nearby avatars
                    try
                    {
                        await instance.RefreshNearbyAvatarDisplayNamesAsync();
                        actions.Add($"Refreshed avatars for {accountId}");
                        
                        // Broadcast the refreshed list
                        var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
                        await _hubContext.Clients
                            .Group($"account_{accountId}")
                            .NearbyAvatarsUpdated(nearbyAvatars.ToList());
                        
                        actions.Add($"Broadcasted avatar update for {accountId}");
                        
                        // Record that we attempted recovery
                        _lastAvatarUpdates[accountId] = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during radar recovery for account {AccountId}", accountId);
                    }
                }

                // Recovery for presence issues
                if (!healthStatus.PresenceWorking && healthStatus.IsConnected)
                {
                    _logger.LogWarning("Attempting presence recovery for account {AccountId}", accountId);
                    
                    try
                    {
                        // Get current status and re-broadcast it
                        var currentStatus = _presenceService.GetAccountStatus(accountId);
                        await _hubContext.Clients
                            .Group($"account_{accountId}")
                            .PresenceStatusChanged(accountId.ToString(), currentStatus.ToString(), 
                                currentStatus.ToString());
                        
                        actions.Add($"Refreshed presence status for {accountId}");
                        _lastPresenceUpdates[accountId] = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during presence recovery for account {AccountId}", accountId);
                    }
                }

                // Recovery for SignalR connection issues
                if (!healthStatus.SignalRWorking)
                {
                    _logger.LogWarning("Attempting SignalR connection cleanup for account {AccountId}", accountId);
                    
                    try
                    {
                        _connectionTrackingService.CleanupStaleConnections();
                        actions.Add($"Cleaned up stale connections for {accountId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during SignalR recovery for account {AccountId}", accountId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during recovery for account {AccountId}", accountId);
            }

            return actions;
        }

        public async Task RunDiagnosticsAsync(Guid accountId)
        {
            try
            {
                _logger.LogInformation("Running diagnostics for account {AccountId}", accountId);
                
                var healthStatus = await CheckAccountHealthAsync(accountId, DateTime.UtcNow);
                
                _logger.LogInformation("Diagnostics for account {AccountId}: Connected={IsConnected}, " +
                    "Radar={RadarWorking}, Presence={PresenceWorking}, SignalR={SignalRWorking}, " +
                    "AvatarCount={AvatarCount}, Issues={Issues}",
                    accountId, healthStatus.IsConnected, healthStatus.RadarWorking, 
                    healthStatus.PresenceWorking, healthStatus.SignalRWorking, 
                    healthStatus.AvatarCount, string.Join(", ", healthStatus.Issues));

                if (healthStatus.Issues.Any())
                {
                    var actions = await AttemptRecoveryAsync(accountId, healthStatus);
                    _logger.LogInformation("Recovery actions taken for account {AccountId}: {Actions}",
                        accountId, string.Join(", ", actions));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running diagnostics for account {AccountId}", accountId);
            }
        }

        public Task<HealthCheckResult> GetHealthStatusAsync()
        {
            var result = new HealthCheckResult
            {
                LastCheck = DateTime.UtcNow,
                IsHealthy = true,
                AccountStatuses = new Dictionary<Guid, AccountHealthStatus>(_accountHealthStatuses)
            };

            foreach (var accountHealth in result.AccountStatuses.Values)
            {
                if (accountHealth.Issues.Any())
                {
                    result.IsHealthy = false;
                    result.Issues.AddRange(accountHealth.Issues);
                }
            }

            return Task.FromResult(result);
        }

        // Methods to track when services are working (called by other services)
        public void RecordAvatarUpdate(Guid accountId)
        {
            _lastAvatarUpdates[accountId] = DateTime.UtcNow;
        }

        public void RecordPresenceUpdate(Guid accountId)
        {
            _lastPresenceUpdates[accountId] = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _healthCheckTimer?.Dispose();
            _accountHealthStatuses.Clear();
            _lastAvatarUpdates.Clear();
            _lastPresenceUpdates.Clear();
            
            _logger.LogInformation("HealthCheckService disposed");
        }
    }
}