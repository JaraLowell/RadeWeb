using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly IHealthCheckService _healthCheckService;
        private readonly IAccountService _accountService;
        private readonly ILogger<HealthController> _logger;

        public HealthController(
            IHealthCheckService healthCheckService,
            IAccountService accountService,
            ILogger<HealthController> logger)
        {
            _healthCheckService = healthCheckService;
            _accountService = accountService;
            _logger = logger;
        }

        /// <summary>
        /// Get overall health status of all services
        /// </summary>
        [HttpGet("status")]
        public async Task<IActionResult> GetHealthStatus()
        {
            try
            {
                var healthStatus = await _healthCheckService.GetHealthStatusAsync();
                return Ok(healthStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting health status");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Run diagnostics for a specific account
        /// </summary>
        [HttpPost("diagnose/{accountId}")]
        public async Task<IActionResult> RunDiagnostics(Guid accountId)
        {
            try
            {
                await _healthCheckService.RunDiagnosticsAsync(accountId);
                return Ok(new { message = $"Diagnostics completed for account {accountId}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running diagnostics for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Run diagnostics for all connected accounts
        /// </summary>
        [HttpPost("diagnose-all")]
        public async Task<IActionResult> RunAllDiagnostics()
        {
            try
            {
                var accounts = await _accountService.GetAccountsAsync();
                var connectedAccounts = accounts.Where(a => 
                {
                    var instance = _accountService.GetInstance(a.Id);
                    return instance?.IsConnected == true;
                }).ToList();

                foreach (var account in connectedAccounts)
                {
                    await _healthCheckService.RunDiagnosticsAsync(account.Id);
                }

                return Ok(new 
                { 
                    message = $"Diagnostics completed for {connectedAccounts.Count} connected accounts",
                    accountCount = connectedAccounts.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running diagnostics for all accounts");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get basic server statistics
        /// </summary>
        [HttpGet("server-stats")]
        public async Task<IActionResult> GetServerStats()
        {
            try
            {
                var accounts = await _accountService.GetAccountsAsync();
                var connectedCount = 0;
                var totalAvatarCount = 0;

                foreach (var account in accounts)
                {
                    var instance = _accountService.GetInstance(account.Id);
                    if (instance?.IsConnected == true)
                    {
                        connectedCount++;
                        var avatars = await instance.GetNearbyAvatarsAsync();
                        totalAvatarCount += avatars.Count();
                    }
                }

                var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime;

                return Ok(new
                {
                    serverUptime = uptime,
                    totalAccounts = accounts.Count(),
                    connectedAccounts = connectedCount,
                    totalAvatarsDetected = totalAvatarCount,
                    extendedRuntime = uptime.TotalHours > 1
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting server stats");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}