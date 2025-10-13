using System.Collections.Concurrent;
using OpenMetaverse;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Background service that periodically cycles through nearby avatars
    /// to proactively fetch display names and populate the global cache.
    /// Runs every 5 minutes and processes up to 20 avatars at a time,
    /// avoiding duplicate requests for avatars we already have cached.
    /// </summary>
    public interface IPeriodicDisplayNameService
    {
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
        void RegisterAccount(Guid accountId);
        void UnregisterAccount(Guid accountId);
    }

    public class PeriodicDisplayNameService : BackgroundService, IPeriodicDisplayNameService
    {
        private readonly ILogger<PeriodicDisplayNameService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IGlobalDisplayNameCache _globalDisplayNameCache;
        
        // Track which accounts are active for processing
        private readonly ConcurrentDictionary<Guid, bool> _activeAccounts = new();
        
        // Processing interval (5 minutes)
        private readonly TimeSpan _processingInterval = TimeSpan.FromMinutes(5);
        
        // Maximum number of avatars to process per cycle
        private const int MaxAvatarsPerCycle = 20;
        
        // Minimum time between checking the same avatar (to avoid spam)
        private readonly TimeSpan _minimumCacheDuration = TimeSpan.FromHours(1);
        
        private Timer? _processingTimer;
        private readonly SemaphoreSlim _processingSemaphore = new(1, 1);
        private volatile bool _isDisposing = false;

        public PeriodicDisplayNameService(
            ILogger<PeriodicDisplayNameService> logger,
            IServiceProvider serviceProvider,
            IGlobalDisplayNameCache globalDisplayNameCache)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _globalDisplayNameCache = globalDisplayNameCache;
        }

        public void RegisterAccount(Guid accountId)
        {
            _activeAccounts.TryAdd(accountId, true);
            _logger.LogDebug("Registered account {AccountId} for periodic display name processing", accountId);
        }

        public void UnregisterAccount(Guid accountId)
        {
            _activeAccounts.TryRemove(accountId, out _);
            _logger.LogDebug("Unregistered account {AccountId} from periodic display name processing", accountId);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Periodic Display Name Service started");

            // Start the periodic timer
            _processingTimer = new Timer(ProcessDisplayNames, null, _processingInterval, _processingInterval);

            try
            {
                // Keep the service running until cancellation is requested
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                _logger.LogInformation("Periodic Display Name Service is stopping due to cancellation");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in Periodic Display Name Service");
                throw;
            }
        }

        private async void ProcessDisplayNames(object? state)
        {
            if (_isDisposing || !await _processingSemaphore.WaitAsync(1000))
            {
                return;
            }

            try
            {
                var startTime = DateTime.UtcNow;
                _logger.LogDebug("Starting periodic display name processing cycle");

                var totalProcessed = 0;
                var totalRequested = 0;

                // Process each active account
                foreach (var accountId in _activeAccounts.Keys.ToList())
                {
                    if (_isDisposing)
                        break;

                    try
                    {
                        var (processed, requested) = await ProcessAccountDisplayNames(accountId);
                        totalProcessed += processed;
                        totalRequested += requested;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing display names for account {AccountId}", accountId);
                    }
                }

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "Completed periodic display name processing cycle: {TotalProcessed} avatars processed, {TotalRequested} names requested in {Duration:F2}s",
                    totalProcessed, totalRequested, duration.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic display name processing");
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }

        private async Task<(int processed, int requested)> ProcessAccountDisplayNames(Guid accountId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                
                var instance = accountService.GetInstance(accountId);
                if (instance?.IsConnected != true)
                {
                    _logger.LogDebug("Account {AccountId} is not connected, skipping display name processing", accountId);
                    return (0, 0);
                }

                // Get nearby avatar data directly without triggering display name lookups (avoids circular calls)
                var nearbyAvatarData = await instance.GetNearbyAvatarDataAsync();
                var avatarList = nearbyAvatarData.ToList();
                
                if (avatarList.Count == 0)
                {
                    _logger.LogDebug("No nearby avatars found for account {AccountId}", accountId);
                    return (0, 0);
                }

                _logger.LogDebug("Found {Count} nearby avatars for account {AccountId}", avatarList.Count, accountId);

                // Filter avatars that need display name updates
                var avatarsNeedingUpdate = new List<string>();
                var cutoffTime = DateTime.UtcNow - _minimumCacheDuration;

                foreach (var (avatarId, avatarName, position) in avatarList.Take(MaxAvatarsPerCycle))
                {
                    try
                    {
                        var cachedDisplayName = await _globalDisplayNameCache.GetCachedDisplayNameAsync(avatarId);
                        
                        // Check if we need to update this avatar's display name
                        bool needsUpdate = cachedDisplayName == null ||
                                         cachedDisplayName.LastUpdated < cutoffTime ||
                                         DateTime.UtcNow > cachedDisplayName.NextUpdate ||
                                         string.IsNullOrEmpty(cachedDisplayName.DisplayNameValue) ||
                                         cachedDisplayName.DisplayNameValue == "Loading..." ||
                                         cachedDisplayName.DisplayNameValue == "???";

                        if (needsUpdate)
                        {
                            avatarsNeedingUpdate.Add(avatarId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error checking cache for avatar {AvatarId}", avatarId);
                        // If we can't check the cache, assume it needs updating
                        avatarsNeedingUpdate.Add(avatarId);
                    }
                }

                if (avatarsNeedingUpdate.Count == 0)
                {
                    _logger.LogDebug("All nearby avatars for account {AccountId} have recent display names", accountId);
                    return (avatarList.Count, 0);
                }

                _logger.LogDebug("Requesting display names for {Count} avatars on account {AccountId}", 
                    avatarsNeedingUpdate.Count, accountId);

                // Request display names through the global cache
                var success = await _globalDisplayNameCache.RequestDisplayNamesAsync(avatarsNeedingUpdate, accountId);
                
                if (success)
                {
                    _logger.LogDebug("Successfully requested display names for {Count} avatars on account {AccountId}", 
                        avatarsNeedingUpdate.Count, accountId);
                }
                else
                {
                    _logger.LogWarning("Failed to request display names for account {AccountId}", accountId);
                }

                return (avatarList.Count, avatarsNeedingUpdate.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing display names for account {AccountId}", accountId);
                return (0, 0);
            }
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Periodic Display Name Service");
            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Periodic Display Name Service");
            
            _isDisposing = true;
            
            // Stop the timer
            if (_processingTimer != null)
            {
                await _processingTimer.DisposeAsync();
                _processingTimer = null;
            }

            // Wait for any ongoing processing to complete
            await _processingSemaphore.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            
            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            _isDisposing = true;
            
            try
            {
                _processingTimer?.Dispose();
                _processingSemaphore?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing Periodic Display Name Service");
            }
            
            base.Dispose();
        }
    }
}