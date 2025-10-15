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
        private readonly ConcurrentDictionary<Guid, bool> _busyStatuses = new(); // Track busy status separately
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
                // Primarily rely on our own tracking rather than trying to modify Movement.Away
                // which might be automatically managed by the movement system
                
                // Set away animation - this is how SL clients properly indicate away status
                var awayAnimations = new Dictionary<UUID, bool>();
                
                // Use the standard away animation UUID (from Second Life client)
                var awayAnimUUID = new UUID("fd037134-85d4-f241-72c6-4f42164fedee"); // Standard away animation
                awayAnimations.Add(awayAnimUUID, away);
                
                // Send the animation change
                instance.Client.Self.Animate(awayAnimations, true);
                
                // Try to set the Movement.Away flag, but don't rely on it persisting
                try
                {
                    instance.Client.Self.Movement.Away = away;
                    _logger.LogInformation("Account {AccountId}: Attempted to set Movement.Away to {Away}", accountId, away);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set Movement.Away for account {AccountId}", accountId);
                }

                // Clear busy status if setting away
                if (away)
                {
                    _busyStatuses.AddOrUpdate(accountId, false, (key, oldValue) => false);
                }

                // Update our own tracking - this is the authoritative source
                var newStatus = away ? PresenceStatus.Away : PresenceStatus.Online;
                _accountStatuses.AddOrUpdate(accountId, newStatus, (key, oldValue) => newStatus);

                var statusText = away ? "Away" : "Online";
                
                // Update the WebRadegastInstance status as well so AccountService reflects the change
                instance.UpdatePresenceStatus(statusText);
                
                _logger.LogInformation("Set account {AccountId} away status to {Away} (using internal tracking)", accountId, away);
                
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

                // Clear away status if setting busy
                if (busy)
                {
                    try
                    {
                        instance.Client.Self.Movement.Away = false;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clear Movement.Away for account {AccountId}", accountId);
                    }
                }

                // Track busy status separately since Movement doesn't have a Busy property
                _busyStatuses.AddOrUpdate(accountId, busy, (key, oldValue) => busy);

                // Update our authoritative tracking
                var newStatus = busy ? PresenceStatus.Busy : PresenceStatus.Online;
                _accountStatuses.AddOrUpdate(accountId, newStatus, (key, oldValue) => newStatus);

                var statusText = busy ? "Busy" : "Online";
                
                // Update the WebRadegastInstance status as well so AccountService reflects the change
                instance.UpdatePresenceStatus(statusText);
                
                _logger.LogInformation("Set account {AccountId} busy status to {Busy} (using internal tracking)", accountId, busy);
                
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
            _logger.LogDebug("Browser closed - automatic away status disabled, users can set status manually");

            // Note: Removed automatic setting of accounts to away on browser close.
            // Users can manually set away/busy status as desired using the UI controls.
            return Task.CompletedTask;
        }

        public Task HandleBrowserReturnAsync()
        {
            _logger.LogDebug("Browser returned - automatic status changes disabled, users can set status manually");

            // Note: Removed automatic clearing of away status and setting busy on browser return.
            // Users can manually manage their away/busy status as desired using the UI controls.
            return Task.CompletedTask;
        }

        public PresenceStatus GetAccountStatus(Guid accountId)
        {
            // Get the SL client instance to check current status
            var instance = _accountService.GetInstance(accountId);
            if (instance != null && instance.IsConnected)
            {
                // Primary approach: Check our internal tracking first (most reliable)
                if (_accountStatuses.TryGetValue(accountId, out var trackedStatus))
                {
                    // Verify the tracked status makes sense
                    var isBusyTracked = _busyStatuses.TryGetValue(accountId, out var isBusy) && isBusy;
                    
                    if (trackedStatus == PresenceStatus.Away)
                    {
                        _logger.LogDebug("Account {AccountId} status: Away (from internal tracking)", accountId);
                        instance.UpdatePresenceStatus("Away");
                        return PresenceStatus.Away;
                    }
                    else if (trackedStatus == PresenceStatus.Busy || isBusyTracked)
                    {
                        _logger.LogDebug("Account {AccountId} status: Busy (from internal tracking)", accountId);
                        instance.UpdatePresenceStatus("Busy");
                        return PresenceStatus.Busy;
                    }
                }
                
                // Fallback: Try to check Movement.Away (may not be reliable)
                try
                {
                    var isAway = instance.IsAway;
                    _logger.LogDebug("Account {AccountId} fallback check: SL client IsAway={IsAway}", accountId, isAway);
                    
                    if (isAway)
                    {
                        var awayStatus = PresenceStatus.Away;
                        _accountStatuses.AddOrUpdate(accountId, awayStatus, (key, oldValue) => awayStatus);
                        instance.UpdatePresenceStatus("Away");
                        return awayStatus;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking Movement.Away for account {AccountId}", accountId);
                }

                // Default to online for connected accounts
                var onlineStatus = PresenceStatus.Online;
                _accountStatuses.AddOrUpdate(accountId, onlineStatus, (key, oldValue) => onlineStatus);
                instance.UpdatePresenceStatus("Online");
                _logger.LogDebug("Account {AccountId} status: Online (default for connected)", accountId);
                return onlineStatus;
            }

            // Fall back to cached status if client unavailable
            if (_accountStatuses.TryGetValue(accountId, out var cachedStatus))
            {
                _logger.LogDebug("Account {AccountId} status: {Status} (cached, client unavailable)", accountId, cachedStatus);
                return cachedStatus;
            }

            // Default to online if we can't determine status
            _logger.LogDebug("Account {AccountId} status: Online (default, no cache or client)", accountId);
            return PresenceStatus.Online;
        }
    }
}