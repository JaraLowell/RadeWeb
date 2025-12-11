using Microsoft.EntityFrameworkCore;
using OpenMetaverse;
using RadegastWeb.Data;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for managing auto-greeter functionality
    /// Detects new avatars within 20 meters and sends customized greetings
    /// </summary>
    public class AutoGreeterService : IAutoGreeterService
    {
        private readonly ILogger<AutoGreeterService> _logger;
        private readonly IAccountService _accountService;
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        
        // Track which avatars have been greeted per account to avoid spam
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _greetedAvatars = new();
        
        // Track when avatars left the area for return detection
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _avatarDepartures = new();
        
        // Cooldown period before greeting same avatar again (e.g., after region change)
        private readonly TimeSpan _greetCooldown = TimeSpan.FromMinutes(15);
        
        public AutoGreeterService(
            ILogger<AutoGreeterService> logger,
            IAccountService accountService,
            IDbContextFactory<RadegastDbContext> dbContextFactory)
        {
            _logger = logger;
            _accountService = accountService;
            _dbContextFactory = dbContextFactory;
        }
        
        /// <summary>
        /// Process a new avatar that has entered radar range
        /// </summary>
        public async Task ProcessNewAvatarAsync(string avatarId, string displayName, double distance, Guid accountId)
        {
            try
            {
                // Check if distance is within 20 meters
                if (distance > 20.0)
                {
                    return;
                }
                
                // Get account settings from database
                using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                var account = await dbContext.Accounts.FindAsync(accountId);
                
                if (account == null)
                {
                    _logger.LogWarning("Account {AccountId} not found for auto-greeter", accountId);
                    return;
                }
                
                // Check if this is a returning avatar
                var isReturning = IsReturningAvatar(accountId, avatarId, account.AutoGreeterReturnTimeHours);
                
                // If returning and return greeter is enabled
                if (isReturning && account.AutoGreeterReturnEnabled)
                {
                    // Get the WebRadegastInstance for this account
                    var returnInstance = _accountService.GetInstance(accountId);
                    if (returnInstance == null || !returnInstance.IsConnected)
                    {
                        _logger.LogWarning("Instance not found or not connected for account {AccountId}", accountId);
                        return;
                    }
                    
                    // Format the return greeting message
                    var returnMessage = FormatGreetingMessage(account.AutoGreeterReturnMessage, avatarId, displayName);
                    
                    // Send the return greeting to local chat
                    returnInstance.SendChat(returnMessage, ChatType.Normal, 0);
                    
                    _logger.LogInformation("Auto-greeter sent return greeting to {DisplayName} ({AvatarId}) from account {AccountId}: {Message}", 
                        displayName, avatarId, accountId, returnMessage);
                    
                    // Remove from departures and mark as greeted
                    RemoveFromDepartures(accountId, avatarId);
                    MarkAsGreeted(accountId, avatarId);
                    
                    return;
                }
                
                // Check if we've already greeted this avatar recently
                if (HasBeenGreeted(accountId, avatarId))
                {
                    _logger.LogDebug("Avatar {AvatarId} already greeted by account {AccountId}", avatarId, accountId);
                    return;
                }
                
                // Check if auto-greeter is enabled
                if (!account.AutoGreeterEnabled)
                {
                    _logger.LogDebug("Auto-greeter disabled for account {AccountId}", accountId);
                    return;
                }
                
                // Get the WebRadegastInstance for this account
                var instance = _accountService.GetInstance(accountId);
                if (instance == null || !instance.IsConnected)
                {
                    _logger.LogWarning("Instance not found or not connected for account {AccountId}", accountId);
                    return;
                }
                
                // Format the greeting message
                var greetingMessage = FormatGreetingMessage(account.AutoGreeterMessage, avatarId, displayName);
                
                // Send the greeting to local chat
                instance.SendChat(greetingMessage, ChatType.Normal, 0);
                
                _logger.LogInformation("Auto-greeter sent greeting to {DisplayName} ({AvatarId}) from account {AccountId}: {Message}", 
                    displayName, avatarId, accountId, greetingMessage);
                
                // Mark this avatar as greeted
                MarkAsGreeted(accountId, avatarId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing auto-greeter for avatar {AvatarId} on account {AccountId}", 
                    avatarId, accountId);
            }
        }
        
        /// <summary>
        /// Format the greeting message with avatar information
        /// </summary>
        private string FormatGreetingMessage(string template, string avatarId, string displayName)
        {
            // Replace {name} placeholder with SL agent link
            var agentLink = $"secondlife:///app/agent/{avatarId}/about";
            var message = template.Replace("{name}", agentLink);
            
            // Also support {displayname} for plain text name
            message = message.Replace("{displayname}", displayName);
            
            return message;
        }
        
        /// <summary>
        /// Mark an avatar as greeted
        /// </summary>
        private void MarkAsGreeted(Guid accountId, string avatarId)
        {
            var accountGreeted = _greetedAvatars.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, DateTime>());
            accountGreeted.AddOrUpdate(avatarId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
        }
        
        /// <summary>
        /// Check if an avatar has been greeted recently
        /// </summary>
        public bool HasBeenGreeted(Guid accountId, string avatarId)
        {
            if (!_greetedAvatars.TryGetValue(accountId, out var accountGreeted))
            {
                return false;
            }
            
            if (!accountGreeted.TryGetValue(avatarId, out var greetedTime))
            {
                return false;
            }
            
            // Check if cooldown period has passed
            if (DateTime.UtcNow - greetedTime > _greetCooldown)
            {
                // Cooldown expired, allow greeting again
                accountGreeted.TryRemove(avatarId, out _);
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Clear greeted avatars for an account (e.g., on region change)
        /// </summary>
        public void ClearGreetedAvatars(Guid accountId)
        {
            if (_greetedAvatars.TryGetValue(accountId, out var accountGreeted))
            {
                accountGreeted.Clear();
                _logger.LogDebug("Cleared greeted avatars for account {AccountId}", accountId);
            }
        }
        
        /// <summary>
        /// Track when an avatar leaves the area
        /// </summary>
        public void TrackAvatarDeparture(string avatarId, Guid accountId)
        {
            var accountDepartures = _avatarDepartures.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, DateTime>());
            accountDepartures.AddOrUpdate(avatarId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
            
            // Remove from greeted list when they leave
            if (_greetedAvatars.TryGetValue(accountId, out var accountGreeted))
            {
                accountGreeted.TryRemove(avatarId, out _);
            }
            
            _logger.LogDebug("Tracked departure of avatar {AvatarId} for account {AccountId}", avatarId, accountId);
        }
        
        /// <summary>
        /// Check if an avatar is returning within the configured time window
        /// </summary>
        private bool IsReturningAvatar(Guid accountId, string avatarId, int returnTimeHours)
        {
            if (!_avatarDepartures.TryGetValue(accountId, out var accountDepartures))
            {
                return false;
            }
            
            if (!accountDepartures.TryGetValue(avatarId, out var departureTime))
            {
                return false;
            }
            
            var timeSinceDeparture = DateTime.UtcNow - departureTime;
            return timeSinceDeparture <= TimeSpan.FromHours(returnTimeHours);
        }
        
        /// <summary>
        /// Remove an avatar from the departures tracking
        /// </summary>
        private void RemoveFromDepartures(Guid accountId, string avatarId)
        {
            if (_avatarDepartures.TryGetValue(accountId, out var accountDepartures))
            {
                accountDepartures.TryRemove(avatarId, out _);
            }
        }
        
        /// <summary>
        /// Clean up old avatar tracking data
        /// </summary>
        public void CleanupOldTrackingData(Guid accountId)
        {
            try
            {
                var maxReturnTimeHours = 24; // Use a safe maximum, will check against account settings
                
                // Clean up old departures
                if (_avatarDepartures.TryGetValue(accountId, out var accountDepartures))
                {
                    var cutoffTime = DateTime.UtcNow.AddHours(-maxReturnTimeHours);
                    var oldAvatars = accountDepartures.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();
                    
                    foreach (var avatarId in oldAvatars)
                    {
                        accountDepartures.TryRemove(avatarId, out _);
                    }
                    
                    if (oldAvatars.Count > 0)
                    {
                        _logger.LogDebug("Cleaned up {Count} old avatar departures for account {AccountId}", oldAvatars.Count, accountId);
                    }
                }
                
                // Clean up old greeted avatars
                if (_greetedAvatars.TryGetValue(accountId, out var accountGreeted))
                {
                    var cutoffTime = DateTime.UtcNow.Add(-_greetCooldown);
                    var oldAvatars = accountGreeted.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();
                    
                    foreach (var avatarId in oldAvatars)
                    {
                        accountGreeted.TryRemove(avatarId, out _);
                    }
                    
                    if (oldAvatars.Count > 0)
                    {
                        _logger.LogDebug("Cleaned up {Count} old greeted avatars for account {AccountId}", oldAvatars.Count, accountId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old tracking data for account {AccountId}", accountId);
            }
        }
        
        /// <summary>
        /// Detect avatars that have left by comparing current nearby list with tracked avatars
        /// </summary>
        public void DetectDepartures(Guid accountId, IEnumerable<string> currentNearbyAvatarIds)
        {
            try
            {
                // Get the list of avatars we're currently tracking
                if (!_greetedAvatars.TryGetValue(accountId, out var accountGreeted))
                {
                    return; // No avatars tracked for this account
                }
                
                var currentSet = new HashSet<string>(currentNearbyAvatarIds);
                var trackedAvatars = accountGreeted.Keys.ToList();
                
                // Find avatars that were tracked but are no longer nearby
                foreach (var avatarId in trackedAvatars)
                {
                    if (!currentSet.Contains(avatarId))
                    {
                        // This avatar has left - track their departure
                        TrackAvatarDeparture(avatarId, accountId);
                        _logger.LogDebug("Detected departure of avatar {AvatarId} for account {AccountId}", avatarId, accountId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting departures for account {AccountId}", accountId);
            }
        }
    }
}
