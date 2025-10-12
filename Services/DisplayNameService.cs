using OpenMetaverse;
using RadegastWeb.Models;
using RadegastWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace RadegastWeb.Services
{

    public interface IDisplayNameService
    {
        Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        Task<string> GetLegacyNameAsync(Guid accountId, string avatarId, string? fallbackName = null);
        Task<string> GetUserNameAsync(Guid accountId, string avatarId, string? fallbackName = null);
        Task RefreshDisplayNameAsync(Guid accountId, string avatarId);
        Task PreloadDisplayNamesAsync(Guid accountId, IEnumerable<string> avatarIds);
        Task<bool> UpdateDisplayNamesAsync(Guid accountId, Dictionary<UUID, AgentDisplayName> displayNames);
        Task<bool> UpdateLegacyNamesAsync(Guid accountId, Dictionary<UUID, string> legacyNames);
        Task CleanExpiredCacheAsync(Guid accountId);
        Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId);
        void CleanupAccount(Guid accountId);
        
        // Event for display name changes
        event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
    }

    public class DisplayNameService : IDisplayNameService
    {
        private readonly ILogger<DisplayNameService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IGlobalDisplayNameCache _globalCache;
        private readonly string _connectionString;
        
        // Event for display name changes
        public event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
        
        // Cache for display names per account
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DisplayName>> _nameCache = new();
        
        // Request channels per account for efficient batching (like Radegast)
        private readonly ConcurrentDictionary<Guid, Channel<string>> _requestChannels = new();
        private readonly ConcurrentDictionary<Guid, Task> _processingTasks = new();
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationTokens = new();
        
        // Rate limiting per account (matches Radegast's TokenBucket approach)
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _rateLimiters = new();
        private readonly TimeSpan _rateLimitWindow = TimeSpan.FromSeconds(1);
        private readonly int _maxRequestsPerSecond = 15; // Increased from 5
        private readonly int _burstSize = 25; // Allow bursts like Radegast
        
        // Cache expiry settings
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(48);
        private readonly TimeSpan _refreshInterval = TimeSpan.FromHours(24);
        
        // Batching settings (matches Radegast's approach)
        private readonly int _maxBatchSize = 100; // Radegast uses 100
        private readonly TimeSpan _batchWindow = TimeSpan.FromMilliseconds(100); // Radegast uses 100ms

        public DisplayNameService(
            ILogger<DisplayNameService> logger, 
            IServiceProvider serviceProvider, 
            IConfiguration configuration,
            IGlobalDisplayNameCache globalCache)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _globalCache = globalCache;
            
            var contentRoot = configuration.GetValue<string>("ContentRoot") ?? Directory.GetCurrentDirectory();
            var dataDirectory = Path.Combine(contentRoot, "data");
            var dbPath = Path.Combine(dataDirectory, "radegast.db");
            _connectionString = $"Data Source={dbPath}";
        }

        private RadegastDbContext CreateDbContext()
        {
            var optionsBuilder = new DbContextOptionsBuilder<RadegastDbContext>();
            optionsBuilder.UseSqlite(_connectionString)
                         .UseLoggerFactory(LoggerFactory.Create(builder => 
                             builder.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning)
                                   .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)));
            return new RadegastDbContext(optionsBuilder.Options);
        }

        private ConcurrentDictionary<string, DisplayName> GetAccountCache(Guid accountId)
        {
            return _nameCache.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, DisplayName>());
        }

        private static bool IsInvalidNameValue(string? nameValue)
        {
            return string.IsNullOrWhiteSpace(nameValue) || 
                   nameValue.Equals("Loading...", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<DisplayName?> GetCachedDisplayNameAsync(Guid accountId, string avatarId)
        {
            var cache = GetAccountCache(accountId);
            
            // Try memory cache first
            if (cache.TryGetValue(avatarId, out var cachedName))
            {
                // Check if cache is still valid
                if (DateTime.UtcNow - cachedName.CachedAt < _cacheExpiry)
                {
                    return cachedName;
                }
                else
                {
                    // Remove expired cache entry
                    cache.TryRemove(avatarId, out _);
                }
            }

            // Try database cache
            try
            {
                using var context = CreateDbContext();
                var dbName = await context.DisplayNames
                    .FirstOrDefaultAsync(dn => dn.AccountId == accountId && dn.AvatarId == avatarId);

                if (dbName != null && DateTime.UtcNow - dbName.CachedAt < _cacheExpiry)
                {
                    // Restore to memory cache
                    cache.TryAdd(avatarId, dbName);
                    return dbName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving cached display name for {AvatarId} on account {AccountId}", avatarId, accountId);
            }

            return null;
        }

        private async Task SaveDisplayNameToCacheAsync(Guid accountId, DisplayName displayName)
        {
            var cache = GetAccountCache(accountId);
            
            // Update memory cache
            cache.AddOrUpdate(displayName.AvatarId, displayName, (key, existing) => displayName);

            // Update database cache with retry logic for race conditions
            var maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var context = CreateDbContext();
                    var existing = await context.DisplayNames
                        .FirstOrDefaultAsync(dn => dn.AccountId == accountId && dn.AvatarId == displayName.AvatarId);

                    if (existing != null)
                    {
                        // Update existing
                        existing.DisplayNameValue = displayName.DisplayNameValue;
                        existing.UserName = displayName.UserName;
                        existing.LegacyFirstName = displayName.LegacyFirstName;
                        existing.LegacyLastName = displayName.LegacyLastName;
                        existing.IsDefaultDisplayName = displayName.IsDefaultDisplayName;
                        existing.NextUpdate = displayName.NextUpdate;
                        existing.LastUpdated = displayName.LastUpdated;
                        existing.CachedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        // Create new
                        displayName.AccountId = accountId;
                        displayName.CachedAt = DateTime.UtcNow;
                        context.DisplayNames.Add(displayName);
                    }

                    await context.SaveChangesAsync();
                    return; // Success, exit the retry loop
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
                {
                    // Handle race condition - another thread inserted the same record
                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogDebug("Unique constraint violation for {AvatarId} on account {AccountId}, retrying... (attempt {Attempt})", 
                            displayName.AvatarId, accountId, attempt + 1);
                        
                        // Small delay before retry to avoid immediate collision
                        await Task.Delay(10 * (attempt + 1)); // 10ms, 20ms, 30ms delays
                        continue;
                    }
                    else
                    {
                        // Final attempt failed, log error but don't throw - memory cache is updated
                        _logger.LogWarning(dbEx, "Failed to save display name to database after {MaxRetries} attempts for {AvatarId} on account {AccountId}. Memory cache updated.", 
                            maxRetries, displayName.AvatarId, accountId);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving display name cache for {AvatarId} on account {AccountId}", displayName.AvatarId, accountId);
                    return; // Exit on other errors
                }
            }
        }

        public async Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            // First try the global cache
            try
            {
                var globalResult = await _globalCache.GetDisplayNameAsync(avatarId, mode, fallbackName);
                if (globalResult != "Loading..." && !string.IsNullOrEmpty(globalResult))
                {
                    return globalResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accessing global cache for {AvatarId}, falling back to account-specific cache", avatarId);
            }

            // Fallback to original account-specific logic
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? "Unknown User";
            }

            var cached = await GetCachedDisplayNameAsync(accountId, avatarId);
            
            if (cached != null)
            {
                // Check if we need to refresh
                if (DateTime.UtcNow > cached.NextUpdate)
                {
                    // Queue for refresh but return cached value immediately
                    _ = Task.Run(() => RefreshDisplayNameAsync(accountId, avatarId));
                }
                
                return FormatDisplayName(cached, mode);
            }

            // Not in cache, queue for fetching
            await QueueNameRequestAsync(accountId, avatarId);
            
            // If we have a fallback name, create a temporary cache entry to avoid repeated "Loading..."
            if (!string.IsNullOrWhiteSpace(fallbackName) && fallbackName != "Loading...")
            {
                try
                {
                    var parts = fallbackName.Trim().Split(' ');
                    var firstName = parts.Length > 0 ? parts[0] : "Unknown";
                    var lastName = parts.Length > 1 ? parts[1] : "Resident";
                    var userName = lastName == "Resident" ? firstName.ToLower() : $"{firstName}.{lastName}".ToLower();
                    
                    var tempDisplayName = new DisplayName
                    {
                        AvatarId = avatarId,
                        DisplayNameValue = fallbackName,
                        UserName = userName,
                        LegacyFirstName = firstName,
                        LegacyLastName = lastName,
                        IsDefaultDisplayName = true,
                        NextUpdate = DateTime.UtcNow.AddMinutes(5), // Short refresh for temp entries
                        LastUpdated = DateTime.UtcNow
                    };

                    await SaveDisplayNameToCacheAsync(accountId, tempDisplayName);
                    return FormatDisplayName(tempDisplayName, mode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create temporary cache entry for {AvatarId}", avatarId);
                }
            }
            
            // Return fallback name immediately instead of "Loading..."
            return fallbackName ?? "Loading...";
        }

        public async Task<string> GetLegacyNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
        {
            // First try the global cache
            try
            {
                var globalResult = await _globalCache.GetLegacyNameAsync(avatarId, fallbackName);
                if (globalResult != "Loading..." && !string.IsNullOrEmpty(globalResult))
                {
                    return globalResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accessing global cache for legacy name {AvatarId}", avatarId);
            }

            // Fallback to original account-specific logic
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName ?? "Unknown User";
            }

            var cached = await GetCachedDisplayNameAsync(accountId, avatarId);
            if (cached != null)
            {
                return cached.LegacyFullName;
            }

            await QueueNameRequestAsync(accountId, avatarId);
            
            // If we have a fallback name and no cache, try to use it immediately
            if (!string.IsNullOrWhiteSpace(fallbackName) && fallbackName != "Loading...")
            {
                return fallbackName;
            }
            
            return fallbackName ?? "Loading...";
        }

        public async Task<string> GetUserNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
        {
            // First try the global cache
            try
            {
                var globalResult = await _globalCache.GetUserNameAsync(avatarId, fallbackName);
                if (globalResult != "Loading..." && !string.IsNullOrEmpty(globalResult))
                {
                    return globalResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accessing global cache for username {AvatarId}", avatarId);
            }

            // Fallback to original account-specific logic
            if (string.IsNullOrEmpty(avatarId) || avatarId == UUID.Zero.ToString())
            {
                return fallbackName?.ToLower().Replace(" ", ".") ?? "unknown.user";
            }

            var cached = await GetCachedDisplayNameAsync(accountId, avatarId);
            if (cached != null)
            {
                return cached.UserName;
            }

            await QueueNameRequestAsync(accountId, avatarId);
            return fallbackName?.ToLower().Replace(" ", ".") ?? "loading...";
        }

        private string FormatDisplayName(DisplayName displayName, NameDisplayMode mode)
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

        private async Task QueueNameRequestAsync(Guid accountId, string avatarId)
        {
            var channel = _requestChannels.GetOrAdd(accountId, _ => 
                Channel.CreateUnbounded<string>());

            if (!await channel.Writer.WaitToWriteAsync())
            {
                return;
            }

            await channel.Writer.WriteAsync(avatarId);
            
            // Ensure processing task is running for this account
            EnsureProcessingTaskRunning(accountId);
        }

        private void EnsureProcessingTaskRunning(Guid accountId)
        {
            if (_processingTasks.ContainsKey(accountId))
                return;

            var cts = _cancellationTokens.GetOrAdd(accountId, _ => new CancellationTokenSource());
            var task = Task.Run(() => ProcessNameRequestsAsync(accountId, cts.Token), cts.Token);
            
            _processingTasks.TryAdd(accountId, task);
        }

        private async Task ProcessNameRequestsAsync(Guid accountId, CancellationToken cancellationToken)
        {
            if (!_requestChannels.TryGetValue(accountId, out var channel))
                return;

            var rateLimiter = _rateLimiters.GetOrAdd(accountId, _ => new SemaphoreSlim(_maxRequestsPerSecond, _burstSize));

            try
            {
                var reader = channel.Reader;
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Wait for requests to arrive
                    await reader.WaitToReadAsync(cancellationToken);
                    
                    // Collect batch of requests (like Radegast does)
                    var batchedRequests = new HashSet<string>();
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    
                    // Collect requests for up to 100ms or until we have max batch size (matches Radegast)
                    while (stopwatch.ElapsedMilliseconds < _batchWindow.TotalMilliseconds && 
                           batchedRequests.Count < _maxBatchSize &&
                           reader.TryRead(out var avatarId))
                    {
                        batchedRequests.Add(avatarId);
                    }
                    
                    // Add small delay to collect any remaining requests in the window
                    if (batchedRequests.Count > 0 && stopwatch.ElapsedMilliseconds < _batchWindow.TotalMilliseconds)
                    {
                        var remainingTime = _batchWindow - stopwatch.Elapsed;
                        if (remainingTime > TimeSpan.Zero)
                        {
                            await Task.Delay(remainingTime, cancellationToken);
                            
                            // Collect any additional requests that came in during the delay
                            while (batchedRequests.Count < _maxBatchSize && reader.TryRead(out var avatarId))
                            {
                                batchedRequests.Add(avatarId);
                            }
                        }
                    }

                    if (batchedRequests.Count == 0)
                        continue;

                    // Rate limiting (allow bursts like Radegast)
                    await rateLimiter.WaitAsync(cancellationToken);
                    
                    try
                    {
                        await FetchDisplayNamesFromGridAsync(accountId, batchedRequests.ToList());
                    }
                    finally
                    {
                        // Release rate limiter after delay
                        _ = Task.Delay(_rateLimitWindow, cancellationToken).ContinueWith(_ => 
                        {
                            if (!cancellationToken.IsCancellationRequested)
                                rateLimiter.Release();
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in name processing task for account {AccountId}", accountId);
            }
            finally
            {
                _processingTasks.TryRemove(accountId, out _);
            }
        }

        private async Task FetchDisplayNamesFromGridAsync(Guid accountId, List<string> avatarIds)
        {
            if (avatarIds.Count == 0) return;
            
            try
            {
                // Get the WebRadegastInstance for this account
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                var instance = accountService.GetInstance(accountId);
                
                if (instance?.Client?.Network?.Connected != true)
                {
                    _logger.LogWarning("Cannot fetch display names - account {AccountId} not connected", accountId);
                    return;
                }

                var client = instance.Client;
                var uuidList = avatarIds.Where(id => UUID.TryParse(id, out _))
                                      .Select(id => new UUID(id))
                                      .ToList();

                if (uuidList.Count == 0)
                    return;

                _logger.LogDebug("Fetching {Count} display names for account {AccountId}", uuidList.Count, accountId);

                // Check if display names are available on this grid
                if (client.Avatars.DisplayNamesAvailable())
                {
                    // Use TaskCompletionSource for better async handling (like Radegast)
                    var tcs = new TaskCompletionSource<bool>();
                    var timeout = TimeSpan.FromSeconds(10); // Radegast uses 10 seconds
                    
                    using var timeoutCts = new CancellationTokenSource(timeout);
                    timeoutCts.Token.Register(() => 
                    {
                        if (!tcs.Task.IsCompleted)
                        {
                            tcs.TrySetResult(false);
                        }
                    });

                    // Fetch display names with callback
                    try
                    {
                        await client.Avatars.GetDisplayNames(uuidList, (success, names, badIDs) =>
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    if (success && names?.Length > 0)
                                    {
                                        var displayNameDict = names.ToDictionary(n => n.ID, n => n);
                                        await UpdateDisplayNamesAsync(accountId, displayNameDict);
                                        _logger.LogDebug("Successfully updated {Count} display names for account {AccountId}", 
                                            names.Length, accountId);
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Failed to fetch display names for account {AccountId}: Success={Success}, Names={NameCount}, BadIDs={BadCount}", 
                                            accountId, success, names?.Length ?? 0, badIDs?.Length ?? 0);
                                        
                                        // Fall back to legacy names if display names failed
                                        if (badIDs?.Length > 0 || !success)
                                        {
                                            var legacyIds = badIDs?.ToList() ?? uuidList;
                                            await RequestLegacyNamesAsync(client, accountId, legacyIds);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error processing display name response for account {AccountId}", accountId);
                                }
                                finally
                                {
                                    tcs.TrySetResult(success);
                                }
                            });
                        });

                        await tcs.Task;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error calling GetDisplayNames for account {AccountId}", accountId);
                        // Fall back to legacy names
                        await RequestLegacyNamesAsync(client, accountId, uuidList);
                    }
                }
                else
                {
                    // Fall back to legacy names
                    await RequestLegacyNamesAsync(client, accountId, uuidList);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching display names for account {AccountId}", accountId);
            }
        }

        private async Task RequestLegacyNamesAsync(GridClient client, Guid accountId, List<UUID> uuidList)
        {
            try
            {
                var tcs = new TaskCompletionSource<bool>();
                var timeout = TimeSpan.FromSeconds(10);
                
                using var timeoutCts = new CancellationTokenSource(timeout);
                timeoutCts.Token.Register(() => 
                {
                    if (!tcs.Task.IsCompleted)
                    {
                        tcs.TrySetResult(false);
                    }
                });
                
                EventHandler<UUIDNameReplyEventArgs>? handler = null;
                handler = (sender, e) =>
                {
                    client.Avatars.UUIDNameReply -= handler;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await UpdateLegacyNamesAsync(accountId, e.Names);
                            _logger.LogDebug("Successfully updated {Count} legacy names for account {AccountId}", 
                                e.Names.Count, accountId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error updating legacy names for account {AccountId}", accountId);
                        }
                        finally
                        {
                            tcs.TrySetResult(true);
                        }
                    });
                };
                
                client.Avatars.UUIDNameReply += handler;
                client.Avatars.RequestAvatarNames(uuidList);
                
                await tcs.Task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting legacy names for account {AccountId}", accountId);
            }
        }

        public async Task<bool> UpdateDisplayNamesAsync(Guid accountId, Dictionary<UUID, AgentDisplayName> displayNames)
        {
            try
            {
                foreach (var kvp in displayNames)
                {
                    var agentDisplayName = kvp.Value;
                    var avatarId = agentDisplayName.ID.ToString();
                    
                    // Check if the display name is invalid (null, empty, or "Loading...")
                    var isInvalidDisplayName = IsInvalidNameValue(agentDisplayName.DisplayName);
                    
                    if (isInvalidDisplayName)
                    {
                        // Check if we have a cached version
                        var cachedName = await GetCachedDisplayNameAsync(accountId, avatarId);
                        if (cachedName != null)
                        {
                            // Use cached version, don't update
                            _logger.LogDebug("Received invalid display name for {AvatarId}, using cached version", avatarId);
                            continue;
                        }
                        else
                        {
                            // No cached version, fall back to legacy name
                            var legacyFullName = $"{agentDisplayName.LegacyFirstName} {agentDisplayName.LegacyLastName}";
                            var displayName = new DisplayName
                            {
                                AvatarId = avatarId,
                                DisplayNameValue = legacyFullName,
                                UserName = agentDisplayName.UserName,
                                LegacyFirstName = agentDisplayName.LegacyFirstName,
                                LegacyLastName = agentDisplayName.LegacyLastName,
                                IsDefaultDisplayName = true, // Mark as default since we're using legacy
                                NextUpdate = agentDisplayName.NextUpdate,
                                LastUpdated = DateTime.UtcNow
                            };

                            await SaveDisplayNameToCacheAsync(accountId, displayName);
                            _logger.LogDebug("Used legacy name for {AvatarId} due to invalid display name", avatarId);
                        }
                    }
                    else
                    {
                        // Valid display name received
                        var cachedName = await GetCachedDisplayNameAsync(accountId, avatarId);
                        
                        // Check if the new display name is different from cached one
                        if (cachedName == null || !cachedName.DisplayNameValue.Equals(agentDisplayName.DisplayName, StringComparison.Ordinal))
                        {
                            var oldDisplayName = cachedName?.DisplayNameValue ?? "";
                            
                            var displayName = new DisplayName
                            {
                                AvatarId = avatarId,
                                DisplayNameValue = agentDisplayName.DisplayName,
                                UserName = agentDisplayName.UserName,
                                LegacyFirstName = agentDisplayName.LegacyFirstName,
                                LegacyLastName = agentDisplayName.LegacyLastName,
                                IsDefaultDisplayName = agentDisplayName.IsDefaultDisplayName,
                                NextUpdate = agentDisplayName.NextUpdate,
                                LastUpdated = DateTime.UtcNow
                            };

                            await SaveDisplayNameToCacheAsync(accountId, displayName);
                            
                            // Fire display name changed event
                            DisplayNameChanged?.Invoke(this, new DisplayNameChangedEventArgs(avatarId, displayName));
                            
                            if (cachedName != null)
                            {
                                _logger.LogDebug("Updated display name for {AvatarId} from '{OldName}' to '{NewName}'", 
                                    avatarId, cachedName.DisplayNameValue, agentDisplayName.DisplayName);
                            }
                            else
                            {
                                _logger.LogDebug("Cached new display name for {AvatarId}: '{DisplayName}'", 
                                    avatarId, agentDisplayName.DisplayName);
                            }
                        }
                        else
                        {
                            // Display name hasn't changed, just update the timestamp
                            cachedName.LastUpdated = DateTime.UtcNow;
                            cachedName.NextUpdate = agentDisplayName.NextUpdate;
                            await SaveDisplayNameToCacheAsync(accountId, cachedName);
                            _logger.LogDebug("Display name for {AvatarId} unchanged, updated timestamp only", avatarId);
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating display names for account {AccountId}", accountId);
                return false;
            }
        }

        public async Task<bool> UpdateLegacyNamesAsync(Guid accountId, Dictionary<UUID, string> legacyNames)
        {
            try
            {
                foreach (var kvp in legacyNames)
                {
                    var avatarId = kvp.Key.ToString();
                    var fullName = kvp.Value;
                    
                    // Check if the legacy name is invalid (null, empty, or "Loading...")
                    var isInvalidLegacyName = IsInvalidNameValue(fullName);
                    
                    if (isInvalidLegacyName)
                    {
                        // Check if we have a cached version
                        var cachedName = await GetCachedDisplayNameAsync(accountId, avatarId);
                        if (cachedName != null)
                        {
                            // Use cached version, don't update
                            _logger.LogDebug("Received invalid legacy name for {AvatarId}, using cached version", avatarId);
                            continue;
                        }
                        else
                        {
                            // No cached version and invalid name, skip this entry
                            _logger.LogWarning("Received invalid legacy name for {AvatarId} with no cached fallback", avatarId);
                            continue;
                        }
                    }
                    
                    var parts = fullName.Trim().Split(' ');
                    
                    if (parts.Length >= 2)
                    {
                        var firstName = parts[0];
                        var lastName = parts[1];
                        var userName = lastName == "Resident" ? firstName.ToLower() : $"{firstName}.{lastName}".ToLower();
                        
                        // Check if we have a cached version and if it's different
                        var cachedName = await GetCachedDisplayNameAsync(accountId, avatarId);
                        
                        if (cachedName == null || !cachedName.LegacyFullName.Equals(fullName, StringComparison.Ordinal))
                        {
                            var oldDisplayName = cachedName?.DisplayNameValue ?? "";
                            
                            var displayName = new DisplayName
                            {
                                AvatarId = avatarId,
                                DisplayNameValue = fullName,
                                UserName = userName,
                                LegacyFirstName = firstName,
                                LegacyLastName = lastName,
                                IsDefaultDisplayName = true,
                                NextUpdate = DateTime.UtcNow.AddHours(24),
                                LastUpdated = DateTime.UtcNow
                            };

                            await SaveDisplayNameToCacheAsync(accountId, displayName);
                            
                            // Fire display name changed event for legacy name updates too
                            DisplayNameChanged?.Invoke(this, new DisplayNameChangedEventArgs(avatarId, displayName));
                            
                            if (cachedName != null)
                            {
                                _logger.LogDebug("Updated legacy name for {AvatarId} from '{OldName}' to '{NewName}'", 
                                    avatarId, cachedName.LegacyFullName, fullName);
                            }
                            else
                            {
                                _logger.LogDebug("Cached new legacy name for {AvatarId}: '{LegacyName}'", 
                                    avatarId, fullName);
                            }
                        }
                        else
                        {
                            // Legacy name hasn't changed, just update the timestamp
                            cachedName.LastUpdated = DateTime.UtcNow;
                            await SaveDisplayNameToCacheAsync(accountId, cachedName);
                            _logger.LogDebug("Legacy name for {AvatarId} unchanged, updated timestamp only", avatarId);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Invalid legacy name format for {AvatarId}: '{LegacyName}'", avatarId, fullName);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating legacy names for account {AccountId}", accountId);
                return false;
            }
        }

        public async Task RefreshDisplayNameAsync(Guid accountId, string avatarId)
        {
            await QueueNameRequestAsync(accountId, avatarId);
        }
        
        /// <summary>
        /// Proactively fetch display names for multiple avatars (e.g., when they enter the sim)
        /// This is more efficient than individual requests
        /// </summary>
        public async Task PreloadDisplayNamesAsync(Guid accountId, IEnumerable<string> avatarIds)
        {
            var uncachedIds = new List<string>();
            
            foreach (var avatarId in avatarIds)
            {
                var cached = await GetCachedDisplayNameAsync(accountId, avatarId);
                if (cached == null || DateTime.UtcNow > cached.NextUpdate)
                {
                    uncachedIds.Add(avatarId);
                }
            }
            
            if (uncachedIds.Count > 0)
            {
                _logger.LogDebug("Preloading {Count} display names for account {AccountId}", uncachedIds.Count, accountId);
                
                // Split into batches if needed
                const int batchSize = 100; // Radegast's max batch size
                for (int i = 0; i < uncachedIds.Count; i += batchSize)
                {
                    var batch = uncachedIds.Skip(i).Take(batchSize);
                    await FetchDisplayNamesFromGridAsync(accountId, batch.ToList());
                    
                    // Small delay between batches to avoid overwhelming the server
                    if (i + batchSize < uncachedIds.Count)
                    {
                        await Task.Delay(100);
                    }
                }
            }
        }

        public async Task CleanExpiredCacheAsync(Guid accountId)
        {
            try
            {
                using var context = CreateDbContext();
                var expiredDate = DateTime.UtcNow.Subtract(_cacheExpiry);
                
                var expiredNames = await context.DisplayNames
                    .Where(dn => dn.AccountId == accountId && dn.CachedAt < expiredDate)
                    .ToListAsync();

                if (expiredNames.Any())
                {
                    context.DisplayNames.RemoveRange(expiredNames);
                    await context.SaveChangesAsync();
                    
                    // Also remove from memory cache
                    var cache = GetAccountCache(accountId);
                    foreach (var expired in expiredNames)
                    {
                        cache.TryRemove(expired.AvatarId, out _);
                    }
                    
                    _logger.LogInformation("Cleaned {Count} expired display names for account {AccountId}", expiredNames.Count, accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning expired cache for account {AccountId}", accountId);
            }
        }

        public async Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId)
        {
            try
            {
                using var context = CreateDbContext();
                return await context.DisplayNames
                    .Where(dn => dn.AccountId == accountId)
                    .OrderByDescending(dn => dn.LastUpdated)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving cached names for account {AccountId}", accountId);
                return Enumerable.Empty<DisplayName>();
            }
        }

        /// <summary>
        /// Clean up resources for a specific account (call when account disconnects)
        /// </summary>
        public void CleanupAccount(Guid accountId)
        {
            try
            {
                // Cancel processing task
                if (_cancellationTokens.TryRemove(accountId, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                // Remove processing task
                _processingTasks.TryRemove(accountId, out _);

                // Close request channel
                if (_requestChannels.TryRemove(accountId, out var channel))
                {
                    channel.Writer.Complete();
                }

                // Dispose rate limiter
                if (_rateLimiters.TryRemove(accountId, out var rateLimiter))
                {
                    rateLimiter.Dispose();
                }

                // Clear memory cache for this account
                _nameCache.TryRemove(accountId, out _);

                _logger.LogDebug("Cleaned up display name service resources for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up account {AccountId} resources", accountId);
            }
        }
    }
}