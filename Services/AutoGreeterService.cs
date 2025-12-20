using Microsoft.EntityFrameworkCore;
using OpenMetaverse;
using RadegastWeb.Data;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for managing auto-greeter functionality
    /// Detects new avatars within 20 meters and sends customized greetings
    /// 
    /// TRACKING LOGIC:
    /// - _greetedAvatars: Avatars that have been greeted recently (cleared on departure or after cooldown)
    /// - _initialGreetings: Avatars that received an initial greeting (persists until they leave or data cleanup)
    /// - _avatarDepartures: Avatars that left the area (tracks departure time for return detection)
    /// 
    /// DUPLICATE PREVENTION:
    /// - Both HasBeenGreeted() and HasHadInitialGreeting() must be checked before greeting
    /// - This prevents duplicate greetings when avatars transition between coarse/detailed location tracking
    /// - Quick returns (under 2 min) are treated as location tracking artifacts, not actual departures
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
        
        // Track which avatars have received their initial greeting
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _initialGreetings = new();
        
        // Cooldown period before greeting same avatar again (e.g., after region change)
        private readonly TimeSpan _greetCooldown = TimeSpan.FromMinutes(15);
        
        // Minimum cooldown between any greetings (including welcome back) to prevent spam
        private readonly TimeSpan _minGreetingCooldown = TimeSpan.FromMinutes(2);
        
        // Minimum time an avatar must be gone before welcome back message is sent
        private readonly TimeSpan _minReturnTime = TimeSpan.FromMinutes(2);
        
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
                
                // Skip all processing if both greeting features are disabled
                if (!account.AutoGreeterEnabled && !account.AutoGreeterReturnEnabled)
                {
                    return;
                }
                
                // Check if this avatar has received an initial greeting
                var hadInitialGreeting = HasHadInitialGreeting(accountId, avatarId);
                
                // Check if this is a returning avatar (left and came back)
                var isReturning = IsReturningAvatar(accountId, avatarId, account.AutoGreeterReturnTimeHours, out var timeSinceDeparture);
                
                _logger.LogDebug("Avatar {AvatarId} greeting check: hadInitial={HadInitial}, isReturning={IsReturning}, timeSince={TimeSince:F1}min",
                    avatarId, hadInitialGreeting, isReturning, timeSinceDeparture.TotalMinutes);
                
                // If avatar had initial greeting and is now returning
                if (hadInitialGreeting && isReturning)
                {
                    // Check if they've been gone long enough for a welcome back message
                    if (timeSinceDeparture < _minReturnTime)
                    {
                        _logger.LogDebug("Avatar {AvatarId} returned too quickly ({TotalMinutes:F1} minutes) - likely coarse/detailed location switch, marking as present", 
                            avatarId, timeSinceDeparture.TotalMinutes);
                        RemoveFromDepartures(accountId, avatarId);
                        // Mark as greeted to prevent duplicate greetings during location tracking transitions
                        MarkAsGreeted(accountId, avatarId);
                        // Keep initial greeting marker since they were already greeted and are still around
                        return;
                    }
                    
                    // If return greeter is enabled, send the return greeting
                    if (account.AutoGreeterReturnEnabled)
                    {
                        // Check minimum cooldown to prevent spam (even for welcome back messages)
                        if (HasBeenGreetedRecently(accountId, avatarId, _minGreetingCooldown))
                        {
                            _logger.LogDebug("Avatar {AvatarId} was greeted too recently (cooldown active) for account {AccountId}", avatarId, accountId);
                            return;
                        }
                        
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
                        
                        _logger.LogInformation("Auto-greeter sent return greeting to {DisplayName} ({AvatarId}) from account {AccountId} after {Minutes:F1} minutes away: {Message}", 
                            displayName, avatarId, accountId, timeSinceDeparture.TotalMinutes, returnMessage);
                        
                        // Remove from departures and mark as greeted
                        RemoveFromDepartures(accountId, avatarId);
                        MarkAsGreeted(accountId, avatarId);
                    }
                    else
                    {
                        // Return greeting is disabled, so skip greeting this returning avatar entirely
                        _logger.LogDebug("Avatar {AvatarId} is returning but return greeter is disabled for account {AccountId}, skipping greeting", avatarId, accountId);
                        RemoveFromDepartures(accountId, avatarId);
                    }
                    
                    return;
                }
                
                // If avatar had initial greeting but is not returning (still here or just moved around), skip
                // HOWEVER: If they ARE in departures dictionary, they left and came back but outside return window
                // In that case, their initial greeting data might be stale and we should re-greet them
                if (hadInitialGreeting && !isReturning)
                {
                    // Check if they're in departures dictionary
                    bool hadDepartureTracking = _avatarDepartures.TryGetValue(accountId, out var accountDeps) && 
                                                accountDeps.ContainsKey(avatarId);
                    
                    if (hadDepartureTracking)
                    {
                        // They left and came back, but outside the return time window
                        // Treat them as a new visitor (clear old data)
                        _logger.LogDebug("Avatar {AvatarId} returned outside return window, treating as new visitor", avatarId);
                        
                        // Clear their initial greeting marker so they can be greeted again
                        if (_initialGreetings.TryGetValue(accountId, out var accountInitial))
                        {
                            accountInitial.TryRemove(avatarId, out _);
                        }
                        
                        // Remove from departures
                        RemoveFromDepartures(accountId, avatarId);
                        
                        // Continue to initial greeting logic below
                    }
                    else
                    {
                        // They're still around (never left), skip to avoid duplicate greeting
                        _logger.LogDebug("Avatar {AvatarId} already had initial greeting and is still present, skipping", avatarId);
                        
                        // Make sure they're marked as greeted to prevent any edge cases
                        MarkAsGreeted(accountId, avatarId);
                        return;
                    }
                }
                
                // Check if we've already greeted this avatar recently
                if (HasBeenGreeted(accountId, avatarId))
                {
                    _logger.LogDebug("Avatar {AvatarId} already greeted by account {AccountId}", avatarId, accountId);
                    return;
                }
                
                // Check if auto-greeter is enabled for initial greeting
                if (!account.AutoGreeterEnabled)
                {
                    _logger.LogDebug("Auto-greeter disabled for account {AccountId}, skipping initial greeting", accountId);
                    return;
                }
                
                // Mark this avatar as greeted IMMEDIATELY to prevent duplicate greetings
                // This must happen before any async operations that might allow concurrent processing
                MarkAsGreeted(accountId, avatarId);
                
                // Also mark as having received initial greeting (for return detection)
                MarkInitialGreeting(accountId, avatarId);
                
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
            
            // Support {user} for first name only
            if (message.Contains("{user}"))
            {
                var firstName = ExtractFirstName(avatarId, displayName);
                message = message.Replace("{user}", firstName);
            }
            
            return message;
        }
        
        /// <summary>
        /// Extract the first name from display name cache, falling back to legacy name or URL
        /// </summary>
        private string ExtractFirstName(string avatarId, string displayName)
        {
            try
            {
                // Try to get from database cache
                using var dbContext = _dbContextFactory.CreateDbContext();
                var cachedName = dbContext.GlobalDisplayNames.FirstOrDefault(d => d.AvatarId == avatarId);
                
                if (cachedName != null)
                {
                    // If we have a display name and it's not the default, use the first part
                    if (!cachedName.IsDefaultDisplayName && 
                        !string.IsNullOrEmpty(cachedName.DisplayNameValue) &&
                        cachedName.DisplayNameValue != "Loading..." &&
                        cachedName.DisplayNameValue != "???")
                    {
                        var parts = cachedName.DisplayNameValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            return parts[0]; // Return first part of display name (e.g., "Jara" from "Jara Lowell")
                        }
                    }
                    
                    // Fall back to legacy first name if available
                    if (!string.IsNullOrEmpty(cachedName.LegacyFirstName))
                    {
                        return cachedName.LegacyFirstName; // Return legacy first name (e.g., "Jaraziah")
                    }
                }
                
                // Try to extract from the displayName parameter passed in
                if (!string.IsNullOrEmpty(displayName))
                {
                    var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        return parts[0];
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error extracting first name for avatar {AvatarId}, falling back to URL", avatarId);
            }
            
            // Final fallback: return the agent link URL
            return $"secondlife:///app/agent/{avatarId}/about";
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
        /// Mark an avatar as having received their initial greeting
        /// </summary>
        private void MarkInitialGreeting(Guid accountId, string avatarId)
        {
            var accountInitial = _initialGreetings.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, DateTime>());
            accountInitial.AddOrUpdate(avatarId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
        }
        
        /// <summary>
        /// Check if an avatar has received an initial greeting (persists until they leave)
        /// Used by WebRadegastInstance to avoid re-greeting avatars that just changed state (seated -> standing)
        /// </summary>
        public bool HasHadInitialGreeting(Guid accountId, string avatarId)
        {
            if (!_initialGreetings.TryGetValue(accountId, out var accountInitial))
            {
                return false;
            }
            
            return accountInitial.ContainsKey(avatarId);
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
        /// Check if an avatar has been greeted within a specific time period
        /// Used for minimum cooldown enforcement to prevent spam
        /// </summary>
        private bool HasBeenGreetedRecently(Guid accountId, string avatarId, TimeSpan cooldownPeriod)
        {
            if (!_greetedAvatars.TryGetValue(accountId, out var accountGreeted))
            {
                return false;
            }
            
            if (!accountGreeted.TryGetValue(avatarId, out var greetedTime))
            {
                return false;
            }
            
            // Check if the specified cooldown period has passed
            return DateTime.UtcNow - greetedTime <= cooldownPeriod;
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
            
            // Keep initial greeting tracking for return eligibility check
            // It will be cleaned up later based on return time window
            
            _logger.LogDebug("Tracked departure of avatar {AvatarId} for account {AccountId}", avatarId, accountId);
        }
        
        /// <summary>
        /// Check if an avatar is returning within the configured time window
        /// Returns true if the avatar left and is now returning within the time window
        /// </summary>
        private bool IsReturningAvatar(Guid accountId, string avatarId, int returnTimeHours, out TimeSpan timeSinceDeparture)
        {
            timeSinceDeparture = TimeSpan.Zero;
            
            if (!_avatarDepartures.TryGetValue(accountId, out var accountDepartures))
            {
                return false;
            }
            
            if (!accountDepartures.TryGetValue(avatarId, out var departureTime))
            {
                return false;
            }
            
            timeSinceDeparture = DateTime.UtcNow - departureTime;
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
        public async Task CleanupOldTrackingDataAsync(Guid accountId)
        {
            try
            {
                // Get account settings to use proper return time window
                using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                var account = await dbContext.Accounts.FindAsync(accountId);
                var returnTimeHours = account?.AutoGreeterReturnTimeHours ?? 3;
                
                // Clean up old departures (after return time window has passed)
                if (_avatarDepartures.TryGetValue(accountId, out var accountDepartures))
                {
                    var cutoffTime = DateTime.UtcNow.AddHours(-returnTimeHours);
                    var oldAvatars = accountDepartures.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();
                    
                    foreach (var avatarId in oldAvatars)
                    {
                        accountDepartures.TryRemove(avatarId, out _);
                        
                        // Also remove from initial greetings since they're outside the return window
                        if (_initialGreetings.TryGetValue(accountId, out var accountInitial))
                        {
                            accountInitial.TryRemove(avatarId, out _);
                            _logger.LogDebug("Removed avatar {AvatarId} from initial greetings (outside return window) for account {AccountId}", avatarId, accountId);
                        }
                    }
                    
                    if (oldAvatars.Count > 0)
                    {
                        _logger.LogDebug("Cleaned up {Count} old avatar departures for account {AccountId}", oldAvatars.Count, accountId);
                    }
                }
                
                // Clean up very old initial greetings (for avatars that never left or tracking was missed)
                // Use 2x the return window as safety
                if (_initialGreetings.TryGetValue(accountId, out var accountInitial2))
                {
                    var veryOldCutoff = DateTime.UtcNow.AddHours(-returnTimeHours * 2);
                    var oldAvatars = accountInitial2.Where(kvp => kvp.Value < veryOldCutoff).Select(kvp => kvp.Key).ToList();
                    
                    foreach (var avatarId in oldAvatars)
                    {
                        accountInitial2.TryRemove(avatarId, out _);
                    }
                    
                    if (oldAvatars.Count > 0)
                    {
                        _logger.LogDebug("Cleaned up {Count} very old initial greetings for account {AccountId}", oldAvatars.Count, accountId);
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
