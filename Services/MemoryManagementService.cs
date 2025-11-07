using System.Diagnostics;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service to monitor memory usage and perform aggressive cleanup when needed
    /// </summary>
    public interface IMemoryManagementService
    {
        void ForceGarbageCollection();
        long GetCurrentMemoryUsageMB();
        void TriggerMemoryCleanupIfNeeded();
        void RegisterPeriodicCleanup();
    }

    public class MemoryManagementService : IMemoryManagementService, IDisposable
    {
        private readonly ILogger<MemoryManagementService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly Timer _memoryMonitorTimer;
        private readonly object _cleanupLock = new();
        private DateTime _lastForceCleanup = DateTime.MinValue;
        private const long MEMORY_THRESHOLD_MB = 1500; // Force cleanup at 1.5GB
        private const long MEMORY_WARNING_MB = 1200; // Warn at 1.2GB
        private volatile bool _disposed = false;

        public MemoryManagementService(ILogger<MemoryManagementService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            
            // Monitor memory every 2 minutes
            _memoryMonitorTimer = new Timer(MonitorMemoryUsage, null, 
                TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }

        public long GetCurrentMemoryUsageMB()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.WorkingSet64 / (1024 * 1024);
            }
            catch
            {
                return GC.GetTotalMemory(false) / (1024 * 1024);
            }
        }

        public void ForceGarbageCollection()
        {
            try
            {
                _logger.LogInformation("Forcing garbage collection...");
                
                var memoryBefore = GetCurrentMemoryUsageMB();
                
                // Aggressive garbage collection
                GC.Collect(2, GCCollectionMode.Aggressive, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, true);
                
                var memoryAfter = GetCurrentMemoryUsageMB();
                var saved = memoryBefore - memoryAfter;
                
                _logger.LogInformation("Garbage collection completed: {BeforeMB}MB -> {AfterMB}MB (saved {SavedMB}MB)",
                    memoryBefore, memoryAfter, saved);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during forced garbage collection");
            }
        }

        public void TriggerMemoryCleanupIfNeeded()
        {
            var currentMemoryMB = GetCurrentMemoryUsageMB();
            
            if (currentMemoryMB > MEMORY_THRESHOLD_MB)
            {
                lock (_cleanupLock)
                {
                    // Prevent too frequent cleanup (minimum 10 minutes between forced cleanups)
                    if (DateTime.UtcNow - _lastForceCleanup < TimeSpan.FromMinutes(10))
                    {
                        _logger.LogDebug("Skipping memory cleanup - too recent (last: {LastCleanup})", _lastForceCleanup);
                        return;
                    }
                    
                    _lastForceCleanup = DateTime.UtcNow;
                    
                    _logger.LogWarning("Memory usage ({CurrentMB}MB) exceeds threshold ({ThresholdMB}MB) - performing aggressive cleanup",
                        currentMemoryMB, MEMORY_THRESHOLD_MB);
                    
                    PerformAggressiveCleanup();
                }
            }
            else if (currentMemoryMB > MEMORY_WARNING_MB)
            {
                _logger.LogInformation("Memory usage warning: {CurrentMB}MB (threshold: {ThresholdMB}MB)",
                    currentMemoryMB, MEMORY_THRESHOLD_MB);
            }
        }

        public void RegisterPeriodicCleanup()
        {
            // Called by hosting service to enable periodic monitoring
            _logger.LogInformation("Memory management service started - monitoring every 2 minutes");
        }

        private void MonitorMemoryUsage(object? state)
        {
            if (_disposed) return;
            
            try
            {
                TriggerMemoryCleanupIfNeeded();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in memory monitoring timer");
            }
        }

        private void PerformAggressiveCleanup()
        {
            try
            {
                var memoryBefore = GetCurrentMemoryUsageMB();
                
                // Clean up all major services
                using var scope = _serviceProvider.CreateScope();
                
                // Clean display name caches
                var globalCache = scope.ServiceProvider.GetService<IGlobalDisplayNameCache>();
                globalCache?.CleanExpiredCache();
                
                // Clean connection tracking
                var connectionService = scope.ServiceProvider.GetService<IConnectionTrackingService>();
                connectionService?.PerformDeepCleanup();
                
                // Clean account instances
                var accountService = scope.ServiceProvider.GetService<IAccountService>();
                if (accountService != null)
                {
                    var accounts = accountService.GetAccountsAsync().Result;
                    foreach (var account in accounts)
                    {
                        var instance = accountService.GetInstance(account.Id);
                        instance?.PerformLibOpenMetaverseCleanup();
                    }
                }
                
                // Force garbage collection after cleanup
                ForceGarbageCollection();
                
                var memoryAfter = GetCurrentMemoryUsageMB();
                var saved = memoryBefore - memoryAfter;
                
                _logger.LogInformation("Aggressive memory cleanup completed: {BeforeMB}MB -> {AfterMB}MB (saved {SavedMB}MB)",
                    memoryBefore, memoryAfter, saved);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during aggressive memory cleanup");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _memoryMonitorTimer?.Dispose();
            _logger.LogInformation("Memory management service disposed");
        }
    }
}