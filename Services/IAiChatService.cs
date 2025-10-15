using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Interface for AI chat bot functionality
    /// </summary>
    public interface IAiChatService
    {
        /// <summary>
        /// Whether the AI chat service is enabled and configured
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Check if the AI should respond to a chat message
        /// </summary>
        /// <param name="message">The incoming chat message</param>
        /// <returns>True if the AI should respond</returns>
        Task<bool> ShouldRespondAsync(ChatMessageDto message);

        /// <summary>
        /// Generate an AI response to a chat message
        /// </summary>
        /// <param name="message">The incoming chat message</param>
        /// <param name="chatHistory">Recent chat history for context</param>
        /// <returns>The AI-generated response, or null if no response should be sent</returns>
        Task<string?> GenerateResponseAsync(ChatMessageDto message, IEnumerable<ChatMessageDto> chatHistory);

        /// <summary>
        /// Process an incoming chat message and potentially generate a response
        /// </summary>
        /// <param name="message">The incoming chat message</param>
        /// <param name="chatHistory">Recent chat history for context</param>
        /// <returns>The response message to send, or null if no response</returns>
        Task<string?> ProcessChatMessageAsync(ChatMessageDto message, IEnumerable<ChatMessageDto> chatHistory);

        /// <summary>
        /// Reload the AI bot configuration from file
        /// </summary>
        Task ReloadConfigurationAsync();

        /// <summary>
        /// Get the current configuration
        /// </summary>
        AiBotConfig? GetConfiguration();
    }
}