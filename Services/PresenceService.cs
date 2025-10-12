using RadegastWeb.Core;
using RadegastWeb.Models;
using OpenMetaverse;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public enum PresenceStatus
    {
        Online,
        Away,
        Busy
    }

    public interface IPresenceService
    {
        Task SetAwayAsync(Guid accountId, bool away);
        Task SetBusyAsync(Guid accountId, bool busy);
        Task SetActiveAccountAsync(Guid? accountId);
        Task HandleBrowserCloseAsync();
        Task HandleBrowserReturnAsync();
        PresenceStatus GetAccountStatus(Guid accountId);
        event EventHandler<PresenceStatusChangedEventArgs> PresenceStatusChanged;
    }

    public class PresenceStatusChangedEventArgs : EventArgs
    {
        public Guid AccountId { get; set; }
        public PresenceStatus Status { get; set; }
        public string StatusText { get; set; } = string.Empty;
    }

    public class PresenceService : IPresenceService
    {
        private readonly IAccountService _accountService;
        private readonly ILogger<PresenceService> _logger;
        private readonly ConcurrentDictionary<Guid, PresenceStatus> _accountStatuses = new();
        private Guid? _activeAccountId;
        private bool _browserIsAway = false;

        public event EventHandler<PresenceStatusChangedEventArgs>? PresenceStatusChanged;

        public PresenceService(IAccountService accountService, ILogger<PresenceService> logger)
        {
            _accountService = accountService;
            _logger = logger;
        }

        public Task SetAwayAsync(Guid accountId, bool away)
        {
            var instance = _accountService.GetInstance(accountId);
            if (instance == null || !instance.IsConnected)
            {
                _logger.LogWarning("Cannot set away status for account {AccountId} - not connected", accountId);
                return Task.CompletedTask;
            }

            try
            {
                // Use the same logic as Radegast StateManager.SetAway
                var awayAnim = new Dictionary<UUID, bool> { { Animations.AWAY, away } };
                instance.Client.Self.Animate(awayAnim, true);
                instance.Client.Self.Movement.Away = away;

                var newStatus = away ? PresenceStatus.Away : PresenceStatus.Online;
                _accountStatuses.AddOrUpdate(accountId, newStatus, (key, oldValue) => newStatus);

                var statusText = away ? "Away" : "Online";
                _logger.LogInformation("Set account {AccountId} away status to {Away}", accountId, away);
                
                PresenceStatusChanged?.Invoke(this, new PresenceStatusChangedEventArgs
                {
                    AccountId = accountId,
                    Status = newStatus,
                    StatusText = statusText
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting away status for account {AccountId}", accountId);
            }
            
            return Task.CompletedTask;
        }

        public Task SetBusyAsync(Guid accountId, bool busy)
        {
            var instance = _accountService.GetInstance(accountId);
            if (instance == null || !instance.IsConnected)
            {
                _logger.LogWarning("Cannot set busy status for account {AccountId} - not connected", accountId);
                return Task.CompletedTask;
            }

            try
            {
                // Use the same logic as Radegast StateManager.SetBusy
                var busyAnim = new Dictionary<UUID, bool> { { Animations.BUSY, busy } };
                instance.Client.Self.Animate(busyAnim, true);

                var newStatus = busy ? PresenceStatus.Busy : PresenceStatus.Online;
                _accountStatuses.AddOrUpdate(accountId, newStatus, (key, oldValue) => newStatus);

                var statusText = busy ? "Busy" : "Online";
                _logger.LogInformation("Set account {AccountId} busy status to {Busy}", accountId, busy);
                
                PresenceStatusChanged?.Invoke(this, new PresenceStatusChangedEventArgs
                {
                    AccountId = accountId,
                    Status = newStatus,
                    StatusText = statusText
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting busy status for account {AccountId}", accountId);
            }
            
            return Task.CompletedTask;
        }

        public async Task SetActiveAccountAsync(Guid? accountId)
        {
            var previousActiveAccount = _activeAccountId;
            _activeAccountId = accountId;

            _logger.LogInformation("Active account changed from {PreviousAccountId} to {NewAccountId}", 
                previousActiveAccount, accountId);

            // Set previous active account to busy (if it exists and is different)
            if (previousActiveAccount.HasValue && 
                previousActiveAccount != accountId &&
                _accountService.GetInstance(previousActiveAccount.Value)?.IsConnected == true)
            {
                await SetBusyAsync(previousActiveAccount.Value, true);
            }

            // Set new active account back to online (if it exists)
            if (accountId.HasValue && 
                _accountService.GetInstance(accountId.Value)?.IsConnected == true)
            {
                // First clear any busy status
                await SetBusyAsync(accountId.Value, false);
                
                // If browser was away, don't override that with online
                if (!_browserIsAway)
                {
                    // Make sure we're not busy either (unless manually set)
                    var currentStatus = GetAccountStatus(accountId.Value);
                    if (currentStatus == PresenceStatus.Busy)
                    {
                        _accountStatuses.AddOrUpdate(accountId.Value, PresenceStatus.Online, (key, oldValue) => PresenceStatus.Online);
                        
                        PresenceStatusChanged?.Invoke(this, new PresenceStatusChangedEventArgs
                        {
                            AccountId = accountId.Value,
                            Status = PresenceStatus.Online,
                            StatusText = "Online"
                        });
                    }
                }
            }
        }

        public async Task HandleBrowserCloseAsync()
        {
            _browserIsAway = true;
            _logger.LogInformation("Browser closed - setting all connected accounts to away");

            var tasks = new List<Task>();
            
            foreach (var account in await _accountService.GetAccountsAsync())
            {
                var instance = _accountService.GetInstance(account.Id);
                if (instance?.IsConnected == true)
                {
                    // Set away and ensure busy is disabled for browser close
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // First disable busy if it was set
                            await SetBusyAsync(account.Id, false);
                            // Then set away
                            await SetAwayAsync(account.Id, true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error setting account {AccountId} to away on browser close", account.Id);
                        }
                    }));
                }
            }

            await Task.WhenAll(tasks);
        }

        public async Task HandleBrowserReturnAsync()
        {
            _browserIsAway = false;
            _logger.LogInformation("Browser returned - updating account statuses");

            var tasks = new List<Task>();
            
            foreach (var account in await _accountService.GetAccountsAsync())
            {
                var instance = _accountService.GetInstance(account.Id);
                if (instance?.IsConnected == true)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // Clear away status
                            await SetAwayAsync(account.Id, false);
                            
                            // If this is not the active account, set it to busy
                            if (_activeAccountId != account.Id)
                            {
                                await SetBusyAsync(account.Id, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error updating account {AccountId} status on browser return", account.Id);
                        }
                    }));
                }
            }

            await Task.WhenAll(tasks);
        }

        public PresenceStatus GetAccountStatus(Guid accountId)
        {
            return _accountStatuses.TryGetValue(accountId, out var status) ? status : PresenceStatus.Online;
        }
    }
}