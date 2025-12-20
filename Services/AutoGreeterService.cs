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
    /// UNIFIED TRACKING LOGIC:
    /// - _avatarLastSeen: Tracks when each avatar was last seen (updated continuously from any source)
    /// - _lastGreetingTime: Tracks when we last sent a greeting to each avatar (2 min cooldown)
    /// - _avatarDepartures: Tracks when avatars left (for return detection)
    /// 
    /// GREETING RULES:
    /// 1. Never seen before: Send initial greeting
    /// 2. Last seen within past 3 hours: Send welcome back message
    /// 3. Last greeted less than 2 minutes ago: Ignore (spam prevention)
    /// 4. Still present (never left): Update last seen, don't re-greet
    /// 
    /// DUPLICATE PREVENTION:
    /// - Both coarse location and direct presence updates call UpdateLastSeen()
    /// - Last seen time is always updated, preventing re-greeting of present avatars
    /// - Quick returns (under 2 min) are treated as location tracking artifacts
    /// </summary>
    public class AutoGreeterService : IAutoGreeterService
    {
        private readonly ILogger<AutoGreeterService> _logger;
        private readonly IAccountService _accountService;
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        
        // Track when each avatar was last seen (continuously updated from any presence source)
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _avatarLastSeen = new();
        
        // Track when we last sent a greeting to each avatar (for 2-minute cooldown)
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _lastGreetingTime = new();
        
        // Track when avatars left the area (for return detection)
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _avatarDepartures = new();
        
        // Minimum cooldown between any greetings (including welcome back) to prevent spam
        private readonly TimeSpan _minGreetingCooldown = TimeSpan.FromMinutes(2);
        
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
        /// Update the last seen time for an avatar (called from any presence source)
        /// This keeps the tracking data fresh and prevents re-greeting of avatars that are still present
        /// </summary>
        public void UpdateLastSeen(Guid accountId, string avatarId)
        {
            var accountLastSeen = _avatarLastSeen.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, DateTime>());
            accountLastSeen.AddOrUpdate(avatarId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
            
            // If they're in departures, remove them (they're back/never left)
            if (_avatarDepartures.TryGetValue(accountId, out var accountDeps))
            {
                accountDeps.TryRemove(avatarId, out _);
            }
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
                    // Still update last seen even if greeter is disabled (for tracking purposes)
                    UpdateLastSeen(accountId, avatarId);
                    return;
                }
                
                // Check if we greeted them too recently (2 minute cooldown for spam prevention)
                if (_lastGreetingTime.TryGetValue(accountId, out var accountGreetings) &&
                    accountGreetings.TryGetValue(avatarId, out var lastGreeted) &&
                    DateTime.UtcNow - lastGreeted < _minGreetingCooldown)
                {
                    _logger.LogDebug("Avatar {AvatarId} was greeted too recently ({TotalSeconds:F0}s ago), skipping", 
                        avatarId, (DateTime.UtcNow - lastGreeted).TotalSeconds);
                    // Update last seen even though we're not greeting (they're still present)
                    UpdateLastSeen(accountId, avatarId);
                    return;
                }
                
                // Determine greeting status based on last seen time (BEFORE updating it)
                DateTime lastSeenTime = DateTime.MinValue;
                bool isNew = !_avatarLastSeen.TryGetValue(accountId, out var accountLastSeen) || 
                             !accountLastSeen.TryGetValue(avatarId, out lastSeenTime);
                
                bool isReturning = false;
                TimeSpan timeSinceLastSeen = TimeSpan.Zero;
                
                if (!isNew)
                {
                    timeSinceLastSeen = DateTime.UtcNow - lastSeenTime;
                    
                    // Check if they're actually returning (were in departures tracking)
                    if (_avatarDepartures.TryGetValue(accountId, out var accountDeps) &&
                        accountDeps.TryGetValue(avatarId, out var departureTime))
                    {
                        var timeSinceDeparture = DateTime.UtcNow - departureTime;
                        
                        // If they left briefly (< 2 min), treat as location tracking artifact
                        if (timeSinceDeparture < TimeSpan.FromMinutes(2))
                        {
                            _logger.LogDebug("Avatar {AvatarId} returned too quickly ({TotalSeconds:F0}s) - location tracking artifact", 
                                avatarId, timeSinceDeparture.TotalSeconds);
                            accountDeps.TryRemove(avatarId, out _);
                            return;
                        }
                        
                        // Check if they're within return window (configurable hours)
                        if (timeSinceDeparture <= TimeSpan.FromHours(account.AutoGreeterReturnTimeHours))
                        {
                            isReturning = true;
                            timeSinceLastSeen = timeSinceDeparture;
                            _logger.LogDebug("Avatar {AvatarId} is returning after {TotalMinutes:F1} minutes", 
                                avatarId, timeSinceDeparture.TotalMinutes);
                        }
                        else
                        {
                            // Outside return window, treat as new
                            _logger.LogDebug("Avatar {AvatarId} returned outside window ({TotalHours:F1} hours), treating as new", 
                                avatarId, timeSinceDeparture.TotalHours);
                            isNew = true;
                            isReturning = false;
                            accountDeps.TryRemove(avatarId, out _);
                        }
                    }
                    else
                    {
                        // Not in departures - they never left, still present
                        _logger.LogDebug("Avatar {AvatarId} still present (last seen {TotalSeconds:F0}s ago), skipping greeting", 
                            avatarId, timeSinceLastSeen.TotalSeconds);
                        // Update last seen to keep tracking current
                        UpdateLastSeen(accountId, avatarId);
                        return;
                    }
                }
                
                // Send appropriate greeting
                if (isReturning && account.AutoGreeterReturnEnabled)
                {
                    // Send welcome back message
                    var instance = _accountService.GetInstance(accountId);
                    if (instance == null || !instance.IsConnected)
                    {
                        _logger.LogWarning("Instance not found or not connected for account {AccountId}", accountId);
                        return;
                    }
                    
                    var returnMessage = FormatGreetingMessage(account.AutoGreeterReturnMessage, avatarId, displayName);
                    instance.SendChat(returnMessage, ChatType.Normal, 0);
                    
                    _logger.LogInformation("Sent welcome back greeting to {DisplayName} ({AvatarId}) after {Minutes:F1} min: {Message}", 
                        displayName, avatarId, timeSinceLastSeen.TotalMinutes, returnMessage);
                    
                    // Record greeting time and update last seen
                    RecordGreeting(accountId, avatarId);
                    UpdateLastSeen(accountId, avatarId);
                    
                    // Remove from departures
                    if (_avatarDepartures.TryGetValue(accountId, out var accountDeps))
                    {
                        accountDeps.TryRemove(avatarId, out _);
                    }
                }
                else if (isNew && account.AutoGreeterEnabled)
                {
                    // Send initial greeting
                    var instance = _accountService.GetInstance(accountId);
                    if (instance == null || !instance.IsConnected)
                    {
                        _logger.LogWarning("Instance not found or not connected for account {AccountId}", accountId);
                        return;
                    }
                    
                    var greetingMessage = FormatGreetingMessage(account.AutoGreeterMessage, avatarId, displayName);
                    instance.SendChat(greetingMessage, ChatType.Normal, 0);
                    
                    _logger.LogInformation("Sent initial greeting to {DisplayName} ({AvatarId}): {Message}", 
                        displayName, avatarId, greetingMessage);
                    
                    // Record greeting time and update last seen
                    RecordGreeting(accountId, avatarId);
                    UpdateLastSeen(accountId, avatarId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing auto-greeter for avatar {AvatarId} on account {AccountId}", 
                    avatarId, accountId);
            }
        }
        
        /// <summary>
        /// Record that we sent a greeting to this avatar
        /// </summary>
        private void RecordGreeting(Guid accountId, string avatarId)
        {
            var accountGreetings = _lastGreetingTime.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, DateTime>());
            accountGreetings.AddOrUpdate(avatarId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
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
        /// Check if an avatar has received an initial greeting (for backward compatibility with WebRadegastInstance)
        /// Now checks if avatar was last seen and not departed
        /// </summary>
        public bool HasHadInitialGreeting(Guid accountId, string avatarId)
        {
            // Check if we've seen them before and they haven't departed
            if (_avatarLastSeen.TryGetValue(accountId, out var accountLastSeen) &&
                accountLastSeen.ContainsKey(avatarId))
            {
                // They've been seen - check if they're still around (not in departures)
                bool hasDeparted = _avatarDepartures.TryGetValue(accountId, out var accountDeps) &&
                                   accountDeps.ContainsKey(avatarId);
                return !hasDeparted;
            }
            
            return false;
        }
        
        /// <summary>
        /// Check if an avatar has been greeted recently (for backward compatibility)
        /// Now checks last greeting time with 2-minute cooldown
        /// </summary>
        public bool HasBeenGreeted(Guid accountId, string avatarId)
        {
            if (_lastGreetingTime.TryGetValue(accountId, out var accountGreetings) &&
                accountGreetings.TryGetValue(avatarId, out var lastGreeted))
            {
                return DateTime.UtcNow - lastGreeted < _minGreetingCooldown;
            }
            
            return false;
        }
        
        /// <summary>
        /// Clear greeted avatars for an account (e.g., on region change)
        /// </summary>
        public void ClearGreetedAvatars(Guid accountId)
        {
            if (_lastGreetingTime.TryGetValue(accountId, out var accountGreetings))
            {
                accountGreetings.Clear();
            }
            if (_avatarLastSeen.TryGetValue(accountId, out var accountLastSeen))
            {
                accountLastSeen.Clear();
            }
            if (_avatarDepartures.TryGetValue(accountId, out var accountDeps))
            {
                accountDeps.Clear();
            }
            
            _logger.LogDebug("Cleared all avatar tracking for account {AccountId}", accountId);
        }
        
        /// <summary>
        /// Track when an avatar leaves the area
        /// </summary>
        public void TrackAvatarDeparture(string avatarId, Guid accountId)
        {
            var accountDepartures = _avatarDepartures.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, DateTime>());
            accountDepartures.AddOrUpdate(avatarId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
            
            _logger.LogDebug("Tracked departure of avatar {AvatarId} for account {AccountId}", avatarId, accountId);
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
                        
                        // Also remove from last seen since they're outside the return window
                        if (_avatarLastSeen.TryGetValue(accountId, out var accountLastSeen))
                        {
                            accountLastSeen.TryRemove(avatarId, out _);
                        }
                    }
                    
                    if (oldAvatars.Count > 0)
                    {
                        _logger.LogDebug("Cleaned up {Count} old avatar departures for account {AccountId}", oldAvatars.Count, accountId);
                    }
                }
                
                // Clean up old last-seen times (for avatars that never departed but are very old)
                // Use 2x the return window as safety
                if (_avatarLastSeen.TryGetValue(accountId, out var accountLastSeen2))
                {
                    var veryOldCutoff = DateTime.UtcNow.AddHours(-returnTimeHours * 2);
                    var oldAvatars = accountLastSeen2.Where(kvp => kvp.Value < veryOldCutoff).Select(kvp => kvp.Key).ToList();
                    
                    foreach (var avatarId in oldAvatars)
                    {
                        accountLastSeen2.TryRemove(avatarId, out _);
                    }
                    
                    if (oldAvatars.Count > 0)
                    {
                        _logger.LogDebug("Cleaned up {Count} very old last-seen times for account {AccountId}", oldAvatars.Count, accountId);
                    }
                }
                
                // Clean up old greeting times (older than cooldown period)
                if (_lastGreetingTime.TryGetValue(accountId, out var accountGreetings))
                {
                    var cutoffTime = DateTime.UtcNow.Add(-_minGreetingCooldown);
                    var oldAvatars = accountGreetings.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();
                    
                    foreach (var avatarId in oldAvatars)
                    {
                        accountGreetings.TryRemove(avatarId, out _);
                    }
                    
                    if (oldAvatars.Count > 0)
                    {
                        _logger.LogDebug("Cleaned up {Count} old greeting times for account {AccountId}", oldAvatars.Count, accountId);
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
                // Get the list of avatars we're currently tracking via last seen
                if (!_avatarLastSeen.TryGetValue(accountId, out var accountLastSeen))
                {
                    return; // No avatars tracked for this account
                }
                
                var currentSet = new HashSet<string>(currentNearbyAvatarIds);
                var trackedAvatars = accountLastSeen.Keys.ToList();
                
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
