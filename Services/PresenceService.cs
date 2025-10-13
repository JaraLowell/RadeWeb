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
                // Set away animation - this is how SL clients properly indicate away status
                var awayAnimations = new Dictionary<UUID, bool>();
                
                // Use the standard away animation UUID (from Second Life client)
                var awayAnimUUID = new UUID("fd037134-85d4-f241-72c6-4f42164fedee"); // Standard away animation
                awayAnimations.Add(awayAnimUUID, away);
                
                // Send the animation change
                instance.Client.Self.Animate(awayAnimations, true);
                
                // Also set the Movement.Away flag for compatibility
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
                // Set busy animation - this is how SL clients properly indicate busy status
                var busyAnimations = new Dictionary<UUID, bool>();
                
                // Use the standard busy animation UUID (from Second Life client)
                var busyAnimUUID = new UUID("efcf670c-2d18-8128-973a-034ebc806b67"); // Standard busy animation
                busyAnimations.Add(busyAnimUUID, busy);
                
                // Send the animation change
                instance.Client.Self.Animate(busyAnimations, true);

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

        public Task SetActiveAccountAsync(Guid? accountId)
        {
            var previousActiveAccount = _activeAccountId;
            _activeAccountId = accountId;

            _logger.LogInformation("Active account changed from {PreviousAccountId} to {NewAccountId}", 
                previousActiveAccount, accountId);

            // Note: Removed automatic busy/away status changes when switching accounts.
            // Users can now manually set busy/away status as desired.
            return Task.CompletedTask;
        }

        public Task HandleBrowserCloseAsync()
        {
            _logger.LogInformation("Browser closed - automatic away status disabled, users can set status manually");

            // Note: Removed automatic setting of accounts to away on browser close.
            // Users can manually set away/busy status as desired using the UI controls.
            return Task.CompletedTask;
        }

        public Task HandleBrowserReturnAsync()
        {
            _logger.LogInformation("Browser returned - automatic status changes disabled, users can set status manually");

            // Note: Removed automatic clearing of away status and setting busy on browser return.
            // Users can manually manage their away/busy status as desired using the UI controls.
            return Task.CompletedTask;
        }

        public PresenceStatus GetAccountStatus(Guid accountId)
        {
            return _accountStatuses.TryGetValue(accountId, out var status) ? status : PresenceStatus.Online;
        }
    }
}