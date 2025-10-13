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
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(48); // Same as Radegast
        private readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(30);
        
        private readonly Timer _saveTimer;
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
            
            // Load cached names on startup
            _ = Task.Run(LoadCachedNamesAsync);
        }

        public async Task<DisplayName?> GetCachedDisplayNameAsync(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId))
                return null;

            // Check in-memory cache first
            if (_globalNameCache.TryGetValue(avatarId, out var cachedName))
            {
                // Check if cache is still valid
                if (DateTime.UtcNow - cachedName.CachedAt < _cacheExpiry)
                {
                    return cachedName;
                }
                else
                {
                    // Remove expired entry
                    _globalNameCache.TryRemove(avatarId, out _);
                }
            }

            // Check memory cache (faster than DB)
            var cacheKey = $"global_display_name_{avatarId}";
            if (_memoryCache.TryGetValue(cacheKey, out DisplayName? memoryCached) && memoryCached != null)
            {
                if (DateTime.UtcNow - memoryCached.CachedAt < _cacheExpiry)
                {
                    _globalNameCache.TryAdd(avatarId, memoryCached);
                    return memoryCached;
                }
            }

            // Try database as last resort
            if (_disposed)
                return null;
                
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RadegastDbContext>>();
                using var context = dbContextFactory.CreateDbContext();
                
                var dbName = await context.DisplayNames
                    .Where(dn => dn.AvatarId == avatarId)
                    .OrderByDescending(dn => dn.LastUpdated)
                    .FirstOrDefaultAsync();

                if (dbName != null && DateTime.UtcNow - dbName.CachedAt < _cacheExpiry)
                {
                    // Remove account-specific data for global cache
                    var globalName = new DisplayName
                    {
                        AvatarId = dbName.AvatarId,
                        DisplayNameValue = dbName.DisplayNameValue,
                        UserName = dbName.UserName,
                        LegacyFirstName = dbName.LegacyFirstName,
                        LegacyLastName = dbName.LegacyLastName,
                        IsDefaultDisplayName = dbName.IsDefaultDisplayName,
                        NextUpdate = dbName.NextUpdate,
                        LastUpdated = dbName.LastUpdated,
                        CachedAt = dbName.CachedAt,
                        AccountId = Guid.Empty // Global cache doesn't need account ID
                    };
                    
                    _globalNameCache.TryAdd(avatarId, globalName);
                    _memoryCache.Set(cacheKey, globalName, _cacheExpiry);
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

            // Check in-memory cache first
            if (_globalNameCache.TryGetValue(avatarId, out var cachedName))
            {
                // Check if cache is still valid
                if (DateTime.UtcNow - cachedName.CachedAt < _cacheExpiry)
                {
                    return mode switch
                    {
                        NameDisplayMode.Smart => !string.IsNullOrEmpty(cachedName.DisplayNameValue) 
                            ? cachedName.DisplayNameValue 
                            : $"{cachedName.LegacyFirstName} {cachedName.LegacyLastName}".Trim(),
                        NameDisplayMode.OnlyDisplayName => cachedName.DisplayNameValue ?? $"{cachedName.LegacyFirstName} {cachedName.LegacyLastName}".Trim(),
                        NameDisplayMode.Standard => $"{cachedName.LegacyFirstName} {cachedName.LegacyLastName}".Trim(),
                        NameDisplayMode.DisplayNameAndUserName => !string.IsNullOrEmpty(cachedName.UserName) 
                            ? $"{cachedName.DisplayNameValue} ({cachedName.UserName})"
                            : cachedName.DisplayNameValue ?? $"{cachedName.LegacyFirstName} {cachedName.LegacyLastName}".Trim(),
                        _ => cachedName.DisplayNameValue ?? $"{cachedName.LegacyFirstName} {cachedName.LegacyLastName}".Trim()
                    };
                }
                else
                {
                    // Remove expired entry
                    _globalNameCache.TryRemove(avatarId, out _);
                }
            }

            // Check memory cache (faster than DB)
            var cacheKey = $"global_display_name_{avatarId}";
            if (_memoryCache.TryGetValue(cacheKey, out DisplayName? memoryCached))
            {
                if (memoryCached != null && DateTime.UtcNow - memoryCached.CachedAt < _cacheExpiry)
                {
                    _globalNameCache.TryAdd(avatarId, memoryCached);
                    return mode switch
                    {
                        NameDisplayMode.Smart => !string.IsNullOrEmpty(memoryCached.DisplayNameValue) 
                            ? memoryCached.DisplayNameValue 
                            : $"{memoryCached.LegacyFirstName} {memoryCached.LegacyLastName}".Trim(),
                        NameDisplayMode.OnlyDisplayName => memoryCached.DisplayNameValue ?? $"{memoryCached.LegacyFirstName} {memoryCached.LegacyLastName}".Trim(),
                        NameDisplayMode.Standard => $"{memoryCached.LegacyFirstName} {memoryCached.LegacyLastName}".Trim(),
                        NameDisplayMode.DisplayNameAndUserName => !string.IsNullOrEmpty(memoryCached.UserName) 
                            ? $"{memoryCached.DisplayNameValue} ({memoryCached.UserName})"
                            : memoryCached.DisplayNameValue ?? $"{memoryCached.LegacyFirstName} {memoryCached.LegacyLastName}".Trim(),
                        _ => memoryCached.DisplayNameValue ?? $"{memoryCached.LegacyFirstName} {memoryCached.LegacyLastName}".Trim()
                    };
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
                CachedAt = DateTime.UtcNow,
                AccountId = Guid.Empty
            };

            _globalNameCache.AddOrUpdate(displayName.AvatarId, globalName, (key, existing) => globalName);
            
            var cacheKey = $"global_display_name_{displayName.AvatarId}";
            _memoryCache.Set(cacheKey, globalName, _cacheExpiry);
            
            _hasUpdates = true;
            
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

                var displayName = new DisplayName
                {
                    AvatarId = avatarId,
                    DisplayNameValue = agentDisplayName.DisplayName,
                    UserName = agentDisplayName.UserName,
                    LegacyFirstName = agentDisplayName.LegacyFirstName,
                    LegacyLastName = agentDisplayName.LegacyLastName,
                    IsDefaultDisplayName = agentDisplayName.IsDefaultDisplayName,
                    NextUpdate = agentDisplayName.NextUpdate,
                    LastUpdated = DateTime.UtcNow,
                    CachedAt = DateTime.UtcNow,
                    AccountId = Guid.Empty
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

                var displayName = new DisplayName
                {
                    AvatarId = avatarId,
                    DisplayNameValue = fullName,
                    UserName = userName,
                    LegacyFirstName = firstName,
                    LegacyLastName = lastName,
                    IsDefaultDisplayName = true,
                    NextUpdate = DateTime.UtcNow.AddHours(24),
                    LastUpdated = DateTime.UtcNow,
                    CachedAt = DateTime.UtcNow,
                    AccountId = Guid.Empty
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
                
                var cachedNames = await context.DisplayNames
                    .Where(dn => dn.CachedAt > DateTime.UtcNow.AddDays(-2)) // Load recent cache
                    .ToListAsync();

                var globalNames = new Dictionary<string, DisplayName>();
                
                foreach (var name in cachedNames)
                {
                    var globalName = new DisplayName
                    {
                        AvatarId = name.AvatarId,
                        DisplayNameValue = name.DisplayNameValue,
                        UserName = name.UserName,
                        LegacyFirstName = name.LegacyFirstName,
                        LegacyLastName = name.LegacyLastName,
                        IsDefaultDisplayName = name.IsDefaultDisplayName,
                        NextUpdate = name.NextUpdate,
                        LastUpdated = name.LastUpdated,
                        CachedAt = name.CachedAt,
                        AccountId = Guid.Empty
                    };
                    
                    // Keep the most recent version for each avatar
                    if (!globalNames.TryGetValue(name.AvatarId, out var existing) ||
                        name.LastUpdated > existing.LastUpdated)
                    {
                        globalNames[name.AvatarId] = globalName;
                    }
                }
                
                foreach (var kvp in globalNames)
                {
                    _globalNameCache.TryAdd(kvp.Key, kvp.Value);
                    var cacheKey = $"global_display_name_{kvp.Key}";
                    _memoryCache.Set(cacheKey, kvp.Value, _cacheExpiry);
                }
                
                _logger.LogInformation("Loaded {Count} cached display names into global cache", globalNames.Count);
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
                
                // Process all names and prepare changes
                foreach (var name in namesToSave)
                {
                    // Find if there's already a record for this avatar (use any account's record)
                    var existing = await context.DisplayNames
                        .FirstOrDefaultAsync(dn => dn.AvatarId == name.AvatarId);

                    if (existing != null)
                    {
                        // Update existing
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
                        // Create new with a placeholder account (we'll use the first available account)
                        var firstAccount = await context.Accounts.FirstOrDefaultAsync();
                        if (firstAccount != null)
                        {
                            name.AccountId = firstAccount.Id;
                            name.CachedAt = DateTime.UtcNow;
                            context.DisplayNames.Add(name);
                        }
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
                                var existing = await context.DisplayNames
                                    .FirstOrDefaultAsync(dn => dn.AvatarId == name.AvatarId);

                                if (existing == null)
                                {
                                    // Still doesn't exist, try to add again
                                    var firstAccount = await context.Accounts.FirstOrDefaultAsync();
                                    if (firstAccount != null)
                                    {
                                        var newName = new DisplayName
                                        {
                                            AvatarId = name.AvatarId,
                                            DisplayNameValue = name.DisplayNameValue,
                                            UserName = name.UserName,
                                            LegacyFirstName = name.LegacyFirstName,
                                            LegacyLastName = name.LegacyLastName,
                                            IsDefaultDisplayName = name.IsDefaultDisplayName,
                                            NextUpdate = name.NextUpdate,
                                            LastUpdated = name.LastUpdated,
                                            CachedAt = DateTime.UtcNow,
                                            AccountId = firstAccount.Id
                                        };
                                        context.DisplayNames.Add(newName);
                                    }
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
            var expired = _globalNameCache.Where(kvp => DateTime.UtcNow - kvp.Value.CachedAt > _cacheExpiry)
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
                _logger.LogDebug("Cleaned {Count} expired display names from global cache", expired.Count);
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
                   nameValue.Equals("???", StringComparison.OrdinalIgnoreCase);
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
                NameDisplayMode.DisplayNameAndUserName => $"{displayName.DisplayNameValue} ({displayName.UserName})",
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