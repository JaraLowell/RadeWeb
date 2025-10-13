using Microsoft.Extensions.Logging;
using RadegastWeb.Models;
using System.Text;
using System.IO;
using System.Globalization;

namespace RadegastWeb.Services
{
    public interface IChatLogService
    {
        Task LogChatMessageAsync(ChatMessageDto message);
        Task<string> GetChatLogPathAsync(Guid accountId, string chatType, string? sessionName = null);
        Task<IEnumerable<string>> GetChatLogFilesAsync(Guid accountId);
        Task<string> ReadChatLogAsync(Guid accountId, string logFileName, int maxLines = 100);
    }

    public class ChatLogService : IChatLogService
    {
        private readonly ILogger<ChatLogService> _logger;
        private readonly IDisplayNameService _displayNameService;
        private readonly string _dataRoot;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        public ChatLogService(
            ILogger<ChatLogService> logger, 
            IDisplayNameService displayNameService,
            IConfiguration configuration)
        {
            _logger = logger;
            _displayNameService = displayNameService;
            
            // Get the data root directory
            var contentRoot = configuration.GetValue<string>("ContentRoot") ?? Directory.GetCurrentDirectory();
            _dataRoot = Path.Combine(contentRoot, "data", "accounts");
            
            // Ensure the data directory exists
            if (!Directory.Exists(_dataRoot))
            {
                Directory.CreateDirectory(_dataRoot);
            }
        }

        public async Task LogChatMessageAsync(ChatMessageDto message)
        {
            try
            {
                // For IM messages, we need to get the legacy name for the file path
                string? sessionNameForFile = message.SessionName;
                if (message.ChatType.ToLower() == "im")
                {
                    if (!string.IsNullOrEmpty(message.SenderId))
                    {
                        try
                        {
                            var legacyName = await _displayNameService.GetLegacyNameAsync(
                                message.AccountId, 
                                message.SenderId, 
                                message.SenderName);
                            
                            if (!string.IsNullOrEmpty(legacyName))
                            {
                                sessionNameForFile = legacyName;
                            }
                            else
                            {
                                // Fallback to sender name if legacy name is not available
                                sessionNameForFile = message.SenderName;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error getting legacy name for IM file naming, using sender name as fallback");
                            sessionNameForFile = message.SenderName;
                        }
                    }
                    else
                    {
                        // If no SenderId, use the sender name
                        sessionNameForFile = message.SenderName;
                    }
                }
                
                // Get the appropriate log file path based on chat type
                var logFilePath = await GetChatLogPathAsync(message.AccountId, message.ChatType, sessionNameForFile);
                
                // Format the chat message
                var formattedMessage = await FormatChatMessageAsync(message);
                
                // Write to log file
                await WriteToLogFileAsync(logFilePath, formattedMessage);
                
                _logger.LogDebug("Logged chat message to {LogFile} for account {AccountId}", 
                    Path.GetFileName(logFilePath), message.AccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging chat message for account {AccountId}", message.AccountId);
            }
        }

        public Task<string> GetChatLogPathAsync(Guid accountId, string chatType, string? sessionName = null)
        {
            var accountLogsDir = Path.Combine(_dataRoot, accountId.ToString(), "logs");
            
            // Ensure the logs directory exists
            if (!Directory.Exists(accountLogsDir))
            {
                Directory.CreateDirectory(accountLogsDir);
            }

            string fileName;
            
            switch (chatType.ToLower())
            {
                case "normal":
                case "whisper":
                case "shout":
                case "system":
                case "starttyping":
                case "stoptyping":
                    // Local chat goes to chat.txt
                    fileName = "chat.txt";
                    break;
                    
                case "im":
                    // IM goes to <legacy name>.txt (always use legacy name for file naming)
                    if (!string.IsNullOrEmpty(sessionName))
                    {
                        var sanitizedName = SanitizeFileName(sessionName);
                        fileName = $"{sanitizedName}.txt";
                    }
                    else
                    {
                        fileName = "unknown_im.txt";
                    }
                    break;
                    
                case "group":
                case "conference":
                    // Group/Conference chat goes to <group name> (group).txt
                    if (!string.IsNullOrEmpty(sessionName))
                    {
                        var sanitizedName = SanitizeFileName(sessionName);
                        fileName = $"{sanitizedName}_(group).txt";
                    }
                    else
                    {
                        fileName = "unknown_group_(group).txt";
                    }
                    break;
                    
                case "notice":
                    // Notices go to a separate notices log
                    fileName = "notices.txt";
                    break;
                    
                default:
                    // For unknown types, try to use session name if available
                    if (!string.IsNullOrEmpty(sessionName))
                    {
                        var sanitizedName = SanitizeFileName(sessionName);
                        fileName = $"{sanitizedName}.txt";
                    }
                    else
                    {
                        fileName = "chat.txt"; // Fallback to local chat
                    }
                    break;
            }

            return Task.FromResult(Path.Combine(accountLogsDir, fileName));
        }

        public Task<IEnumerable<string>> GetChatLogFilesAsync(Guid accountId)
        {
            try
            {
                var accountLogsDir = Path.Combine(_dataRoot, accountId.ToString(), "logs");
                
                if (!Directory.Exists(accountLogsDir))
                {
                    return Task.FromResult(Enumerable.Empty<string>());
                }

                var logFiles = Directory.GetFiles(accountLogsDir, "*.txt")
                    .Select(f => Path.GetFileName(f))
                    .OrderBy(f => f)
                    .ToList();

                return Task.FromResult<IEnumerable<string>>(logFiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat log files for account {AccountId}", accountId);
                return Task.FromResult(Enumerable.Empty<string>());
            }
        }

        public async Task<string> ReadChatLogAsync(Guid accountId, string logFileName, int maxLines = 100)
        {
            try
            {
                var logFilePath = Path.Combine(_dataRoot, accountId.ToString(), "logs", logFileName);
                
                if (!File.Exists(logFilePath))
                {
                    return string.Empty;
                }

                var lines = new List<string>();
                
                await _fileLock.WaitAsync();
                try
                {
                    using var reader = new StreamReader(logFilePath, Encoding.UTF8);
                    string? line;
                    var allLines = new List<string>();
                    
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        allLines.Add(line);
                    }
                    
                    // Get the last maxLines
                    if (allLines.Count > maxLines)
                    {
                        lines = allLines.Skip(allLines.Count - maxLines).ToList();
                    }
                    else
                    {
                        lines = allLines;
                    }
                }
                finally
                {
                    _fileLock.Release();
                }

                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading chat log {LogFile} for account {AccountId}", 
                    logFileName, accountId);
                return string.Empty;
            }
        }

        private async Task<string> FormatChatMessageAsync(ChatMessageDto message)
        {
            var timestamp = message.Timestamp.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
            
            string senderName;
            
            // Try to get the display name if we have a SenderId
            if (!string.IsNullOrEmpty(message.SenderId))
            {
                try
                {
                    var displayName = await _displayNameService.GetDisplayNameAsync(
                        message.AccountId, 
                        message.SenderId, 
                        NameDisplayMode.Smart, 
                        message.SenderName);
                    
                    var legacyName = await _displayNameService.GetLegacyNameAsync(
                        message.AccountId, 
                        message.SenderId, 
                        message.SenderName);
                    
                    // If display name is different from legacy name, use format "DisplayName (legacy.name)"
                    // If same or no display name, use just "Legacy Name"
                    if (!string.IsNullOrEmpty(displayName) && 
                        !string.Equals(displayName, legacyName, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(legacyName))
                    {
                        senderName = $"{displayName} ({legacyName.Replace(' ', '.')})";
                    }
                    else if (!string.IsNullOrEmpty(legacyName))
                    {
                        senderName = legacyName;
                    }
                    else
                    {
                        senderName = message.SenderName;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error getting display name for {SenderId}, using fallback", 
                        message.SenderId);
                    senderName = message.SenderName;
                }
            }
            else
            {
                senderName = message.SenderName;
            }

            // Don't strip /me from the message - preserve it as specified
            var messageText = message.Message;

            return $"[{timestamp}]  {senderName}: {messageText}";
        }

        private async Task WriteToLogFileAsync(string filePath, string message)
        {
            await _fileLock.WaitAsync();
            try
            {
                using var writer = new StreamWriter(filePath, append: true, Encoding.UTF8);
                await writer.WriteLineAsync(message);
                await writer.FlushAsync();
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return "unknown";
            }
            
            // First, handle common problematic characters that appear in group names
            var result = fileName;
            
            // Remove or replace brackets, quotes, and other symbols
            result = result.Replace("{", "").Replace("}", "");
            result = result.Replace("[", "").Replace("]", "");
            result = result.Replace("(", "").Replace(")", "");
            result = result.Replace("\"", "").Replace("'", "");
            result = result.Replace("*", "").Replace("?", "");
            result = result.Replace("<", "").Replace(">", "");
            result = result.Replace("|", "-").Replace("\\", "-").Replace("/", "-");
            result = result.Replace(":", "-").Replace(";", "-");
            result = result.Replace("!", "").Replace("@", "at");
            result = result.Replace("#", "").Replace("$", "");
            result = result.Replace("%", "").Replace("^", "");
            result = result.Replace("&", "and").Replace("+", "plus");
            result = result.Replace("=", "").Replace("~", "");
            result = result.Replace("`", "").Replace("´", "");
            
            // Remove any remaining invalid file name characters
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new StringBuilder();
            
            foreach (char c in result)
            {
                if (invalidChars.Contains(c))
                {
                    // Skip invalid characters entirely or replace with underscore
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '.')
                    {
                        sanitized.Append(c);
                    }
                    else
                    {
                        sanitized.Append('_');
                    }
                }
                else
                {
                    sanitized.Append(c);
                }
            }
            
            result = sanitized.ToString();
            
            // Convert multiple spaces to single spaces, then spaces to underscores
            while (result.Contains("  "))
            {
                result = result.Replace("  ", " ");
            }
            // result = result.Replace(' ', '_');
            
            // Remove multiple consecutive underscores and dashes
            while (result.Contains("__"))
            {
                result = result.Replace("__", "_");
            }
            while (result.Contains("--"))
            {
                result = result.Replace("--", "-");
            }
            
            // Remove leading/trailing special characters
            result = result.Trim('_', '-', '.');
            
            // Ensure it's not too long (leave room for " (group).txt" suffix)
            if (result.Length > 180)
            {
                result = result.Substring(0, 180);
                result = result.Trim('_', '-', '.');
            }
            
            // Ensure it's not empty after all the cleaning
            if (string.IsNullOrEmpty(result))
            {
                result = "unknown";
            }
            
            // Make it lowercase for consistency
            return result.ToLowerInvariant();
        }
    }
}