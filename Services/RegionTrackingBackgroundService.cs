namespace RadegastWeb.Services
{
    /// <summary>
    /// Background service that periodically checks tracked regions
    /// </summary>
    public class RegionTrackingBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<RegionTrackingBackgroundService> _logger;
        private DateTime _lastCleanup = DateTime.MinValue;

        public RegionTrackingBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<RegionTrackingBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Region Tracking Background Service is starting");

            // Wait a bit before starting to let the application fully initialize
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DoWorkAsync(stoppingToken);
                    
                    // Get the polling interval from config
                    using var scope = _serviceProvider.CreateScope();
                    var trackingService = scope.ServiceProvider.GetRequiredService<IRegionTrackingService>();
                    var config = await trackingService.GetConfigAsync();
                    
                    var interval = config?.PollingIntervalMinutes ?? 5;
                    
                    _logger.LogInformation(
                        "Region tracking check completed. Next check in {Minutes} minutes",
                        interval);
                    
                    await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Expected when stopping
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in region tracking background service");
                    
                    // Wait a bit before retrying on error
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }

            _logger.LogInformation("Region Tracking Background Service is stopping");
        }

        private async Task DoWorkAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var trackingService = scope.ServiceProvider.GetRequiredService<IRegionTrackingService>();
                
                // Check if tracking is enabled BEFORE doing any work (memory preservation)
                var config = await trackingService.GetConfigAsync();
                if (config == null || !config.Enabled)
                {
                    _logger.LogDebug("Region tracking is disabled - skipping check to preserve memory");
                    return;
                }
                
                // Perform the region check
                await trackingService.CheckRegionsAsync();
                
                // Cleanup old records once per day (to prevent database bloat)
                if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromDays(1))
                {
                    _logger.LogInformation("Running daily cleanup of old region tracking records");
                    var deletedCount = await trackingService.CleanupOldRecordsAsync(keepDays: 32);
                    _lastCleanup = DateTime.UtcNow;
                    
                    if (deletedCount > 0)
                    {
                        _logger.LogInformation("Cleanup removed {Count} old records", deletedCount);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking regions");
            }
        }

        public override void Dispose()
        {
            base.Dispose();
        }
    }
}
