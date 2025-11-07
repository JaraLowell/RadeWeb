using RadegastWeb.Services;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Hosted service to initialize the stats name cache on application startup
    /// </summary>
    public class StatsNameCacheInitializationService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<StatsNameCacheInitializationService> _logger;

        public StatsNameCacheInitializationService(IServiceProvider serviceProvider, ILogger<StatsNameCacheInitializationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Initializing stats name cache on startup...");
                
                using var scope = _serviceProvider.CreateScope();
                var statsNameCache = scope.ServiceProvider.GetRequiredService<IStatsNameCache>();
                
                // Populate missing names from global cache
                await statsNameCache.PopulateFromGlobalCacheAsync();
                
                _logger.LogInformation("Stats name cache initialization completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stats name cache initialization");
                // Don't fail startup if this fails - it's an optimization, not critical
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // No cleanup needed
            return Task.CompletedTask;
        }
    }
}