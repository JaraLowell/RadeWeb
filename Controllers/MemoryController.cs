using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Services;
using System.Diagnostics;
using System.Reflection;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MemoryController : ControllerBase
    {
        private readonly ILogger<MemoryController> _logger;
        private readonly IAccountService _accountService;
        private readonly IConnectionTrackingService _connectionTrackingService;

        public MemoryController(
            ILogger<MemoryController> logger, 
            IAccountService accountService,
            IConnectionTrackingService connectionTrackingService)
        {
            _logger = logger;
            _accountService = accountService;
            _connectionTrackingService = connectionTrackingService;
        }

        /// <summary>
        /// Get comprehensive memory usage information and collection sizes
        /// </summary>
        [HttpGet("stats")]
        public async Task<ActionResult<object>> GetMemoryStats()
        {
            try
            {
                // Force garbage collection to get accurate memory readings
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true);

                var process = Process.GetCurrentProcess();
                var accounts = await _accountService.GetAccountsAsync();
                var accountsList = accounts.ToList();

                var result = new
                {
                    // Memory usage
                    MemoryUsage = new
                    {
                        TotalMemoryMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0),
                        WorkingSetMB = process.WorkingSet64 / (1024.0 * 1024.0),
                        PrivateMemoryMB = process.PrivateMemorySize64 / (1024.0 * 1024.0),
                        GC0Collections = GC.CollectionCount(0),
                        GC1Collections = GC.CollectionCount(1),
                        GC2Collections = GC.CollectionCount(2)
                    },

                    // Account information
                    Accounts = new
                    {
                        TotalCount = accountsList.Count,
                        ConnectedCount = accountsList.Count(a => a.IsConnected),
                        ActiveConnections = GetConnectionStats()
                    },

                    // Collection sizes from various services
                    CollectionSizes = await GetCollectionSizesAsync(accountsList),

                    // System information
                    SystemInfo = new
                    {
                        ProcessorCount = Environment.ProcessorCount,
                        MachineName = Environment.MachineName,
                        OSVersion = Environment.OSVersion.ToString(),
                        Is64BitProcess = Environment.Is64BitProcess,
                        ProcessId = process.Id,
                        StartTime = process.StartTime,
                        UptimeHours = (DateTime.Now - process.StartTime).TotalHours
                    }
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting memory stats");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Force garbage collection and return memory before/after
        /// </summary>
        [HttpPost("gc")]
        public ActionResult<object> ForceGarbageCollection()
        {
            try
            {
                var memoryBefore = GC.GetTotalMemory(false);
                
                GC.Collect(2, GCCollectionMode.Aggressive, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, true);
                
                var memoryAfter = GC.GetTotalMemory(true);

                return Ok(new
                {
                    MemoryBeforeMB = memoryBefore / (1024.0 * 1024.0),
                    MemoryAfterMB = memoryAfter / (1024.0 * 1024.0),
                    MemoryFreedMB = (memoryBefore - memoryAfter) / (1024.0 * 1024.0),
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error forcing garbage collection");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Perform LibOpenMetaverse internal collection cleanup on all connected accounts
        /// This helps prevent memory leaks in the underlying OpenMetaverse library
        /// </summary>
        [HttpPost("cleanup-libopenmv")]
        public async Task<ActionResult<object>> CleanupLibOpenMetaverse()
        {
            try
            {
                var accounts = await _accountService.GetAccountsAsync();
                var cleanedAccounts = new List<object>();
                var memoryBefore = GC.GetTotalMemory(false);

                foreach (var account in accounts)
                {
                    var instance = _accountService.GetInstance(account.Id);
                    if (instance?.IsConnected == true)
                    {
                        instance.PerformLibOpenMetaverseCleanup();
                        cleanedAccounts.Add(new
                        {
                            AccountId = account.Id,
                            FirstName = account.FirstName,
                            LastName = account.LastName,
                            CurrentRegion = instance.AccountInfo?.CurrentRegion ?? "Unknown"
                        });
                    }
                }

                // Force GC after cleanup to see memory impact
                GC.Collect(1, GCCollectionMode.Optimized);
                var memoryAfter = GC.GetTotalMemory(false);
                var memoryFreed = memoryBefore - memoryAfter;

                var result = new
                {
                    CleanedAccountsCount = cleanedAccounts.Count,
                    CleanedAccounts = cleanedAccounts,
                    MemoryBeforeMB = memoryBefore / (1024.0 * 1024.0),
                    MemoryAfterMB = memoryAfter / (1024.0 * 1024.0),
                    MemoryFreedMB = memoryFreed / (1024.0 * 1024.0),
                    Timestamp = DateTime.UtcNow
                };

                _logger.LogInformation("LibOpenMetaverse cleanup completed on {CleanedCount} accounts, freed {FreedMemoryMB:F1}MB", 
                    cleanedAccounts.Count, memoryFreed / (1024.0 * 1024.0));

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during LibOpenMetaverse cleanup");
                return StatusCode(500, $"Error during cleanup: {ex.Message}");
            }
        }

        private object GetConnectionStats()
        {
            try
            {
                // Use reflection to get connection counts without exposing internal details
                var type = _connectionTrackingService.GetType();
                var accountConnectionsField = type.GetField("_accountConnections", BindingFlags.NonPublic | BindingFlags.Instance);
                var connectionAccountsField = type.GetField("_connectionAccounts", BindingFlags.NonPublic | BindingFlags.Instance);

                int totalAccountMappings = 0;
                int totalConnectionMappings = 0;

                if (accountConnectionsField?.GetValue(_connectionTrackingService) is System.Collections.IDictionary accountConnections)
                {
                    totalAccountMappings = accountConnections.Count;
                }

                if (connectionAccountsField?.GetValue(_connectionTrackingService) is System.Collections.IDictionary connectionAccounts)
                {
                    totalConnectionMappings = connectionAccounts.Count;
                }

                return new
                {
                    AccountMappings = totalAccountMappings,
                    ConnectionMappings = totalConnectionMappings
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get connection stats via reflection");
                return new { Error = "Could not retrieve connection stats" };
            }
        }

        private Task<object> GetCollectionSizesAsync(IEnumerable<Models.Account> accounts)
        {
            var sizes = new Dictionary<string, object>();

            try
            {
                // Get collection sizes from WebRadegastInstance for each account
                foreach (var account in accounts)
                {
                    var instance = _accountService.GetInstance(account.Id);
                    if (instance != null)
                    {
                        sizes[$"Account_{account.Id}_Collections"] = GetInstanceCollectionSizes(instance);
                    }
                }

                return Task.FromResult<object>(sizes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get all collection sizes");
                return Task.FromResult<object>(new { Error = "Could not retrieve all collection sizes" });
            }
        }

        private object GetInstanceCollectionSizes(Core.WebRadegastInstance instance)
        {
            try
            {
                var type = instance.GetType();
                var sizes = new Dictionary<string, int>();

                // Use reflection to get sizes of various collections
                var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                
                foreach (var field in fields)
                {
                    if (field.FieldType.IsGenericType)
                    {
                        var value = field.GetValue(instance);
                        if (value != null)
                        {
                            // Check for common collection types
                            if (value is System.Collections.IDictionary dict)
                            {
                                sizes[field.Name] = dict.Count;
                            }
                            else if (value is System.Collections.ICollection collection)
                            {
                                sizes[field.Name] = collection.Count;
                            }
                        }
                    }
                }

                return sizes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get instance collection sizes for account {AccountId}", instance.AccountId);
                return new { Error = $"Could not retrieve collection sizes for account {instance.AccountId}" };
            }
        }
    }
}