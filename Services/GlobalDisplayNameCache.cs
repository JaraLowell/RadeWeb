using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenMetaverse;
using RadegastWeb.Data;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Global display name cache that spans all accounts to reduce duplicate requests
    /// and improve performance across multiple concurrent Second Life connections.
    /// Based on Radegast's NameManager implementation.
    /// 
    /// IMPORTANT: This cache protects existing display names from being overwritten
    /// with invalid names (null, "", "Loading...") or legacy names when a valid 
    /// custom display name already exists. This prevents display name degradation
    /// when multiple accounts login concurrently.
    /// </summary>
    public interface IGlobalDisplayNameCache
    {
        Task<string> GetDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        Task<string> GetLegacyNameAsync(string avatarId, string? fallbackName = null);
        Task<string> GetUserNameAsync(string avatarId, string? fallbackName = null);
        Task<DisplayName?> GetCachedDisplayNameAsync(string avatarId);
        string? GetCachedDisplayName(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart);
        void UpdateDisplayName(DisplayName displayName);
        Task UpdateDisplayNamesAsync(Dictionary<UUID, AgentDisplayName> displayNames);
        Task UpdateLegacyNamesAsync(Dictionary<UUID, string> legacyNames);
        Task PreloadDisplayNamesAsync(IEnumerable<string> avatarIds);
        Task<bool> RequestDisplayNamesAsync(List<string> avatarIds, Guid requestingAccountId);
        Task LoadCachedNamesAsync();
        Task SaveCacheAsync();
        void CleanExpiredCache();
        void RegisterGridClient(Guid accountId, GridClient client);
        void UnregisterGridClient(Guid accountId);
        
        // Events for real-time updates
        event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
    }

    public class GlobalDisplayNameCache : IGlobalDisplayNameCache, IDisposable, IAsyncDisposable
    {
        private readonly ILogger<GlobalDisplayNameCache> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IMemoryCache _memoryCache;
        
        // Global cache that spans all accounts (keyed by avatar UUID)
        private readonly ConcurrentDictionary<string, DisplayName> _globalNameCache = new();
        
        // Track which grid clients we can use for requests
        private readonly ConcurrentDictionary<Guid, GridClient> _activeClients = new();
        
        // Request queue and processing
        private readonly Channel<NameRequest> _requestQueue;
        private readonly ChannelWriter<NameRequest> _requestWriter;
        private readonly Task _processingTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        
        // Rate limiting and batching
        private readonly SemaphoreSlim _requestSemaphore = new(5, 5); // Max 5 concurrent requests
        private readonly TimeSpan _batchDelay = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan _memoryCacheExpiry = TimeSpan.FromHours(2); // Short-term memory cache
        private readonly TimeSpan _databaseCacheExpiry = TimeSpan.FromHours(48); // Long-term database cache
        private readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(30);
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(30);
        
        private readonly Timer _saveTimer;
        private readonly Timer _cleanupTimer;
        private volatile bool _hasUpdates = false;
        private DateTime _lastSave = DateTime.UtcNow;
        private volatile bool _disposed = false;

        public event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;

        public GlobalDisplayNameCache(
            ILogger<GlobalDisplayNameCache> logger,
            IServiceProvider serviceProvider,
            IMemoryCache memoryCache)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _memoryCache = memoryCache;
            
            // Create unbounded channel for name requests
            var options = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            };
            _requestQueue = Channel.CreateUnbounded<NameRequest>(options);
            _requestWriter = _requestQueue.Writer;
            
            // Start background processing task
            _processingTask = Task.Run(ProcessRequests, _cancellationTokenSource.Token);
            
            // Start periodic save timer
            _saveTimer = new Timer(SaveTimerCallback, null, _saveInterval, _saveInterval);
            
            // Start periodic cleanup timer for memory cache
            _cleanupTimer = new Timer(CleanupTimerCallback, null, _cleanupInterval, _cleanupInterval);
            
            // Load cached names on startup
            _ = Task.Run(LoadCachedNamesAsync);
        }

        public async Task<DisplayName?> GetCachedDisplayNameAsync(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId))
                return null;

            // Layer 1: Check short-term memory cache first (2 hours expiry)
            if (_globalNameCache.TryGetValue(avatarId, out var cachedName))
            {
                if (DateTime.UtcNow - cachedName.CachedAt < _memoryCacheExpiry)
                {
                    return cachedName;
                }
                // Expired from memory cache, but don't remove yet (cleanup will handle it)
            }

            // Layer 2: Check IMemoryCache (also short-term)
            var cacheKey = $"global_display_name_{avatarId}";
            if (_memoryCache.TryGetValue(cacheKey, out DisplayName? memoryCached) && memoryCached != null)
            {
                if (DateTime.UtcNow - memoryCached.CachedAt < _memoryCacheExpiry)
                {
                    // Refresh the memory cache
                    _globalNameCache.TryAdd(avatarId, memoryCached);
                    return memoryCached;
                }
            }

            // Layer 3: Check database (48 hours expiry - medium-term storage)
            if (_disposed)
                return null;
                
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RadegastDbContext>>();
                using var context = dbContextFactory.CreateDbContext();
                
                var globalDbName = await context.GlobalDisplayNames
                    .FirstOrDefaultAsync(dn => dn.AvatarId == avatarId);

                if (globalDbName != null && globalDbName.CachedAt > DateTime.UtcNow.Subtract(_databaseCacheExpiry))
                {
                    var globalName = globalDbName.ToDisplayName();
                    
                    // Store in both memory caches with short expiry
                    _globalNameCache.TryAdd(avatarId, globalName);
                    _memoryCache.Set(cacheKey, globalName, _memoryCacheExpiry);
                    return globalName;
                }
            }
            catch (ObjectDisposedException)
            {
                // Service provider disposed, return null
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving cached display name for {AvatarId}", avatarId);
            }

            return null;
        }

        public string? GetCachedDisplayName(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart)
        {
            if (string.IsNullOrEmpty(avatarId))
                return null;

            var cacheKey = $"global_display_name_{avatarId}";

            // Layer 1: Check memory cache (2 hours expiry)
            if (_globalNameCache.TryGetValue(avatarId, out var cachedName))
            {
                if (DateTime.UtcNow - cachedName.CachedAt < _memoryCacheExpiry)
                {
                    // Refresh IMemoryCache for consistency
                    _memoryCache.Set(cacheKey, cachedName, _memoryCacheExpiry);
                    return FormatDisplayName(cachedName, mode);
                }
                // Don't remove expired entries here to avoid race conditions
                // Let the cleanup process handle expired entries periodically
            }

            // Layer 2: Check IMemoryCache
            if (_memoryCache.TryGetValue(cacheKey, out DisplayName? memoryCached))
            {
                if (memoryCached != null && DateTime.UtcNow - memoryCached.CachedAt < _memoryCacheExpiry)
                {
                    // Sync back to ConcurrentDictionary for consistency
                    _globalNameCache.AddOrUpdate(avatarId, memoryCached, (key, existing) => memoryCached);
                    return FormatDisplayName(memoryCached, mode);
                }
            }

            // For synchronous method, we don't hit the database
            // The caller should use the async version or trigger background loading
            return null;
        }

        public async Task<string> GetDisplayNameAsync(string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? "Unknown User";
            }

            var cached = await GetCachedDisplayNameAsync(avatarId);
            
            if (cached != null)
            {
                // Check if we need to refresh
                if (DateTime.UtcNow > cached.NextUpdate)
                {
                    // Queue for refresh but return cached value immediately
                    await QueueNameRequestAsync(avatarId);
                }
                
                return FormatDisplayName(cached, mode);
            }

            // Not in cache, queue for fetching
            await QueueNameRequestAsync(avatarId);
            
            return fallbackName ?? "Loading...";
        }

        public async Task<string> GetLegacyNameAsync(string avatarId, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? "Unknown User";
            }

            var cached = await GetCachedDisplayNameAsync(avatarId);
            if (cached != null)
            {
                return cached.LegacyFullName;
            }

            await QueueNameRequestAsync(avatarId);
            return fallbackName ?? "Loading...";
        }

        public async Task<string> GetUserNameAsync(string avatarId, string? fallbackName = null)
        {
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName?.ToLower().Replace(" ", ".") ?? "unknown.user";
            }

            var cached = await GetCachedDisplayNameAsync(avatarId);
            if (cached != null)
            {
                return cached.UserName;
            }

            await QueueNameRequestAsync(avatarId);
            return fallbackName?.ToLower().Replace(" ", ".") ?? "loading...";
        }

        public void UpdateDisplayName(DisplayName displayName)
        {
            if (displayName == null || string.IsNullOrEmpty(displayName.AvatarId))
                return;

            // Check if the new display name is valid (not blank, null, or "Loading...")
            var isNewNameValid = !IsInvalidNameValue(displayName.DisplayNameValue);
            
            // Get existing cached name to compare
            var existingName = _globalNameCache.TryGetValue(displayName.AvatarId, out var existing) ? existing : null;
            var isExistingNameValid = existingName != null && !IsInvalidNameValue(existingName.DisplayNameValue);
            
            // Only update if:
            // 1. We don't have an existing name, OR
            // 2. The new name is valid and the existing name is invalid, OR  
            // 3. Both names are valid but the new one is different (an actual update), AND
            // 4. Don't overwrite a custom display name with a default/legacy name unless the existing is invalid
            bool shouldUpdate = existingName == null ||
                               (isNewNameValid && !isExistingNameValid) ||
                               (isNewNameValid && isExistingNameValid && 
                                !existingName.DisplayNameValue.Equals(displayName.DisplayNameValue, StringComparison.Ordinal) &&
                                !(displayName.IsDefaultDisplayName && !existingName.IsDefaultDisplayName)); // Don't overwrite custom with default
            
            if (!shouldUpdate)
            {
                _logger.LogDebug("PROTECTED: Skipping display name update for {AvatarId}: new='{NewName}' (valid={NewValid}, default={NewDefault}), existing='{ExistingName}' (valid={ExistingValid}, default={ExistingDefault})", 
                    displayName.AvatarId, displayName.DisplayNameValue, isNewNameValid, displayName.IsDefaultDisplayName,
                    existingName?.DisplayNameValue, isExistingNameValid, existingName?.IsDefaultDisplayName ?? true);
                return;
            }

            var globalName = new DisplayName
            {
                AvatarId = displayName.AvatarId,
                DisplayNameValue = displayName.DisplayNameValue,
                UserName = displayName.UserName,
                LegacyFirstName = displayName.LegacyFirstName,
                LegacyLastName = displayName.LegacyLastName,
                IsDefaultDisplayName = displayName.IsDefaultDisplayName,
                NextUpdate = displayName.NextUpdate,
                LastUpdated = DateTime.UtcNow,
                CachedAt = DateTime.UtcNow
            };

            _globalNameCache.AddOrUpdate(displayName.AvatarId, globalName, (key, existingCache) => globalName);
            
            var cacheKey = $"global_display_name_{displayName.AvatarId}";
            _memoryCache.Set(cacheKey, globalName, _memoryCacheExpiry);
            
            _hasUpdates = true;
            
            _logger.LogDebug("Updated global display name for {AvatarId}: '{DisplayName}' (valid={Valid})", 
                displayName.AvatarId, displayName.DisplayNameValue, isNewNameValid);
            
            // Fire event for real-time updates
            DisplayNameChanged?.Invoke(this, new DisplayNameChangedEventArgs(displayName.AvatarId, globalName));
        }

        public Task UpdateDisplayNamesAsync(Dictionary<UUID, AgentDisplayName> displayNames)
        {
            var updatedNames = new List<DisplayName>();
            
            foreach (var kvp in displayNames)
            {
                var agentDisplayName = kvp.Value;
                var avatarId = agentDisplayName.ID.ToString();
                
                if (IsInvalidNameValue(agentDisplayName.DisplayName))
                    continue;

                // Clean display name at database level: only strip "Resident" if it's a default display name 
                // and the legacy last name is "Resident" (indicating it's a legacy SL naming artifact, not intentional)
                var cleanedDisplayName = agentDisplayName.IsDefaultDisplayName && 
                                        agentDisplayName.LegacyLastName.Equals("Resident", StringComparison.OrdinalIgnoreCase) &&
                                        agentDisplayName.DisplayName.EndsWith(" Resident", StringComparison.OrdinalIgnoreCase)
                    ? agentDisplayName.DisplayName.Substring(0, agentDisplayName.DisplayName.Length - " Resident".Length)
                    : agentDisplayName.DisplayName;

                var displayName = new DisplayName
                {
                    AvatarId = avatarId,
                    DisplayNameValue = cleanedDisplayName,
                    UserName = agentDisplayName.UserName,
                    LegacyFirstName = agentDisplayName.LegacyFirstName,
                    LegacyLastName = agentDisplayName.LegacyLastName.Equals("Resident", StringComparison.OrdinalIgnoreCase) ? string.Empty : agentDisplayName.LegacyLastName,
                    IsDefaultDisplayName = agentDisplayName.IsDefaultDisplayName,
                    NextUpdate = agentDisplayName.NextUpdate,
                    LastUpdated = DateTime.UtcNow,
                    CachedAt = DateTime.UtcNow
                };

                UpdateDisplayName(displayName);
                updatedNames.Add(displayName);
            }
            
            _logger.LogDebug("Updated {Count} display names in global cache", updatedNames.Count);
            return Task.CompletedTask;
        }

        public Task UpdateLegacyNamesAsync(Dictionary<UUID, string> legacyNames)
        {
            foreach (var kvp in legacyNames)
            {
                var avatarId = kvp.Key.ToString();
                var fullName = kvp.Value;
                
                if (IsInvalidNameValue(fullName))
                    continue;

                var parts = fullName.Trim().Split(' ');
                if (parts.Length < 2)
                    continue;

                var firstName = parts[0];
                var lastName = parts[1];
                var userName = lastName == "Resident" ? firstName.ToLower() : $"{firstName}.{lastName}".ToLower();

                // Clean legacy names at database level: always strip "Resident" from legacy names since they are default display names
                var cleanedDisplayName = lastName.Equals("Resident", StringComparison.OrdinalIgnoreCase) 
                    ? firstName 
                    : fullName;

                if (cleanedDisplayName != fullName)
                {
                    _logger.LogDebug("Cleaned legacy name for {AvatarId}: '{Original}' -> '{Cleaned}'", 
                        avatarId, fullName, cleanedDisplayName);
                }

                // Check if we already have a display name for this avatar
                var existingName = _globalNameCache.TryGetValue(avatarId, out var existing) ? existing : null;
                var isExistingNameValid = existingName != null && !IsInvalidNameValue(existingName.DisplayNameValue);
                
                // Only update with legacy name if:
                // 1. We don't have an existing name, OR
                // 2. The existing name is invalid/placeholder (Loading..., ???, etc.), OR
                // 3. The existing display name equals the legacy name (no custom display name)
                // 
                // NEVER overwrite a valid custom display name with a legacy name
                if (existingName != null && isExistingNameValid && 
                    !existingName.DisplayNameValue.Equals(cleanedDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("PROTECTED: Skipping legacy name update for {AvatarId}: existing valid display name '{ExistingName}' (custom={IsCustom}), not overwriting with legacy name '{LegacyName}'", 
                        avatarId, existingName.DisplayNameValue, !existingName.IsDefaultDisplayName, cleanedDisplayName);
                    continue;
                }

                var displayName = new DisplayName
                {
                    AvatarId = avatarId,
                    DisplayNameValue = cleanedDisplayName,
                    UserName = userName,
                    LegacyFirstName = firstName,
                    LegacyLastName = lastName.Equals("Resident", StringComparison.OrdinalIgnoreCase) ? string.Empty : lastName,
                    IsDefaultDisplayName = true,
                    NextUpdate = DateTime.UtcNow.AddHours(24),
                    LastUpdated = DateTime.UtcNow,
                    CachedAt = DateTime.UtcNow
                };

                UpdateDisplayName(displayName);
            }
            
            return Task.CompletedTask;
        }

        public async Task PreloadDisplayNamesAsync(IEnumerable<string> avatarIds)
        {
            var uncachedIds = new List<string>();
            
            foreach (var avatarId in avatarIds)
            {
                var cached = await GetCachedDisplayNameAsync(avatarId);
                if (cached == null || DateTime.UtcNow > cached.NextUpdate)
                {
                    uncachedIds.Add(avatarId);
                }
            }
            
            if (uncachedIds.Count > 0)
            {
                _logger.LogDebug("Preloading {Count} display names in global cache", uncachedIds.Count);
                
                // Queue all uncached IDs
                foreach (var avatarId in uncachedIds)
                {
                    await QueueNameRequestAsync(avatarId);
                }
            }
        }

        public async Task<bool> RequestDisplayNamesAsync(List<string> avatarIds, Guid requestingAccountId)
        {
            if (!_activeClients.TryGetValue(requestingAccountId, out var client) || 
                client?.Network?.Connected != true)
            {
                // Try to find any active client
                client = _activeClients.Values.FirstOrDefault(c => c?.Network?.Connected == true);
                if (client == null)
                {
                    _logger.LogWarning("No active grid clients available for display name requests");
                    return false;
                }
            }

            var uuidList = avatarIds.Where(id => UUID.TryParse(id, out _))
                                  .Select(id => new UUID(id))
                                  .ToList();

            if (uuidList.Count == 0)
                return false;

            try
            {
                var tcs = new TaskCompletionSource<bool>();
                
                if (client.Avatars.DisplayNamesAvailable())
                {
                    // Use display names API
                    await client.Avatars.GetDisplayNames(uuidList, 
                        (success, names, badIDs) =>
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    if (success && names?.Length > 0)
                                    {
                                        var displayNameDict = names.ToDictionary(n => n.ID, n => n);
                                        await UpdateDisplayNamesAsync(displayNameDict);
                                    }
                                    
                                    // Handle failed IDs with legacy names
                                    if (badIDs?.Length > 0)
                                    {
                                        RequestLegacyNames(client, badIDs.ToList());
                                    }
                                }
                                finally
                                {
                                    tcs.TrySetResult(success);
                                }
                            });
                        });
                }
                else
                {
                    // Fall back to legacy names
                    RequestLegacyNames(client, uuidList);
                    tcs.SetResult(true);
                }
                
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting display names for {Count} avatars", uuidList.Count);
                return false;
            }
        }

        public void RegisterGridClient(Guid accountId, GridClient client)
        {
            _activeClients.AddOrUpdate(accountId, client, (key, existing) => client);
            
            // Subscribe to display name events
            client.Avatars.DisplayNameUpdate += OnDisplayNameUpdate;
            client.Avatars.UUIDNameReply += OnUUIDNameReply;
            
            // If this is the first client being registered and cache is empty, 
            // trigger a background reload of cached names from database
            if (_activeClients.Count == 1 && _globalNameCache.IsEmpty)
            {
                _logger.LogInformation("Global display name cache is empty with first client registration, triggering cache pre-load");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LoadCachedNamesAsync();
                        _logger.LogInformation("Pre-loaded {Count} cached display names for new grid client registration", _globalNameCache.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to pre-load cached display names for grid client registration");
                    }
                });
            }
            else
            {
                _logger.LogDebug("Global display name cache has {Count} entries with {ClientCount} active clients", 
                    _globalNameCache.Count, _activeClients.Count);
            }
            
            _logger.LogDebug("Registered grid client for account {AccountId}", accountId);
        }

        public void UnregisterGridClient(Guid accountId)
        {
            if (_activeClients.TryRemove(accountId, out var client))
            {
                // Unsubscribe from events
                client.Avatars.DisplayNameUpdate -= OnDisplayNameUpdate;
                client.Avatars.UUIDNameReply -= OnUUIDNameReply;
                
                _logger.LogDebug("Unregistered grid client for account {AccountId}", accountId);
            }
        }

        public async Task LoadCachedNamesAsync()
        {
            if (_disposed)
                return;
                
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RadegastDbContext>>();
                using var context = dbContextFactory.CreateDbContext();
                
                // Load from GlobalDisplayNames table (database acts as medium-term cache)
                var cutoffTime = DateTime.UtcNow.Subtract(_databaseCacheExpiry);
                var globalNames = await context.GlobalDisplayNames
                    .Where(dn => dn.CachedAt > cutoffTime)
                    .ToListAsync();
                
                foreach (var globalName in globalNames)
                {
                    var displayName = globalName.ToDisplayName();
                    // Only load into memory cache if it's still fresh enough for memory
                    if (DateTime.UtcNow - displayName.CachedAt < _memoryCacheExpiry)
                    {
                        _globalNameCache.TryAdd(globalName.AvatarId, displayName);
                        var cacheKey = $"global_display_name_{globalName.AvatarId}";
                        _memoryCache.Set(cacheKey, displayName, _memoryCacheExpiry);
                    }
                }
                
                _logger.LogInformation("Loaded {Total} cached display names into global cache", globalNames.Count);
            }
            catch (ObjectDisposedException)
            {
                // Service provider disposed during shutdown, log and ignore
                _logger.LogDebug("Service provider disposed during cache loading");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cached display names");
            }
        }

        public async Task SaveCacheAsync()
        {
            if (!_hasUpdates || _disposed)
                return;

            try
            {
                // Check if service provider is still available
                if (_disposed)
                    return;

                using var scope = _serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RadegastDbContext>>();
                using var context = dbContextFactory.CreateDbContext();
                
                var namesToSave = _globalNameCache.Values.Where(n => n.LastUpdated > _lastSave).ToList();
                
                if (namesToSave.Count == 0)
                {
                    _logger.LogDebug("No names to save to global cache");
                    return;
                }
                
                _logger.LogDebug("Saving {Count} display names to global cache", namesToSave.Count);
                
                // Process all names and save to GlobalDisplayNames table
                foreach (var name in namesToSave)
                {
                    // Check if we already have this in GlobalDisplayNames table
                    var existing = await context.GlobalDisplayNames
                        .FirstOrDefaultAsync(dn => dn.AvatarId == name.AvatarId);

                    if (existing != null)
                    {
                        // Only update if the new display name is valid or if the existing one is also invalid
                        var isNewNameValid = !IsInvalidNameValue(name.DisplayNameValue);
                        var isExistingNameValid = !IsInvalidNameValue(existing.DisplayNameValue);
                        
                        // Only update if:
                        // 1. New name is valid and existing is invalid, OR
                        // 2. Both names are valid but different (actual update), AND
                        // 3. Don't overwrite a custom display name with a default/legacy name unless the existing is invalid
                        bool shouldUpdate = (isNewNameValid && !isExistingNameValid) ||
                                          (isNewNameValid && isExistingNameValid && 
                                           !existing.DisplayNameValue.Equals(name.DisplayNameValue, StringComparison.Ordinal) &&
                                           !(name.IsDefaultDisplayName && !existing.IsDefaultDisplayName)); // Don't overwrite custom with default
                        
                        if (shouldUpdate)
                        {
                            // Update existing global record
                            existing.DisplayNameValue = name.DisplayNameValue;
                            existing.UserName = name.UserName;
                            existing.LegacyFirstName = name.LegacyFirstName;
                            existing.LegacyLastName = name.LegacyLastName;
                            existing.IsDefaultDisplayName = name.IsDefaultDisplayName;
                            existing.NextUpdate = name.NextUpdate;
                            existing.LastUpdated = name.LastUpdated;
                            existing.CachedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            // Skip this update but still update LastUpdated to track processing
                            existing.LastUpdated = name.LastUpdated;
                            existing.CachedAt = DateTime.UtcNow;
                            
                            _logger.LogDebug("PROTECTED: Skipping database save for {AvatarId}: new='{NewName}' (valid={NewValid}, default={NewDefault}), existing='{ExistingName}' (valid={ExistingValid}, default={ExistingDefault})", 
                                name.AvatarId, name.DisplayNameValue, isNewNameValid, name.IsDefaultDisplayName,
                                existing.DisplayNameValue, isExistingNameValid, existing.IsDefaultDisplayName);
                        }
                    }
                    else
                    {
                        // Create new global record
                        var globalDisplayName = GlobalDisplayName.FromDisplayName(name);
                        globalDisplayName.CachedAt = DateTime.UtcNow;
                        context.GlobalDisplayNames.Add(globalDisplayName);
                    }
                }
                
                // Save all changes at once with retry logic for race conditions
                var maxRetries = 3;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        await context.SaveChangesAsync();
                        break; // Success, exit retry loop
                    }
                    catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
                    {
                        // Handle race condition - another thread inserted the same record
                        if (attempt < maxRetries - 1)
                        {
                            _logger.LogDebug("Unique constraint violation in global cache batch save, retrying... (attempt {Attempt})", attempt + 1);
                            
                            // Refresh the context to get the latest state and try again
                            context.ChangeTracker.Clear();
                            
                            // Re-process the problematic records by refreshing from database
                            foreach (var name in namesToSave)
                            {
                                var existing = await context.GlobalDisplayNames
                                    .FirstOrDefaultAsync(dn => dn.AvatarId == name.AvatarId);

                                if (existing == null)
                                {
                                    // Still doesn't exist, try to add again
                                    var globalDisplayName = GlobalDisplayName.FromDisplayName(name);
                                    globalDisplayName.CachedAt = DateTime.UtcNow;
                                    context.GlobalDisplayNames.Add(globalDisplayName);
                                }
                                else
                                {
                                    // Update the existing record
                                    existing.DisplayNameValue = name.DisplayNameValue;
                                    existing.UserName = name.UserName;
                                    existing.LegacyFirstName = name.LegacyFirstName;
                                    existing.LegacyLastName = name.LegacyLastName;
                                    existing.IsDefaultDisplayName = name.IsDefaultDisplayName;
                                    existing.NextUpdate = name.NextUpdate;
                                    existing.LastUpdated = name.LastUpdated;
                                    existing.CachedAt = DateTime.UtcNow;
                                }
                            }
                            
                            await Task.Delay(10 * (attempt + 1)); // 10ms, 20ms, 30ms delays
                            continue;
                        }
                        else
                        {
                            // Final attempt failed, log warning but don't throw
                            _logger.LogWarning(dbEx, "Failed to save display names to database after {MaxRetries} attempts in global cache", maxRetries);
                            break;
                        }
                    }
                }
                
                _hasUpdates = false;
                _lastSave = DateTime.UtcNow;
                
                _logger.LogDebug("Processed {Count} display names for database save", namesToSave.Count);
            }
            catch (ObjectDisposedException)
            {
                // Service provider disposed during shutdown, log and ignore
                _logger.LogDebug("Service provider disposed during cache save");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving display name cache");
            }
        }

        public void CleanExpiredCache()
        {
            // Clean expired entries from memory cache (2-hour expiry)
            var expired = _globalNameCache.Where(kvp => DateTime.UtcNow - kvp.Value.CachedAt > _memoryCacheExpiry)
                                         .Select(kvp => kvp.Key)
                                         .ToList();

            foreach (var key in expired)
            {
                _globalNameCache.TryRemove(key, out _);
                var cacheKey = $"global_display_name_{key}";
                _memoryCache.Remove(cacheKey);
            }

            if (expired.Count > 0)
            {
                _logger.LogDebug("Cleaned {Count} expired display names from memory cache", expired.Count);
            }
        }

        private void CleanupTimerCallback(object? state)
        {
            if (_disposed)
                return;

            try
            {
                CleanExpiredCache();
                
                // Also clean database periodically (every 4th cleanup = every 2 hours)
                var now = DateTime.UtcNow;
                if ((now.Hour % 2 == 0) && now.Minute < 30)
                {
                    _ = Task.Run(CleanExpiredDatabaseEntriesAsync);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic cache cleanup");
            }
        }

        private async Task CleanExpiredDatabaseEntriesAsync()
        {
            if (_disposed)
                return;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RadegastDbContext>>();
                using var context = dbContextFactory.CreateDbContext();
                
                var cutoffTime = DateTime.UtcNow.Subtract(_databaseCacheExpiry);
                var expiredEntries = await context.GlobalDisplayNames
                    .Where(dn => dn.CachedAt < cutoffTime)
                    .ToListAsync();

                if (expiredEntries.Any())
                {
                    context.GlobalDisplayNames.RemoveRange(expiredEntries);
                    await context.SaveChangesAsync();
                    
                    _logger.LogInformation("Cleaned {Count} expired display names from database", expiredEntries.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning expired database entries");
            }
        }

        private async Task QueueNameRequestAsync(string avatarId)
        {
            if (!await _requestWriter.WaitToWriteAsync())
                return;

            await _requestWriter.WriteAsync(new NameRequest(avatarId, DateTime.UtcNow));
        }

        private async Task ProcessRequests()
        {
            var reader = _requestQueue.Reader;
            var batchedRequests = new Dictionary<string, DateTime>();
            
            while (await reader.WaitToReadAsync(_cancellationTokenSource.Token))
            {
                try
                {
                    await _requestSemaphore.WaitAsync(_cancellationTokenSource.Token);
                    
                    // Collect requests for batching
                    var deadline = DateTime.UtcNow.Add(_batchDelay);
                    while (DateTime.UtcNow < deadline && batchedRequests.Count < 100)
                    {
                        if (reader.TryRead(out var request))
                        {
                            batchedRequests[request.AvatarId] = request.RequestTime;
                        }
                        else
                        {
                            await Task.Delay(5, _cancellationTokenSource.Token);
                        }
                    }
                    
                    if (batchedRequests.Count > 0)
                    {
                        var avatarIds = batchedRequests.Keys.ToList();
                        var requestingAccount = _activeClients.Keys.FirstOrDefault();
                        
                        await RequestDisplayNamesAsync(avatarIds, requestingAccount);
                        batchedRequests.Clear();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing name requests");
                }
                finally
                {
                    _requestSemaphore.Release();
                }
            }
        }

        private void RequestLegacyNames(GridClient client, List<UUID> uuidList)
        {
            EventHandler<UUIDNameReplyEventArgs> handler = null!;
            handler = (sender, e) =>
            {
                client.Avatars.UUIDNameReply -= handler;
                _ = Task.Run(() => UpdateLegacyNamesAsync(e.Names));
            };

            client.Avatars.UUIDNameReply += handler;
            client.Avatars.RequestAvatarNames(uuidList);
        }

        private void OnDisplayNameUpdate(object? sender, DisplayNameUpdateEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                var displayNameDict = new Dictionary<UUID, AgentDisplayName> { { e.DisplayName.ID, e.DisplayName } };
                await UpdateDisplayNamesAsync(displayNameDict);
            });
        }

        private void OnUUIDNameReply(object? sender, UUIDNameReplyEventArgs e)
        {
            _ = Task.Run(() => UpdateLegacyNamesAsync(e.Names));
        }

        private void SaveTimerCallback(object? state)
        {
            if (_disposed)
                return;
                
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!_disposed)
                    {
                        await SaveCacheAsync();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Service provider was disposed, ignore
                    _logger.LogDebug("Service provider disposed during save operation, skipping save");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in save timer callback");
                }
            });
        }

        private static bool IsInvalidNameValue(string? nameValue)
        {
            return string.IsNullOrWhiteSpace(nameValue) || 
                   nameValue.Equals("Loading...", StringComparison.OrdinalIgnoreCase) ||
                   nameValue.Equals("???", StringComparison.OrdinalIgnoreCase) ||
                   nameValue.Equals("Unknown User", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatDisplayName(DisplayName displayName, NameDisplayMode mode)
        {
            return mode switch
            {
                NameDisplayMode.Standard => displayName.LegacyFullName,
                NameDisplayMode.OnlyDisplayName => displayName.DisplayNameValue,
                NameDisplayMode.Smart => displayName.IsDefaultDisplayName 
                    ? displayName.DisplayNameValue 
                    : $"{displayName.DisplayNameValue} ({displayName.UserName})",
                NameDisplayMode.DisplayNameAndUserName => displayName.IsDefaultDisplayName
                    ? displayName.DisplayNameValue
                    : $"{displayName.DisplayNameValue} ({displayName.UserName})",
                _ => displayName.LegacyFullName
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
                
            _disposed = true;
            
            _cancellationTokenSource.Cancel();
            _saveTimer?.Dispose();
            _cleanupTimer?.Dispose();
            
            // Save any pending changes
            try
            {
                if (_hasUpdates && !_disposed)
                {
                    await SaveCacheAsync();
                }
            }
            catch (ObjectDisposedException)
            {
                // Service provider already disposed, ignore
                _logger.LogDebug("Service provider disposed during final save, skipping");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache during disposal");
            }
            
            // Wait for processing task to complete before disposing
            try
            {
                if (_processingTask != null)
                {
                    await _processingTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for processing task to complete during async disposal");
            }
            finally
            {
                // Only dispose if task is in a completion state
                if (_processingTask?.IsCompleted == true)
                {
                    _processingTask.Dispose();
                }
            }
            
            _requestSemaphore?.Dispose();
            _cancellationTokenSource?.Dispose();
            
            // Unregister all clients
            foreach (var accountId in _activeClients.Keys.ToList())
            {
                UnregisterGridClient(accountId);
            }
        }

        public void Dispose()
        {
            try
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during synchronous disposal");
            }
        }

        private record NameRequest(string AvatarId, DateTime RequestTime);
    }

    public class DisplayNameChangedEventArgs : EventArgs
    {
        public string AvatarId { get; }
        public DisplayName DisplayName { get; }

        public DisplayNameChangedEventArgs(string avatarId, DisplayName displayName)
        {
            AvatarId = avatarId;
            DisplayName = displayName;
        }
    }
}