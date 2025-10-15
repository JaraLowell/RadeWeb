using Microsoft.Extensions.Configuration;
using RadegastWeb.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for handling AI chat bot functionality
    /// </summary>
    public class AiChatService : IAiChatService
    {
        private readonly ILogger<AiChatService> _logger;
        private readonly IAccountService _accountService;
        private readonly IChatHistoryService _chatHistoryService;
        private readonly HttpClient _httpClient;
        private readonly string _configPath;
        private AiBotConfig? _cachedConfig;
        private readonly object _configLock = new();
        private DateTime _configLastLoaded = DateTime.MinValue;
        private static readonly TimeSpan ConfigCacheTimeout = TimeSpan.FromMinutes(5);
        private readonly Random _random = new();

        public AiChatService(
            ILogger<AiChatService> logger,
            IAccountService accountService,
            IChatHistoryService chatHistoryService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _accountService = accountService;
            _chatHistoryService = chatHistoryService;
            _httpClient = httpClientFactory.CreateClient();
            
            // Get the data directory path from configuration or use default
            var dataDirectory = configuration.GetValue<string>("DataDirectory") ?? "data";
            _configPath = Path.Combine(dataDirectory, "aibot.json");
            
            // Initialize configuration
            _ = Task.Run(LoadConfigurationAsync);
        }

        public bool IsEnabled
        {
            get
            {
                var config = GetCachedConfiguration();
                return config?.Enabled == true && 
                       !string.IsNullOrEmpty(config.AvatarName) && 
                       !string.IsNullOrEmpty(config.ApiConfig.ApiKey) &&
                       !string.IsNullOrEmpty(config.SystemPrompt);
            }
        }

        public async Task<bool> ShouldRespondAsync(ChatMessageDto message)
        {
            if (!IsEnabled)
                return false;

            var config = GetCachedConfiguration();
            if (config == null || string.IsNullOrEmpty(config.AvatarName))
                return false;

            // Find the account that matches the configured avatar name
            var accounts = await _accountService.GetAccountsAsync();
            var targetAccount = accounts.FirstOrDefault(a => 
                $"{a.FirstName} {a.LastName}".Equals(config.AvatarName, StringComparison.OrdinalIgnoreCase));

            if (targetAccount == null || targetAccount.Id != message.AccountId)
                return false;

            // Only respond to local chat (not IMs, groups, or whispers)
            if (message.ChatType.ToLower() != "normal")
                return false;

            // Don't respond to our own messages
            var accountInstance = _accountService.GetInstance(message.AccountId);
            if (accountInstance != null && message.SenderName == accountInstance.Client.Self.Name)
                return false;

            // Check ignore lists (UUID is more secure than name)
            if (!string.IsNullOrEmpty(message.SenderId) && 
                config.ResponseConfig.IgnoreUuids.Any(uuid => 
                    string.Equals(uuid, message.SenderId, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("Ignoring message from UUID {SenderId} (in ignore UUID list)", message.SenderId);
                return false;
            }

            // Also check name-based ignore list for legacy support
            if (config.ResponseConfig.IgnoreNames.Any(name => 
                string.Equals(name, message.SenderName, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("Ignoring message from {SenderName} (in ignore name list)", message.SenderName);
                return false;
            }

            // Check response probability
            if (_random.NextDouble() > config.ResponseConfig.ResponseProbability)
                return false;

            // Check for trigger conditions
            var shouldRespond = false;

            // Check for name mentions
            if (config.ResponseConfig.RespondToNameMentions && accountInstance != null)
            {
                var botName = accountInstance.Client.Self.Name;
                if (message.Message.Contains(botName, StringComparison.OrdinalIgnoreCase))
                    shouldRespond = true;
            }

            // Check for questions
            if (config.ResponseConfig.RespondToQuestions && message.Message.TrimEnd().EndsWith('?'))
                shouldRespond = true;

            // Check for trigger keywords
            if (config.ResponseConfig.TriggerKeywords.Any(keyword => 
                message.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                shouldRespond = true;

            return shouldRespond;
        }

        public async Task<string?> GenerateResponseAsync(ChatMessageDto message, IEnumerable<ChatMessageDto> chatHistory)
        {
            if (!IsEnabled)
                return null;

            var config = GetCachedConfiguration();
            if (config == null)
                return null;

            try
            {
                var messages = BuildChatMessages(message, chatHistory, config);
                var response = await CallAiApiAsync(messages, config.ApiConfig);

                if (response.Success && !string.IsNullOrEmpty(response.Content))
                {
                    // Truncate response if too long
                    var content = response.Content;
                    if (content.Length > config.ResponseConfig.MaxResponseLength)
                    {
                        content = content.Substring(0, config.ResponseConfig.MaxResponseLength - 3) + "...";
                    }

                    return content;
                }

                _logger.LogWarning("AI API call failed: {Error}", response.ErrorMessage);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating AI response");
                return null;
            }
        }

        public async Task<string?> ProcessChatMessageAsync(ChatMessageDto message, IEnumerable<ChatMessageDto> chatHistory)
        {
            if (!await ShouldRespondAsync(message))
                return null;

            var config = GetCachedConfiguration();
            if (config == null)
                return null;

            // Add random delay to make responses more natural
            var delay = _random.Next(
                config.ResponseConfig.MinResponseDelaySeconds * 1000,
                config.ResponseConfig.MaxResponseDelaySeconds * 1000);
            
            await Task.Delay(delay);

            return await GenerateResponseAsync(message, chatHistory);
        }

        public async Task ReloadConfigurationAsync()
        {
            await LoadConfigurationAsync();
        }

        public AiBotConfig? GetConfiguration()
        {
            return GetCachedConfiguration();
        }

        private AiBotConfig? GetCachedConfiguration()
        {
            lock (_configLock)
            {
                if (_cachedConfig == null || DateTime.UtcNow - _configLastLoaded > ConfigCacheTimeout)
                {
                    _ = Task.Run(LoadConfigurationAsync);
                }
                return _cachedConfig;
            }
        }

        private async Task LoadConfigurationAsync()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _logger.LogDebug("AI bot configuration file not found at {ConfigPath}", _configPath);
                    lock (_configLock)
                    {
                        _cachedConfig = null;
                        _configLastLoaded = DateTime.UtcNow;
                    }
                    return;
                }

                var json = await File.ReadAllTextAsync(_configPath);
                var config = JsonSerializer.Deserialize<AiBotConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });

                lock (_configLock)
                {
                    _cachedConfig = config;
                    _configLastLoaded = DateTime.UtcNow;
                }

                _logger.LogInformation("AI bot configuration loaded. Enabled: {Enabled}, Avatar: {AvatarName}", 
                    config?.Enabled, config?.AvatarName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading AI bot configuration from {ConfigPath}", _configPath);
                lock (_configLock)
                {
                    _cachedConfig = null;
                    _configLastLoaded = DateTime.UtcNow;
                }
            }
        }

        private List<AiChatMessage> BuildChatMessages(ChatMessageDto message, IEnumerable<ChatMessageDto> chatHistory, AiBotConfig config)
        {
            var messages = new List<AiChatMessage>();

            // Add system prompt
            messages.Add(new AiChatMessage
            {
                Role = "system",
                Content = config.SystemPrompt
            });

            // Add chat history if enabled
            if (config.ChatHistory.IncludeHistory)
            {
                var relevantHistory = chatHistory
                    .Where(h => h.ChatType.ToLower() == "normal" && // Only normal chat
                               h.Timestamp >= DateTime.UtcNow.AddMinutes(-config.ChatHistory.MaxHistoryAgeMinutes))
                    .OrderBy(h => h.Timestamp)
                    .TakeLast(config.ChatHistory.MaxHistoryMessages)
                    .ToList();

                var totalHistoryCharacters = 0;
                var historyMessages = new List<AiChatMessage>();

                foreach (var historyMsg in relevantHistory)
                {
                    // Skip bot's own messages if not configured to include them
                    var accountInstance = _accountService.GetInstance(message.AccountId);
                    if (!config.ChatHistory.IncludeBotMessages && 
                        accountInstance != null && 
                        historyMsg.SenderName == accountInstance.Client.Self.Name)
                        continue;

                    // Truncate individual messages if they're too long
                    var messageText = historyMsg.Message;
                    if (messageText.Length > config.ChatHistory.MaxMessageLength)
                    {
                        messageText = messageText.Substring(0, config.ChatHistory.MaxMessageLength - 3) + "...";
                    }

                    var formattedContent = $"{historyMsg.SenderName}: {messageText}";
                    
                    // Check if adding this message would exceed the total character limit
                    if (totalHistoryCharacters + formattedContent.Length > config.ChatHistory.MaxHistoryCharacters)
                    {
                        _logger.LogDebug("Chat history truncated at {TotalChars} characters to stay within {MaxChars} limit", 
                            totalHistoryCharacters, config.ChatHistory.MaxHistoryCharacters);
                        break;
                    }

                    totalHistoryCharacters += formattedContent.Length;
                    historyMessages.Add(new AiChatMessage
                    {
                        Role = "user",
                        Content = formattedContent,
                        SenderName = historyMsg.SenderName,
                        Timestamp = historyMsg.Timestamp
                    });
                }

                messages.AddRange(historyMessages);
                
                _logger.LogDebug("Included {MessageCount} history messages with {TotalChars} characters", 
                    historyMessages.Count, totalHistoryCharacters);
            }

            // Add current message (also apply message length limit)
            var currentMessageText = message.Message;
            if (currentMessageText.Length > config.ChatHistory.MaxMessageLength)
            {
                currentMessageText = currentMessageText.Substring(0, config.ChatHistory.MaxMessageLength - 3) + "...";
            }

            messages.Add(new AiChatMessage
            {
                Role = "user",
                Content = $"{message.SenderName}: {currentMessageText}",
                SenderName = message.SenderName,
                Timestamp = message.Timestamp
            });

            return messages;
        }

        private async Task<AiApiResponse> CallAiApiAsync(List<AiChatMessage> messages, AiApiConfig apiConfig)
        {
            try
            {
                var request = new AiApiRequest
                {
                    Model = apiConfig.Model,
                    Messages = messages,
                    MaxTokens = apiConfig.MaxTokens,
                    Temperature = apiConfig.Temperature
                };

                var jsonRequest = SerializeApiRequest(request, apiConfig.Provider);
                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                // Set authorization header
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiConfig.ApiKey);

                // Set timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(apiConfig.TimeoutSeconds));

                string endpoint = apiConfig.Provider.ToLower() switch
                {
                    "openai" => "/v1/chat/completions",
                    "anthropic" => "/v1/messages",
                    _ => "/v1/chat/completions" // Default to OpenAI format
                };

                var response = await _httpClient.PostAsync($"{apiConfig.ApiUrl.TrimEnd('/')}{endpoint}", content, cts.Token);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    return ParseApiResponse(responseJson, apiConfig.Provider);
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    return new AiApiResponse
                    {
                        Success = false,
                        ErrorMessage = $"API call failed with status {response.StatusCode}: {errorContent}"
                    };
                }
            }
            catch (TaskCanceledException)
            {
                return new AiApiResponse
                {
                    Success = false,
                    ErrorMessage = "Request timed out"
                };
            }
            catch (Exception ex)
            {
                return new AiApiResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private string SerializeApiRequest(AiApiRequest request, string provider)
        {
            return provider.ToLower() switch
            {
                "openai" => SerializeOpenAiRequest(request),
                "anthropic" => SerializeAnthropicRequest(request),
                _ => SerializeOpenAiRequest(request) // Default to OpenAI format
            };
        }

        private string SerializeOpenAiRequest(AiApiRequest request)
        {
            var openAiRequest = new
            {
                model = request.Model,
                messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
                max_tokens = request.MaxTokens,
                temperature = request.Temperature
            };

            return JsonSerializer.Serialize(openAiRequest);
        }

        private string SerializeAnthropicRequest(AiApiRequest request)
        {
            var systemMessage = request.Messages.FirstOrDefault(m => m.Role == "system");
            var userMessages = request.Messages.Where(m => m.Role != "system").ToList();

            var anthropicRequest = new
            {
                model = request.Model,
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                system = systemMessage?.Content ?? "",
                messages = userMessages.Select(m => new { role = m.Role, content = m.Content })
            };

            return JsonSerializer.Serialize(anthropicRequest);
        }

        private AiApiResponse ParseApiResponse(string responseJson, string provider)
        {
            try
            {
                return provider.ToLower() switch
                {
                    "openai" => ParseOpenAiResponse(responseJson),
                    "anthropic" => ParseAnthropicResponse(responseJson),
                    _ => ParseOpenAiResponse(responseJson) // Default to OpenAI format
                };
            }
            catch (Exception ex)
            {
                return new AiApiResponse
                {
                    Success = false,
                    ErrorMessage = $"Failed to parse API response: {ex.Message}"
                };
            }
        }

        private AiApiResponse ParseOpenAiResponse(string responseJson)
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    var tokenUsage = 0;
                    if (root.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("total_tokens", out var totalTokens))
                    {
                        tokenUsage = totalTokens.GetInt32();
                    }

                    return new AiApiResponse
                    {
                        Success = true,
                        Content = content.GetString(),
                        TokensUsed = tokenUsage
                    };
                }
            }

            return new AiApiResponse
            {
                Success = false,
                ErrorMessage = "Unexpected response format from OpenAI API"
            };
        }

        private AiApiResponse ParseAnthropicResponse(string responseJson)
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("content", out var content) && content.GetArrayLength() > 0)
            {
                var firstContent = content[0];
                if (firstContent.TryGetProperty("text", out var text))
                {
                    var tokenUsage = 0;
                    if (root.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("output_tokens", out var outputTokens))
                    {
                        tokenUsage = outputTokens.GetInt32();
                    }

                    return new AiApiResponse
                    {
                        Success = true,
                        Content = text.GetString(),
                        TokensUsed = tokenUsage
                    };
                }
            }

            return new AiApiResponse
            {
                Success = false,
                ErrorMessage = "Unexpected response format from Anthropic API"
            };
        }
    }
}