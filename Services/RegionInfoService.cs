using RadegastWeb.Models;
using RadegastWeb.Core;
using OpenMetaverse;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public interface IRegionInfoService
    {
        Task<RegionStatsDto?> GetRegionStatsAsync(Guid accountId);
        Task UpdateRegionStatsAsync(Guid accountId);
        Task StartPeriodicUpdatesAsync(Guid accountId);
        Task StopPeriodicUpdatesAsync(Guid accountId);
        void CleanupAccount(Guid accountId);
        event EventHandler<RegionStatsUpdatedEventArgs> RegionStatsUpdated;
    }

    public class RegionStatsUpdatedEventArgs : EventArgs
    {
        public Guid AccountId { get; set; }
        public RegionStatsDto Stats { get; set; } = new();
    }

    public class RegionInfoService : IRegionInfoService, IDisposable
    {
        private readonly IAccountService _accountService;
        private readonly ILogger<RegionInfoService> _logger;
        private readonly ConcurrentDictionary<Guid, RegionStatsDto> _regionStats = new();
        private readonly ConcurrentDictionary<Guid, Timer> _updateTimers = new();
        private readonly ConcurrentDictionary<Guid, bool> _isUpdating = new();
        private readonly ConcurrentDictionary<Guid, DateTime> _lastBroadcastTime = new();
        private bool _disposed = false;

        // MEMORY FIX: Update interval in milliseconds (reduced from 1 second to 5 seconds to reduce memory pressure)
        // Frequent updates cause SignalR broadcast queuing and memory leaks
        private const int UpdateIntervalMs = 5000;
        
        // MEMORY FIX: Minimum time between broadcasts to prevent SignalR queue buildup (1 second throttle)
        private readonly TimeSpan _broadcastThrottleInterval = TimeSpan.FromSeconds(1);

        public event EventHandler<RegionStatsUpdatedEventArgs>? RegionStatsUpdated;

        public RegionInfoService(IAccountService accountService, ILogger<RegionInfoService> logger)
        {
            _accountService = accountService;
            _logger = logger;
        }

        public Task<RegionStatsDto?> GetRegionStatsAsync(Guid accountId)
        {
            _regionStats.TryGetValue(accountId, out var stats);
            return Task.FromResult(stats);
        }

        public Task UpdateRegionStatsAsync(Guid accountId)
        {
            return Task.Run(() =>
            {
                // Prevent concurrent updates for the same account
                if (!_isUpdating.TryAdd(accountId, true))
                {
                    return;
                }

            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance?.IsConnected != true)
                {
                    _logger.LogDebug("Cannot update region stats for account {AccountId} - not connected", accountId);
                    return;
                }

                var client = instance.Client;
                var currentSim = client.Network.CurrentSim;
                
                if (currentSim == null)
                {
                    _logger.LogDebug("Cannot update region stats for account {AccountId} - no current sim", accountId);
                    return;
                }

                // Build stats similar to Radegast's UpdateSimDisplay
                var stats = new RegionStatsDto
                {
                    AccountId = accountId,
                    
                    // Basic region information
                    RegionName = currentSim.Name,
                    ProductName = currentSim.ProductName ?? "Unknown",
                    ProductSku = currentSim.ProductSku ?? "Unknown",
                    SimVersion = currentSim.SimVersion ?? "Unknown",
                    DataCenter = currentSim.ColoLocation ?? "Unknown",
                    CPUClass = (int)currentSim.CPUClass,
                    CPURatio = currentSim.CPURatio,
                    
                    // Get maturity level from access level
                    MaturityLevel = GetMaturityLevel(currentSim.Access),
                    
                    // Position information
                    RegionX = (float)(currentSim.Handle >> 32),
                    RegionY = (float)(currentSim.Handle & 0xFFFFFFFF),
                    MyPosition = client.Self.SimPosition,
                    
                    LastUpdated = DateTime.UtcNow
                };

                // Get simulation statistics
                var simStats = currentSim.Stats;
                try
                {
                    // Performance metrics
                    stats.TimeDilation = simStats.Dilation;
                    stats.FPS = simStats.FPS;
                    stats.PhysicsFPS = simStats.PhysicsFPS;
                    
                    // Agent and object counts
                    stats.MainAgents = simStats.Agents;
                    stats.ChildAgents = simStats.ChildAgents;
                    stats.Objects = simStats.Objects;
                    stats.ActiveObjects = simStats.ScriptedObjects;
                    stats.ActiveScripts = simStats.ActiveScripts;
                    
                    // Processing times (convert to milliseconds as needed)
                    stats.NetTime = simStats.NetTime;
                    stats.PhysicsTime = simStats.PhysicsTime;
                    stats.SimTime = simStats.OtherTime; // OtherTime is simulation time
                    stats.AgentTime = simStats.AgentTime;
                    stats.ImagesTime = simStats.ImageTime;
                    stats.ScriptTime = simStats.ScriptTime;
                    
                    // Calculate total frame time (similar to Radegast calculation)
                    float total = simStats.NetTime + simStats.PhysicsTime + simStats.OtherTime + 
                                  simStats.AgentTime + simStats.ImageTime + simStats.ScriptTime;
                    stats.TotalFrameTime = total;
                    
                    // Calculate spare time (similar to Radegast: max(0, 1000/45 - total))
                    // 45 FPS target = ~22.22ms per frame
                    stats.SpareTime = Math.Max(0f, (1000f / 45f) - total);
                    
                    // Network statistics
                    stats.PendingDownloads = simStats.PendingDownloads;
                    stats.PendingUploads = simStats.PendingUploads;
                    stats.PendingLocalUploads = simStats.PendingLocalUploads;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading simulation stats for account {AccountId}, using default values", accountId);
                }

                // Store the updated stats
                _regionStats.AddOrUpdate(accountId, stats, (key, oldStats) => stats);

                // MEMORY FIX: Throttle broadcasts to prevent SignalR message queue buildup
                var now = DateTime.UtcNow;
                var lastBroadcast = _lastBroadcastTime.GetValueOrDefault(accountId, DateTime.MinValue);
                var timeSinceLastBroadcast = now - lastBroadcast;
                
                if (timeSinceLastBroadcast >= _broadcastThrottleInterval)
                {
                    // Fire the updated event (throttled to prevent memory leaks)
                    RegionStatsUpdated?.Invoke(this, new RegionStatsUpdatedEventArgs
                    {
                        AccountId = accountId,
                        Stats = stats
                    });
                    
                    _lastBroadcastTime.AddOrUpdate(accountId, now, (key, old) => now);
                    
                    _logger.LogDebug("Broadcasted region stats for account {AccountId}: {RegionName} (Dilation: {TimeDilation:F3}, FPS: {FPS})", 
                        accountId, stats.RegionName, stats.TimeDilation, stats.FPS);
                }
                else
                {
                    _logger.LogTrace("Throttled region stats broadcast for account {AccountId} (last broadcast {Elapsed}ms ago)", 
                        accountId, timeSinceLastBroadcast.TotalMilliseconds);
                }


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating region stats for account {AccountId}", accountId);
            }
            finally
            {
                _isUpdating.TryRemove(accountId, out _);
            }
            });
        }

        public async Task StartPeriodicUpdatesAsync(Guid accountId)
        {
            try
            {
                var instance = _accountService.GetInstance(accountId);
                if (instance?.IsConnected != true)
                {
                    _logger.LogDebug("Cannot start periodic updates for account {AccountId} - not connected", accountId);
                    return;
                }

                // Stop any existing timer for this account
                await StopPeriodicUpdatesAsync(accountId);

                // Create new timer for periodic updates
                var timer = new Timer(async _ => await UpdateRegionStatsAsync(accountId),
                    null, TimeSpan.Zero, TimeSpan.FromMilliseconds(UpdateIntervalMs));

                _updateTimers.TryAdd(accountId, timer);

                _logger.LogInformation("Started periodic region stats updates for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting periodic updates for account {AccountId}", accountId);
            }
        }

        public async Task StopPeriodicUpdatesAsync(Guid accountId)
        {
            try
            {
                if (_updateTimers.TryRemove(accountId, out var timer))
                {
                    await timer.DisposeAsync();
                    _logger.LogInformation("Stopped periodic region stats updates for account {AccountId}", accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping periodic updates for account {AccountId}", accountId);
            }
        }

        public void CleanupAccount(Guid accountId)
        {
            try
            {
                // Stop periodic updates
                _ = Task.Run(async () => await StopPeriodicUpdatesAsync(accountId));

                // MEMORY FIX: Remove stored stats and broadcast tracking
                _regionStats.TryRemove(accountId, out _);
                _isUpdating.TryRemove(accountId, out _);
                _lastBroadcastTime.TryRemove(accountId, out _);

                _logger.LogInformation("Cleaned up region stats for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up region stats for account {AccountId}", accountId);
            }
        }

        private static string GetMaturityLevel(SimAccess access)
        {
            return access switch
            {
                SimAccess.Mature => "Mature",
                SimAccess.Adult => "Adult",
                SimAccess.PG => "General",
                SimAccess.Down => "Down",
                SimAccess.NonExistent => "Non-Existent",
                _ => "Unknown"
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                // Stop all timers
                var stopTasks = _updateTimers.Keys.Select(accountId => 
                    Task.Run(async () => await StopPeriodicUpdatesAsync(accountId)));
                
                Task.WaitAll(stopTasks.ToArray(), TimeSpan.FromSeconds(5));

                // MEMORY FIX: Clear all data including broadcast tracking
                _regionStats.Clear();
                _updateTimers.Clear();
                _isUpdating.Clear();
                _lastBroadcastTime.Clear();

                _logger.LogInformation("RegionInfoService disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing RegionInfoService");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}