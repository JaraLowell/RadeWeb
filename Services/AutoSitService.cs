using System.Text.Json;
using Microsoft.Extensions.Logging;
using RadegastWeb.Models;
using RadegastWeb.Core;
using OpenMetaverse;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service interface for auto-sit functionality
    /// </summary>
    public interface IAutoSitService
    {
        /// <summary>
        /// Gets the auto-sit configuration for an account
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <returns>Auto-sit configuration or null if not found</returns>
        Task<AutoSitConfig?> GetAutoSitConfigAsync(Guid accountId);

        /// <summary>
        /// Saves the auto-sit configuration for an account
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="config">Auto-sit configuration</param>
        Task SaveAutoSitConfigAsync(Guid accountId, AutoSitConfig config);

        /// <summary>
        /// Updates the last sit target for an account (called when user sits on an object)
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="targetUuid">UUID of the object sat on</param>
        Task UpdateLastSitTargetAsync(Guid accountId, string targetUuid);

        /// <summary>
        /// Schedules auto-sit execution after login
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="instance">WebRadegastInstance</param>
        Task ScheduleAutoSitAsync(Guid accountId, WebRadegastInstance instance);

        /// <summary>
        /// Cancels any pending auto-sit for an account
        /// </summary>
        /// <param name="accountId">Account ID</param>
        void CancelAutoSit(Guid accountId);

        /// <summary>
        /// Enables or disables auto-sit for an account
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="enabled">Whether to enable auto-sit</param>
        Task SetAutoSitEnabledAsync(Guid accountId, bool enabled);
    }

    /// <summary>
    /// Service for managing auto-sit functionality
    /// </summary>
    public class AutoSitService : IAutoSitService, IDisposable
    {
        private readonly ILogger<AutoSitService> _logger;
        private readonly Dictionary<Guid, Timer> _autoSitTimers = new();
        private readonly object _lock = new();
        private bool _disposed;

        public AutoSitService(ILogger<AutoSitService> logger)
        {
            _logger = logger;
        }

        public async Task<AutoSitConfig?> GetAutoSitConfigAsync(Guid accountId)
        {
            try
            {
                var configPath = GetConfigPath(accountId);
                
                if (!File.Exists(configPath))
                    return null;

                var json = await File.ReadAllTextAsync(configPath);
                var config = JsonSerializer.Deserialize<AutoSitConfig>(json);
                
                _logger.LogDebug("Loaded auto-sit config for account {AccountId}: {Config}", accountId, json);
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading auto-sit config for account {AccountId}", accountId);
                return null;
            }
        }

        public async Task SaveAutoSitConfigAsync(Guid accountId, AutoSitConfig config)
        {
            try
            {
                var configPath = GetConfigPath(accountId);
                var directory = Path.GetDirectoryName(configPath);
                
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                config.LastUpdated = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                
                await File.WriteAllTextAsync(configPath, json);
                _logger.LogInformation("Saved auto-sit config for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving auto-sit config for account {AccountId}", accountId);
                throw;
            }
        }

        public async Task UpdateLastSitTargetAsync(Guid accountId, string targetUuid)
        {
            try
            {
                var config = await GetAutoSitConfigAsync(accountId) ?? new AutoSitConfig();
                config.TargetUuid = targetUuid;
                await SaveAutoSitConfigAsync(accountId, config);
                
                _logger.LogInformation("Updated last sit target for account {AccountId} to {TargetUuid}", accountId, targetUuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating last sit target for account {AccountId}", accountId);
            }
        }

        public async Task ScheduleAutoSitAsync(Guid accountId, WebRadegastInstance instance)
        {
            try
            {
                var config = await GetAutoSitConfigAsync(accountId);
                
                if (config == null || !config.Enabled || string.IsNullOrEmpty(config.TargetUuid))
                {
                    _logger.LogDebug("Auto-sit not scheduled for account {AccountId}: config disabled or no target", accountId);
                    return;
                }

                if (!UUID.TryParse(config.TargetUuid, out var targetUuid))
                {
                    _logger.LogWarning("Invalid UUID in auto-sit config for account {AccountId}: {TargetUuid}", accountId, config.TargetUuid);
                    return;
                }

                lock (_lock)
                {
                    // Cancel any existing timer
                    if (_autoSitTimers.ContainsKey(accountId))
                    {
                        _autoSitTimers[accountId].Dispose();
                        _autoSitTimers.Remove(accountId);
                    }

                    // Create new timer
                    var timer = new Timer(async _ => await ExecuteAutoSitAsync(accountId, instance, targetUuid, config), 
                                        null, 
                                        TimeSpan.FromSeconds(config.DelaySeconds), 
                                        Timeout.InfiniteTimeSpan);

                    _autoSitTimers[accountId] = timer;
                }

                _logger.LogInformation("Scheduled auto-sit for account {AccountId} on object {TargetUuid} in {DelaySeconds} seconds", 
                                     accountId, config.TargetUuid, config.DelaySeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling auto-sit for account {AccountId}", accountId);
            }
        }

        public void CancelAutoSit(Guid accountId)
        {
            lock (_lock)
            {
                if (_autoSitTimers.TryGetValue(accountId, out var timer))
                {
                    timer.Dispose();
                    _autoSitTimers.Remove(accountId);
                    _logger.LogInformation("Cancelled auto-sit for account {AccountId}", accountId);
                }
            }
        }

        public async Task SetAutoSitEnabledAsync(Guid accountId, bool enabled)
        {
            var config = await GetAutoSitConfigAsync(accountId) ?? new AutoSitConfig();
            config.Enabled = enabled;
            await SaveAutoSitConfigAsync(accountId, config);
            
            if (!enabled)
            {
                CancelAutoSit(accountId);
            }

            _logger.LogInformation("Set auto-sit enabled={Enabled} for account {AccountId}", enabled, accountId);
        }

        private async Task ExecuteAutoSitAsync(Guid accountId, WebRadegastInstance instance, UUID targetUuid, AutoSitConfig config)
        {
            int retryCount = 0;
            bool success = false;

            while (retryCount <= config.MaxRetries && !success && !_disposed)
            {
                try
                {
                    if (!instance.IsConnected)
                    {
                        _logger.LogWarning("Cannot execute auto-sit for account {AccountId}: not connected", accountId);
                        break;
                    }

                    // Check if object exists in region
                    if (!instance.IsObjectInRegion(targetUuid))
                    {
                        _logger.LogWarning("Auto-sit failed for account {AccountId}: object {TargetUuid} not found in region (attempt {AttemptNumber}/{MaxAttempts})", 
                                         accountId, targetUuid, retryCount + 1, config.MaxRetries + 1);
                        
                        if (retryCount < config.MaxRetries)
                        {
                            retryCount++;
                            await Task.Delay(TimeSpan.FromSeconds(config.RetryDelaySeconds));
                            continue;
                        }
                        break;
                    }

                    // Attempt to sit
                    success = instance.SetSitting(true, targetUuid);
                    
                    if (success)
                    {
                        _logger.LogInformation("Auto-sit successful for account {AccountId} on object {TargetUuid}", accountId, targetUuid);
                    }
                    else
                    {
                        _logger.LogWarning("Auto-sit failed for account {AccountId}: SetSitting returned false (attempt {AttemptNumber}/{MaxAttempts})", 
                                         accountId, retryCount + 1, config.MaxRetries + 1);
                        
                        if (retryCount < config.MaxRetries)
                        {
                            retryCount++;
                            await Task.Delay(TimeSpan.FromSeconds(config.RetryDelaySeconds));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing auto-sit for account {AccountId} (attempt {AttemptNumber}/{MaxAttempts})", 
                                   accountId, retryCount + 1, config.MaxRetries + 1);
                    
                    if (retryCount < config.MaxRetries)
                    {
                        retryCount++;
                        await Task.Delay(TimeSpan.FromSeconds(config.RetryDelaySeconds));
                    }
                }
            }

            // Clean up timer
            lock (_lock)
            {
                if (_autoSitTimers.TryGetValue(accountId, out var timer))
                {
                    timer.Dispose();
                    _autoSitTimers.Remove(accountId);
                }
            }
        }

        private static string GetConfigPath(Guid accountId)
        {
            return Path.Combine("data", "accounts", accountId.ToString(), "cache", "autosit.json");
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            lock (_lock)
            {
                foreach (var timer in _autoSitTimers.Values)
                {
                    timer.Dispose();
                }
                _autoSitTimers.Clear();
            }
            
            _disposed = true;
        }
    }
}