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
    }

    public class ChatHistoryService : IChatHistoryService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ChatHistoryService> _logger;
        private readonly string _connectionString;

        public ChatHistoryService(IServiceProvider serviceProvider, ILogger<ChatHistoryService> logger, IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            
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
                        SessionName = m.SessionName
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
                
                var sessions = await context.ChatMessages
                    .Where(m => m.AccountId == accountId && !string.IsNullOrEmpty(m.SessionId) && m.SessionId != "local-chat")
                    .GroupBy(m => m.SessionId)
                    .Select(g => new 
                    {
                        SessionId = g.Key,
                        FirstMessage = g.OrderBy(m => m.Timestamp).First(),
                        LastActivity = g.Max(m => m.Timestamp)
                    })
                    .ToListAsync();

                var result = sessions.Select(s => new ChatSessionDto
                {
                    SessionId = s.SessionId ?? "",
                    SessionName = s.FirstMessage.SessionName ?? "Unknown",
                    ChatType = s.FirstMessage.ChatType ?? "IM",
                    TargetId = s.FirstMessage.TargetId ?? "",
                    LastActivity = s.LastActivity,
                    AccountId = accountId,
                    UnreadCount = 0,
                    IsActive = false
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
                var oldMessages = await context.ChatMessages
                    .Where(m => m.Timestamp < cutoffDate)
                    .ToListAsync();

                if (oldMessages.Any())
                {
                    context.ChatMessages.RemoveRange(oldMessages);
                    await context.SaveChangesAsync();
                    
                    _logger.LogInformation("Cleaned up {Count} old chat messages older than {CutoffDate}",
                        oldMessages.Count, cutoffDate);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old chat messages");
            }
        }
    }
}