using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RadegastWeb.Data;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    public interface IChatHistoryService
    {
        Task SaveChatMessageAsync(ChatMessageDto messageDto);
        Task<IEnumerable<ChatMessageDto>> GetChatHistoryAsync(Guid accountId, string sessionId, int count = 50, int skip = 0);
        Task<IEnumerable<ChatSessionDto>> GetRecentSessionsAsync(Guid accountId);
        Task CleanupOldMessagesAsync(TimeSpan olderThan);
        Task<bool> ClearChatHistoryAsync(Guid accountId, string sessionId);
        Task<IEnumerable<string>> GetSessionIdsForAccountAsync(Guid accountId);
        Task<IEnumerable<ChatMessageDto>> GetRecentChatForSessionAsync(Guid accountId, string sessionId);
        Task<IEnumerable<ChatMessageDto>> GetRecentLocalChatAsync(Guid accountId);
    }

    public class ChatHistoryService : IChatHistoryService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ChatHistoryService> _logger;
        private readonly IChatLogService _chatLogService;
        private readonly ISLTimeService _sltTimeService;
        private readonly string _connectionString;

        public ChatHistoryService(IServiceProvider serviceProvider, ILogger<ChatHistoryService> logger, IChatLogService chatLogService, ISLTimeService sltTimeService, IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _chatLogService = chatLogService;
            _sltTimeService = sltTimeService;
            
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

        public async Task SaveChatMessageAsync(ChatMessageDto messageDto)
        {
            try
            {
                using var context = CreateDbContext();
                
                var chatMessage = new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    AccountId = messageDto.AccountId,
                    SenderName = messageDto.SenderName,
                    Message = messageDto.Message,
                    ChatType = messageDto.ChatType,
                    Channel = messageDto.Channel,
                    Timestamp = messageDto.Timestamp,
                    RegionName = messageDto.RegionName,
                    SenderUuid = messageDto.SenderId,
                    SenderId = messageDto.SenderId,
                    TargetId = messageDto.TargetId,
                    SessionId = messageDto.SessionId ?? "local-chat",
                    SessionName = messageDto.SessionName
                };

                context.ChatMessages.Add(chatMessage);
                await context.SaveChangesAsync();
                
                // Also log to text files
                await _chatLogService.LogChatMessageAsync(messageDto);
                
                _logger.LogDebug("Saved chat message for account {AccountId}, session {SessionId}",
                    messageDto.AccountId, messageDto.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving chat message for account {AccountId}",
                    messageDto.AccountId);
            }
        }

        public async Task<IEnumerable<ChatMessageDto>> GetChatHistoryAsync(Guid accountId, string sessionId, int count = 50, int skip = 0)
        {
            try
            {
                using var context = CreateDbContext();
                
                var messages = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && m.SessionId == sessionId)
                    .OrderByDescending(m => m.Timestamp)
                    .Skip(skip)
                    .Take(count)
                    .Select(m => new ChatMessageDto
                    {
                        SenderName = m.SenderName,
                        Message = m.Message,
                        ChatType = m.ChatType,
                        Channel = m.Channel,
                        Timestamp = m.Timestamp,
                        RegionName = m.RegionName,
                        AccountId = m.AccountId,
                        SenderId = m.SenderId,
                        TargetId = m.TargetId,
                        SessionId = m.SessionId,
                        SessionName = m.SessionName,
                        SLTTime = _sltTimeService.FormatSLT(m.Timestamp, "HH:mm:ss"),
                        SLTDateTime = _sltTimeService.FormatSLTWithDate(m.Timestamp, "MMM dd, HH:mm:ss")
                    })
                    .ToListAsync();

                // Return in chronological order (oldest first)
                return messages.OrderBy(m => m.Timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving chat history for account {AccountId}, session {SessionId}",
                    accountId, sessionId);
                return Enumerable.Empty<ChatMessageDto>();
            }
        }

        public async Task<IEnumerable<ChatSessionDto>> GetRecentSessionsAsync(Guid accountId)
        {
            try
            {
                using var context = CreateDbContext();
                
                // MEMORY FIX: Limit session data to prevent excessive memory usage
                var sessions = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && !string.IsNullOrEmpty(m.SessionId) && m.SessionId != "local-chat")
                    .OrderByDescending(m => m.Timestamp)
                    .Take(1000) // Limit to most recent 1000 messages to prevent memory issues
                    .GroupBy(m => m.SessionId)
                    .Select(g => new 
                    {
                        SessionId = g.Key,
                        FirstMessage = g.OrderBy(m => m.Timestamp).First(),
                        LastActivity = g.Max(m => m.Timestamp)
                    })
                    .Take(50) // Limit to 50 most recent sessions
                    .ToListAsync();
                
                // Clear context to release memory immediately
                context.ChangeTracker.Clear();

                var result = sessions.Select(s => new ChatSessionDto
                {
                    SessionId = s.SessionId ?? "",
                    SessionName = s.FirstMessage.SessionName ?? "Unknown",
                    ChatType = s.FirstMessage.ChatType ?? "IM",
                    TargetId = s.FirstMessage.TargetId ?? "",
                    LastActivity = s.LastActivity,
                    AccountId = accountId,
                    UnreadCount = 0,
                    IsActive = false,
                    SLTLastActivity = _sltTimeService.FormatSLTWithDate(s.LastActivity, "MMM dd, HH:mm:ss")
                })
                .OrderByDescending(s => s.LastActivity)
                .Take(20) // Limit to most recent 20 sessions
                .ToList();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving recent sessions for account {AccountId}", accountId);
                return Enumerable.Empty<ChatSessionDto>();
            }
        }

        public async Task CleanupOldMessagesAsync(TimeSpan olderThan)
        {
            try
            {
                using var context = CreateDbContext();
                
                var cutoffDate = DateTime.UtcNow - olderThan;
                
                // MEMORY FIX: Use ExecuteDeleteAsync for efficient bulk deletion without loading into memory
                var deletedCount = await context.ChatMessages
                    .Where(m => m.Timestamp < cutoffDate)
                    .ExecuteDeleteAsync();

                if (deletedCount > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} old chat messages older than {CutoffDate}",
                        deletedCount, cutoffDate);
                }
                else
                {
                    _logger.LogDebug("No old chat messages found to clean up older than {CutoffDate}", cutoffDate);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old chat messages");
            }
        }

        public async Task<bool> ClearChatHistoryAsync(Guid accountId, string sessionId)
        {
            try
            {
                _logger.LogInformation("Attempting to clear chat history for account {AccountId}, session {SessionId}", accountId, sessionId);
                
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    _logger.LogWarning("SessionId is null or empty for account {AccountId}", accountId);
                    return false;
                }
                
                // Debug: List all session IDs for this account
                var allSessionIds = await GetSessionIdsForAccountAsync(accountId);
                
                using var context = CreateDbContext();
                
                // First, check if there are any messages to delete
                var messageCount = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && m.SessionId == sessionId)
                    .CountAsync();
                
                _logger.LogInformation("Found {MessageCount} messages to delete for account {AccountId}, session {SessionId}", 
                    messageCount, accountId, sessionId);
                
                if (messageCount == 0)
                {
                    _logger.LogInformation("No chat messages found to clear for account {AccountId}, session {SessionId} - this is considered successful",
                        accountId, sessionId);
                    return true; // Return true even if no messages to delete
                }
                
                var messagesToDelete = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && m.SessionId == sessionId)
                    .ToListAsync();

                if (messagesToDelete.Any())
                {
                    context.ChatMessages.RemoveRange(messagesToDelete);
                    await context.SaveChangesAsync();
                    
                    _logger.LogInformation("Successfully cleared {Count} chat messages for account {AccountId}, session {SessionId}",
                        messagesToDelete.Count, accountId, sessionId);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing chat history for account {AccountId}, session {SessionId}",
                    accountId, sessionId);
                return false;
            }
        }

        public async Task<IEnumerable<string>> GetSessionIdsForAccountAsync(Guid accountId)
        {
            try
            {
                using var context = CreateDbContext();
                
                var sessionIds = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && !string.IsNullOrEmpty(m.SessionId))
                    .Select(m => m.SessionId!)
                    .Distinct()
                    .ToListAsync();
                
                _logger.LogInformation("Found {Count} unique session IDs for account {AccountId}: {SessionIds}", 
                    sessionIds.Count, accountId, string.Join(", ", sessionIds));
                
                return sessionIds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting session IDs for account {AccountId}", accountId);
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Get recent chat history for any session type (last 48 hours or 1000 messages, whichever is smaller)
        /// MEMORY FIX: Query database directly instead of storing in memory
        /// </summary>
        public async Task<IEnumerable<ChatMessageDto>> GetRecentChatForSessionAsync(Guid accountId, string sessionId)
        {
            try
            {
                using var context = CreateDbContext();
                
                var cutoffTime = DateTime.UtcNow.AddHours(-48); // Last 48 hours
                
                var messages = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && 
                               m.SessionId == sessionId &&
                               m.Timestamp >= cutoffTime)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(1000) // Max 1000 messages
                    .Select(m => new ChatMessageDto
                    {
                        SenderName = m.SenderName,
                        Message = m.Message,
                        ChatType = m.ChatType,
                        Channel = m.Channel,
                        Timestamp = m.Timestamp,
                        RegionName = m.RegionName,
                        AccountId = m.AccountId,
                        SenderId = m.SenderId,
                        TargetId = m.TargetId,
                        SessionId = m.SessionId ?? sessionId,
                        SessionName = m.SessionName,
                        SLTTime = _sltTimeService.FormatSLT(m.Timestamp, "HH:mm:ss"),
                        SLTDateTime = _sltTimeService.FormatSLTWithDate(m.Timestamp, "MMM dd, HH:mm:ss")
                    })
                    .ToListAsync();

                // Clear context to release memory immediately
                context.ChangeTracker.Clear();

                // Return in chronological order (oldest first)
                return messages.OrderBy(m => m.Timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent chat for session {SessionId}, account {AccountId}", sessionId, accountId);
                return Enumerable.Empty<ChatMessageDto>();
            }
        }

        /// <summary>
        /// Get recent local chat history for an account (last 48 hours or 1000 messages, whichever is smaller)
        /// MEMORY FIX: Query database directly instead of storing in memory
        /// </summary>
        public async Task<IEnumerable<ChatMessageDto>> GetRecentLocalChatAsync(Guid accountId)
        {
            // Local chat uses "local-chat" as session ID, but sometimes it's stored as null/empty
            // So we need a special query for local chat that handles both cases
            try
            {
                using var context = CreateDbContext();
                
                var cutoffTime = DateTime.UtcNow.AddHours(-48); // Last 48 hours
                
                var messages = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && 
                               (m.SessionId == "local-chat" || string.IsNullOrEmpty(m.SessionId)) &&
                               m.Timestamp >= cutoffTime)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(1000) // Max 1000 messages
                    .Select(m => new ChatMessageDto
                    {
                        SenderName = m.SenderName,
                        Message = m.Message,
                        ChatType = m.ChatType,
                        Channel = m.Channel,
                        Timestamp = m.Timestamp,
                        RegionName = m.RegionName,
                        AccountId = m.AccountId,
                        SenderId = m.SenderId,
                        TargetId = m.TargetId,
                        SessionId = m.SessionId ?? "local-chat",
                        SessionName = m.SessionName,
                        SLTTime = _sltTimeService.FormatSLT(m.Timestamp, "HH:mm:ss"),
                        SLTDateTime = _sltTimeService.FormatSLTWithDate(m.Timestamp, "MMM dd, HH:mm:ss")
                    })
                    .ToListAsync();

                // Clear context to release memory immediately
                context.ChangeTracker.Clear();

                // Return in chronological order (oldest first)
                return messages.OrderBy(m => m.Timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent local chat for account {AccountId}", accountId);
                return Enumerable.Empty<ChatMessageDto>();
            }
        }
    }
}