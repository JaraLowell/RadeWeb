using RadegastWeb.Core;
using RadegastWeb.Models;
using RadegastWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public interface IAccountService
    {
        Task<Account> CreateAccountAsync(Account account);
        Task<IEnumerable<Account>> GetAccountsAsync();
        Task<Account?> GetAccountAsync(Guid id);
        Task<bool> DeleteAccountAsync(Guid id);
        Task<bool> LoginAccountAsync(Guid id);
        Task<bool> LogoutAccountAsync(Guid id);
        Task<IEnumerable<AccountStatus>> GetAccountStatusesAsync();
        WebRadegastInstance? GetInstance(Guid accountId);
        Task<bool> SendChatAsync(Guid accountId, string message, string chatType = "Normal", int channel = 0);
        Task<bool> SendIMAsync(Guid accountId, string targetId, string message);
        Task<bool> SendGroupIMAsync(Guid accountId, string groupId, string message);
        Task<IEnumerable<AvatarDto>> GetNearbyAvatarsAsync(Guid accountId);
        Task<IEnumerable<ChatSessionDto>> GetChatSessionsAsync(Guid accountId);
        Task LoadAccountsAsync();
        Task EnsureTestAccountAsync();
        Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        Task<IEnumerable<DisplayName>> GetCachedDisplayNamesAsync(Guid accountId);
        Task AcknowledgeNoticeAsync(Guid accountId, string noticeId);
        Task DismissNoticeAsync(Guid accountId, string noticeId);
        Task<IEnumerable<NoticeDto>> GetRecentNoticesAsync(Guid accountId, int count = 20);
        Task<int> GetUnreadNoticesCountAsync(Guid accountId);
        Task<RegionStatsDto?> GetRegionStatsAsync(Guid accountId);
        Task ResetAllAccountsToOfflineAsync();
    }

    public class AccountService : IAccountService, IDisposable
    {
        private readonly ILogger<AccountService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _connectionString;
        private readonly ConcurrentDictionary<Guid, Account> _accounts = new();
        private readonly ConcurrentDictionary<Guid, WebRadegastInstance> _instances = new();
        private readonly IPeriodicDisplayNameService _periodicDisplayNameService;
        private bool _disposed;

        public AccountService(ILogger<AccountService> logger, IServiceProvider serviceProvider, IConfiguration configuration, IPeriodicDisplayNameService periodicDisplayNameService)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _periodicDisplayNameService = periodicDisplayNameService;
            
            // Get the connection string from configuration or build it
            var contentRoot = configuration.GetValue<string>("ContentRoot") ?? Directory.GetCurrentDirectory();
            var dataDirectory = Path.Combine(contentRoot, "data");
            if (!Directory.Exists(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
            }
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

        public async Task LoadAccountsAsync()
        {
            try
            {
                using var context = CreateDbContext();
                var accounts = await context.Accounts.ToListAsync();
                
                _accounts.Clear();
                foreach (var account in accounts)
                {
                    _accounts.TryAdd(account.Id, account);
                }
                
                _logger.LogInformation("Loaded {Count} accounts from database", accounts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading accounts from database");
            }
        }

        public async Task ResetAllAccountsToOfflineAsync()
        {
            try
            {
                using var context = CreateDbContext();
                
                // Update all accounts in database to offline status
                var accounts = await context.Accounts.ToListAsync();
                var updateCount = 0;
                
                foreach (var account in accounts)
                {
                    if (account.IsConnected || account.Status != "Offline")
                    {
                        account.IsConnected = false;
                        account.Status = "Offline";
                        updateCount++;
                    }
                }
                
                if (updateCount > 0)
                {
                    await context.SaveChangesAsync();
                    _logger.LogInformation("Reset {Count} accounts to offline status", updateCount);
                }
                else
                {
                    _logger.LogInformation("All accounts were already offline");
                }
                
                // Also update in-memory accounts
                foreach (var kvp in _accounts)
                {
                    kvp.Value.IsConnected = false;
                    kvp.Value.Status = "Offline";
                }
                
                // Clear any lingering instances (shouldn't be any at startup, but just in case)
                foreach (var instance in _instances.Values)
                {
                    try
                    {
                        instance.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing instance during reset");
                    }
                }
                _instances.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting accounts to offline status");
            }
        }

        public async Task<Account> CreateAccountAsync(Account account)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(account.DisplayName))
                {
                    account.DisplayName = $"{account.FirstName} {account.LastName}";
                }

                using var context = CreateDbContext();
                context.Accounts.Add(account);
                await context.SaveChangesAsync();

                _accounts.TryAdd(account.Id, account);
                _logger.LogInformation("Created account {AccountId} for {DisplayName}", account.Id, account.DisplayName);
                
                return account;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating account for {DisplayName}", account.DisplayName);
                throw;
            }
        }

        public Task<IEnumerable<Account>> GetAccountsAsync()
        {
            return Task.FromResult(_accounts.Values.AsEnumerable());
        }

        public Task<Account?> GetAccountAsync(Guid id)
        {
            _accounts.TryGetValue(id, out var account);
            return Task.FromResult(account);
        }

        public async Task<bool> DeleteAccountAsync(Guid id)
        {
            try
            {
                // First logout and dispose instance if it exists
                if (_instances.TryRemove(id, out var instance))
                {
                    instance.Dispose();
                }

                // Remove from database
                using var context = CreateDbContext();
                var account = await context.Accounts.FindAsync(id);
                if (account != null)
                {
                    context.Accounts.Remove(account);
                    await context.SaveChangesAsync();
                }

                // Remove from memory
                var result = _accounts.TryRemove(id, out _);
                if (result)
                {
                    _logger.LogInformation("Deleted account {AccountId}", id);
                    
                    // Clean up account directories
                    try
                    {
                        var accountDir = Path.Combine("data", "accounts", id.ToString());
                        if (Directory.Exists(accountDir))
                        {
                            Directory.Delete(accountDir, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete account directory for {AccountId}", id);
                    }
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting account {AccountId}", id);
                return false;
            }
        }

        public async Task<bool> LoginAccountAsync(Guid id)
        {
            if (!_accounts.TryGetValue(id, out var account))
            {
                _logger.LogWarning("Attempted to login non-existent account {AccountId}", id);
                return false;
            }

            // If already have an instance, dispose it first
            if (_instances.TryRemove(id, out var existingInstance))
            {
                existingInstance.Dispose();
            }

            try
            {
                // Create new instance with isolated logger
                using var scope = _serviceProvider.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<WebRadegastInstance>>();
                var displayNameService = scope.ServiceProvider.GetRequiredService<IDisplayNameService>();
                var noticeService = scope.ServiceProvider.GetRequiredService<INoticeService>();
                var urlParser = scope.ServiceProvider.GetRequiredService<ISlUrlParser>();
                var nameResolutionService = scope.ServiceProvider.GetRequiredService<INameResolutionService>();
                var groupService = scope.ServiceProvider.GetRequiredService<IGroupService>();
                var globalDisplayNameCache = scope.ServiceProvider.GetRequiredService<IGlobalDisplayNameCache>();
                var statsService = scope.ServiceProvider.GetRequiredService<IStatsService>();
                var corradeService = scope.ServiceProvider.GetRequiredService<ICorradeService>();
                var aiChatService = scope.ServiceProvider.GetRequiredService<IAiChatService>();
                var chatHistoryService = scope.ServiceProvider.GetRequiredService<IChatHistoryService>();
                
                var instance = new WebRadegastInstance(account, logger, displayNameService, noticeService, urlParser, nameResolutionService, groupService, globalDisplayNameCache, statsService, corradeService, aiChatService, chatHistoryService);
                
                var loginResult = await instance.LoginAsync();
                
                if (loginResult)
                {
                    _instances.TryAdd(id, instance);
                    account.IsConnected = true;
                    account.LastLoginAt = DateTime.UtcNow;
                    
                    // Subscribe to display name changes for this account to update the database
                    instance.StatusChanged += async (sender, status) =>
                    {
                        if (sender is WebRadegastInstance webInstance && webInstance.AccountInfo.DisplayName != account.DisplayName)
                        {
                            account.DisplayName = webInstance.AccountInfo.DisplayName;
                            try
                            {
                                using var context = CreateDbContext();
                                var dbAccount = await context.Accounts.FindAsync(id);
                                if (dbAccount != null)
                                {
                                    dbAccount.DisplayName = account.DisplayName;
                                    await context.SaveChangesAsync();
                                    _logger.LogDebug("Updated account display name in database: {DisplayName}", account.DisplayName);
                                }
                            }
                            catch (Exception dbEx)
                            {
                                _logger.LogWarning(dbEx, "Failed to update account display name in database");
                            }
                        }
                    };
                    
                    // Subscribe specifically to our own display name changes for immediate database updates
                    instance.OwnDisplayNameChanged += async (sender, newDisplayName) =>
                    {
                        account.DisplayName = newDisplayName;
                        try
                        {
                            using var context = CreateDbContext();
                            var dbAccount = await context.Accounts.FindAsync(id);
                            if (dbAccount != null)
                            {
                                dbAccount.DisplayName = newDisplayName;
                                await context.SaveChangesAsync();
                                _logger.LogInformation("Updated account {AccountId} display name in database to: '{DisplayName}'", id, newDisplayName);
                            }
                        }
                        catch (Exception dbEx)
                        {
                            _logger.LogError(dbEx, "Failed to update account {AccountId} display name in database to '{DisplayName}'", id, newDisplayName);
                        }
                    };
                    
                    // Update the database
                    try
                    {
                        using var context = CreateDbContext();
                        var dbAccount = await context.Accounts.FindAsync(id);
                        if (dbAccount != null)
                        {
                            dbAccount.IsConnected = true;
                            dbAccount.LastLoginAt = DateTime.UtcNow;
                            await context.SaveChangesAsync();
                        }
                    }
                    catch (Exception dbEx)
                    {
                        _logger.LogWarning(dbEx, "Failed to update database for account {AccountId} login", id);
                    }
                    
                    _logger.LogInformation("Successfully logged in account {AccountId}", id);
                    
                    // Force a status update to ensure immediate broadcast
                    // Wait a moment for the LibreMetaverse client to fully initialize
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000); // Wait 2 seconds
                        if (_instances.TryGetValue(id, out var inst) && inst.IsConnected)
                        {
                            // Trigger a status change event to ensure SignalR broadcast
                            inst.TriggerStatusUpdate();
                            
                            // Start region stats periodic updates
                            try
                            {
                                using var scope = _serviceProvider.CreateScope();
                                var regionInfoService = scope.ServiceProvider.GetRequiredService<IRegionInfoService>();
                                await regionInfoService.StartPeriodicUpdatesAsync(id);
                                _logger.LogInformation("Started region stats updates for account {AccountId}", id);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to start region stats updates for account {AccountId}", id);
                            }
                            
                            // Register account for periodic display name processing
                            try
                            {
                                _periodicDisplayNameService.RegisterAccount(id);
                                _logger.LogInformation("Registered account {AccountId} for periodic display name processing", id);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to register account {AccountId} for periodic display name processing", id);
                            }
                        }
                    });
                    
                    return true;
                }
                else
                {
                    instance.Dispose();
                    _logger.LogWarning("Failed to login account {AccountId}", id);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during login for account {AccountId}", id);
                return false;
            }
        }

        public async Task<bool> LogoutAccountAsync(Guid id)
        {
            if (!_instances.TryRemove(id, out var instance))
            {
                _logger.LogWarning("Attempted to logout non-connected account {AccountId}", id);
                return false;
            }

            try
            {
                await instance.DisconnectAsync();
                instance.Dispose();
                
                if (_accounts.TryGetValue(id, out var account))
                {
                    account.IsConnected = false;
                    account.Status = "Offline";
                    
                    // Update the database
                    try
                    {
                        using var context = CreateDbContext();
                        var dbAccount = await context.Accounts.FindAsync(id);
                        if (dbAccount != null)
                        {
                            dbAccount.IsConnected = false;
                            dbAccount.Status = "Offline";
                            await context.SaveChangesAsync();
                        }
                    }
                    catch (Exception dbEx)
                    {
                        _logger.LogWarning(dbEx, "Failed to update database for account {AccountId} logout", id);
                    }
                }
                
                // Stop region stats updates and cleanup
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var regionInfoService = scope.ServiceProvider.GetRequiredService<IRegionInfoService>();
                    await regionInfoService.StopPeriodicUpdatesAsync(id);
                    regionInfoService.CleanupAccount(id);
                    _logger.LogInformation("Stopped region stats updates for account {AccountId}", id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stop region stats updates for account {AccountId}", id);
                }
                
                // Unregister account from periodic display name processing
                try
                {
                    _periodicDisplayNameService.UnregisterAccount(id);
                    _logger.LogInformation("Unregistered account {AccountId} from periodic display name processing", id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to unregister account {AccountId} from periodic display name processing", id);
                }
                
                _logger.LogInformation("Successfully logged out account {AccountId}", id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during logout for account {AccountId}", id);
                instance.Dispose(); // Ensure cleanup
                return false;
            }
        }

        public Task<IEnumerable<AccountStatus>> GetAccountStatusesAsync()
        {
            // Get presence service to check current presence status
            var presenceService = _serviceProvider.GetService<IPresenceService>();
            
            var statuses = _accounts.Values.Select(account => 
            {
                var status = account.Status;
                
                // If connected, get the actual presence status
                if (account.IsConnected && presenceService != null)
                {
                    var presenceStatus = presenceService.GetAccountStatus(account.Id);
                    var newStatus = presenceStatus switch
                    {
                        PresenceStatus.Away => "Away",
                        PresenceStatus.Busy => "Busy",
                        _ => "Online"
                    };
                    
                    if (newStatus != status)
                    {
                        _logger.LogInformation("Account {AccountId}: status updated from '{OldStatus}' to '{NewStatus}' based on presence", 
                            account.Id, status, newStatus);
                    }
                    
                    status = newStatus;
                }
                
                return new AccountStatus
                {
                    AccountId = account.Id,
                    FirstName = account.FirstName,
                    LastName = account.LastName,
                    DisplayName = account.DisplayName,
                    IsConnected = account.IsConnected,
                    Status = status,
                    CurrentRegion = account.CurrentRegion,
                    LastLoginAt = account.LastLoginAt,
                    AvatarUuid = account.AvatarUuid,
                    GridUrl = account.GridUrl
                };
            });

            return Task.FromResult(statuses);
        }

        public WebRadegastInstance? GetInstance(Guid accountId)
        {
            _instances.TryGetValue(accountId, out var instance);
            return instance;
        }

        public Task<bool> SendChatAsync(Guid accountId, string message, string chatType = "Normal", int channel = 0)
        {
            if (!_instances.TryGetValue(accountId, out var instance))
            {
                _logger.LogWarning("Attempted to send chat for non-connected account {AccountId}", accountId);
                return Task.FromResult(false);
            }

            try
            {
                var openMetaverseChatType = chatType.ToLower() switch
                {
                    "whisper" => OpenMetaverse.ChatType.Whisper,
                    "shout" => OpenMetaverse.ChatType.Shout,
                    "normal" => OpenMetaverse.ChatType.Normal,
                    _ => OpenMetaverse.ChatType.Normal
                };

                instance.SendChat(message, openMetaverseChatType, channel);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending chat for account {AccountId}", accountId);
                return Task.FromResult(false);
            }
        }

        public Task<bool> SendIMAsync(Guid accountId, string targetId, string message)
        {
            if (!_instances.TryGetValue(accountId, out var instance))
            {
                _logger.LogWarning("Attempted to send IM for non-connected account {AccountId}", accountId);
                return Task.FromResult(false);
            }

            try
            {
                instance.SendIM(targetId, message);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending IM for account {AccountId}", accountId);
                return Task.FromResult(false);
            }
        }

        public async Task<IEnumerable<AvatarDto>> GetNearbyAvatarsAsync(Guid accountId)
        {
            if (!_instances.TryGetValue(accountId, out var instance))
            {
                return Enumerable.Empty<AvatarDto>();
            }

            try
            {
                var avatars = await instance.GetNearbyAvatarsAsync();
                return avatars;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting nearby avatars for account {AccountId}", accountId);
                return Enumerable.Empty<AvatarDto>();
            }
        }

        public Task<bool> SendGroupIMAsync(Guid accountId, string groupId, string message)
        {
            if (!_instances.TryGetValue(accountId, out var instance))
            {
                _logger.LogWarning("Attempted to send group IM for non-connected account {AccountId}", accountId);
                return Task.FromResult(false);
            }

            try
            {
                instance.SendGroupIM(groupId, message);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending group IM for account {AccountId}", accountId);
                return Task.FromResult(false);
            }
        }

        public Task<IEnumerable<ChatSessionDto>> GetChatSessionsAsync(Guid accountId)
        {
            if (!_instances.TryGetValue(accountId, out var instance))
            {
                return Task.FromResult(Enumerable.Empty<ChatSessionDto>());
            }

            try
            {
                var sessions = instance.GetChatSessions();
                return Task.FromResult(sessions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat sessions for account {AccountId}", accountId);
                return Task.FromResult(Enumerable.Empty<ChatSessionDto>());
            }
        }

        public async Task EnsureTestAccountAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                using var context = scope.ServiceProvider.GetRequiredService<RadegastDbContext>();

                var testAccountId = Guid.Parse("157c4227-e715-49c5-a6c5-0863ce66cf6c");
                var existingAccount = await context.Accounts.FindAsync(testAccountId);
                
                if (existingAccount == null)
                {
                    var testAccount = new Account
                    {
                        Id = testAccountId,
                        FirstName = "Test",
                        LastName = "User",
                        Password = "password123",
                        DisplayName = "Test User",
                        GridUrl = "https://grid.agni.lindenlab.com:443/cgi-bin/login.cgi",
                        IsConnected = false,
                        CreatedAt = DateTime.UtcNow,
                        Status = "Offline"
                    };

                    context.Accounts.Add(testAccount);
                    await context.SaveChangesAsync();
                    _logger.LogInformation("Test account created successfully");
                    
                    // Add to in-memory cache
                    _accounts.TryAdd(testAccount.Id, testAccount);
                }
                else
                {
                    _logger.LogInformation("Test account already exists");
                    _accounts.TryAdd(existingAccount.Id, existingAccount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring test account exists");
            }
        }

        public async Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var displayNameService = scope.ServiceProvider.GetRequiredService<IDisplayNameService>();
                return await displayNameService.GetDisplayNameAsync(accountId, avatarId, mode, fallbackName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting display name for {AvatarId} on account {AccountId}", avatarId, accountId);
                return fallbackName ?? "Unknown User";
            }
        }

        public async Task<IEnumerable<DisplayName>> GetCachedDisplayNamesAsync(Guid accountId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var displayNameService = scope.ServiceProvider.GetRequiredService<IDisplayNameService>();
                return await displayNameService.GetCachedNamesAsync(accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cached display names for account {AccountId}", accountId);
                return Enumerable.Empty<DisplayName>();
            }
        }

        public async Task AcknowledgeNoticeAsync(Guid accountId, string noticeId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var noticeService = scope.ServiceProvider.GetRequiredService<INoticeService>();
                await noticeService.AcknowledgeNoticeAsync(accountId, noticeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acknowledging notice {NoticeId} for account {AccountId}", noticeId, accountId);
            }
        }

        public async Task DismissNoticeAsync(Guid accountId, string noticeId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var noticeService = scope.ServiceProvider.GetRequiredService<INoticeService>();
                await noticeService.DismissNoticeAsync(accountId, noticeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dismissing notice {NoticeId} for account {AccountId}", noticeId, accountId);
            }
        }

        public async Task<IEnumerable<NoticeDto>> GetRecentNoticesAsync(Guid accountId, int count = 20)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var noticeService = scope.ServiceProvider.GetRequiredService<INoticeService>();
                return await noticeService.GetRecentNoticesAsync(accountId, count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent notices for account {AccountId}", accountId);
                return Enumerable.Empty<NoticeDto>();
            }
        }

        public async Task<int> GetUnreadNoticesCountAsync(Guid accountId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var noticeService = scope.ServiceProvider.GetRequiredService<INoticeService>();
                var unreadNotices = await noticeService.GetUnreadNoticesAsync(accountId);
                return unreadNotices.Count();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting unread notices count for account {AccountId}", accountId);
                return 0;
            }
        }

        public async Task<RegionStatsDto?> GetRegionStatsAsync(Guid accountId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var regionInfoService = scope.ServiceProvider.GetRequiredService<IRegionInfoService>();
                return await regionInfoService.GetRegionStatsAsync(accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting region stats for account {AccountId}", accountId);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            foreach (var instance in _instances.Values)
            {
                try
                {
                    instance.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing instance during service cleanup");
                }
            }

            _instances.Clear();
            _disposed = true;
        }
    }
}