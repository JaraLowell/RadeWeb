using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Configuration for AI Chat Bot settings
    /// </summary>
    public class AiBotConfig
    {
        /// <summary>
        /// Whether the AI chat bot is enabled
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Avatar name (e.g., "FirstName LastName") that the AI bot should respond for
        /// </summary>
        public string? AvatarName { get; set; }

        /// <summary>
        /// The main system prompt that defines the AI's personality and behavior
        /// </summary>
        public string SystemPrompt { get; set; } = string.Empty;

        /// <summary>
        /// API configuration for the AI service
        /// </summary>
        public AiApiConfig ApiConfig { get; set; } = new();

        /// <summary>
        /// Chat history settings
        /// </summary>
        public AiChatHistoryConfig ChatHistory { get; set; } = new();

        /// <summary>
        /// Response settings
        /// </summary>
        public AiResponseConfig ResponseConfig { get; set; } = new();
    }

    /// <summary>
    /// API configuration for AI services
    /// </summary>
    public class AiApiConfig
    {
        /// <summary>
        /// The AI provider type (openai, anthropic, custom)
        /// </summary>
        [Required]
        public string Provider { get; set; } = "openai";

        /// <summary>
        /// The API endpoint URL
        /// </summary>
        [Required]
        public string ApiUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>
        /// The API key for authentication
        /// </summary>
        [Required]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// The model to use (e.g., gpt-4, claude-3-sonnet, etc.)
        /// </summary>
        [Required]
        public string Model { get; set; } = "gpt-4";

        /// <summary>
        /// Maximum tokens in the response
        /// </summary>
        public int MaxTokens { get; set; } = 150;

        /// <summary>
        /// Temperature for response randomness (0.0 to 1.0)
        /// </summary>
        public double Temperature { get; set; } = 0.7;

        /// <summary>
        /// Request timeout in seconds
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;
    }

    /// <summary>
    /// Chat history configuration
    /// </summary>
    public class AiChatHistoryConfig
    {
        /// <summary>
        /// Whether to include chat history in AI requests
        /// </summary>
        public bool IncludeHistory { get; set; } = true;

        /// <summary>
        /// Maximum number of previous messages to include
        /// </summary>
        public int MaxHistoryMessages { get; set; } = 10;

        /// <summary>
        /// Maximum age of messages to include (in minutes)
        /// </summary>
        public int MaxHistoryAgeMinutes { get; set; } = 30;

        /// <summary>
        /// Maximum total characters in chat history (prevents huge payloads)
        /// </summary>
        public int MaxHistoryCharacters { get; set; } = 2000;

        /// <summary>
        /// Maximum characters per individual message (truncate long messages)
        /// </summary>
        public int MaxMessageLength { get; set; } = 200;

        /// <summary>
        /// Whether to include the bot's own messages in history
        /// </summary>
        public bool IncludeBotMessages { get; set; } = true;
    }

    /// <summary>
    /// Response behavior configuration
    /// </summary>
    public class AiResponseConfig
    {
        /// <summary>
        /// Minimum delay before responding (in seconds)
        /// </summary>
        public int MinResponseDelaySeconds { get; set; } = 2;

        /// <summary>
        /// Maximum delay before responding (in seconds)
        /// </summary>
        public int MaxResponseDelaySeconds { get; set; } = 5;

        /// <summary>
        /// Probability of responding to a message (0.0 to 1.0)
        /// </summary>
        public double ResponseProbability { get; set; } = 0.8;

        /// <summary>
        /// Maximum response length in characters
        /// </summary>
        public int MaxResponseLength { get; set; } = 500;

        /// <summary>
        /// Whether to respond to messages that mention the avatar by name
        /// </summary>
        public bool RespondToNameMentions { get; set; } = true;

        /// <summary>
        /// Whether to respond to direct questions (messages ending with ?)
        /// </summary>
        public bool RespondToQuestions { get; set; } = true;

        /// <summary>
        /// List of keywords that trigger responses
        /// </summary>
        public List<string> TriggerKeywords { get; set; } = new();

        /// <summary>
        /// List of avatar UUIDs to ignore (never respond to) - more secure than names
        /// </summary>
        public List<string> IgnoreUuids { get; set; } = new();

        /// <summary>
        /// List of avatar names to ignore (legacy support, less secure than UUIDs)
        /// </summary>
        public List<string> IgnoreNames { get; set; } = new();
    }

    /// <summary>
    /// Represents a chat message for AI processing
    /// </summary>
    public class AiChatMessage
    {
        public string Role { get; set; } = "user"; // "system", "user", "assistant"
        public string Content { get; set; } = string.Empty;
        public string? SenderName { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Request to AI API
    /// </summary>
    public class AiApiRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<AiChatMessage> Messages { get; set; } = new();
        public int MaxTokens { get; set; } = 150;
        public double Temperature { get; set; } = 0.7;
    }

    /// <summary>
    /// Response from AI API
    /// </summary>
    public class AiApiResponse
    {
        public bool Success { get; set; }
        public string? Content { get; set; }
        public string? ErrorMessage { get; set; }
        public int? TokensUsed { get; set; }
    }
}