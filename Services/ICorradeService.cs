using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Interface for Corrade plugin service
    /// </summary>
    public interface ICorradeService
    {
        /// <summary>
        /// Gets whether the Corrade plugin is enabled (has configured groups)
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Processes an incoming whisper message to check if it's a Corrade command
        /// </summary>
        /// <param name="accountId">The account ID that received the whisper</param>
        /// <param name="senderId">The UUID of the sender</param>
        /// <param name="senderName">The display name of the sender</param>
        /// <param name="message">The whisper message content</param>
        /// <returns>Command result indicating success/failure and any response</returns>
        Task<CorradeCommandResult> ProcessWhisperCommandAsync(Guid accountId, string senderId, string senderName, string message);

        /// <summary>
        /// Checks if a whisper message contains a Corrade command
        /// </summary>
        /// <param name="message">The message to check</param>
        /// <returns>True if message starts with a Corrade command</returns>
        bool IsWhisperCorradeCommand(string message);

        /// <summary>
        /// Loads Corrade configuration from corrade.json file
        /// </summary>
        /// <returns>The loaded configuration</returns>
        Task<CorradeConfig> LoadConfigurationAsync();

        /// <summary>
        /// Saves Corrade configuration to corrade.json file
        /// </summary>
        /// <param name="config">The configuration to save</param>
        Task SaveConfigurationAsync(CorradeConfig config);

        /// <summary>
        /// Validates if an account has permission to relay to a specific group
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <param name="groupUuid">The target group UUID</param>
        /// <param name="password">The provided password</param>
        /// <returns>True if permission is granted</returns>
        Task<bool> ValidateGroupPermissionAsync(Guid accountId, string groupUuid, string password);

        /// <summary>
        /// Validates if an account has permission to send messages via Corrade
        /// </summary>
        /// <param name="accountId">The account ID</param>
        /// <param name="entity">The entity type (local, group, avatar)</param>
        /// <param name="targetId">The target ID for group/avatar entities</param>
        /// <param name="groupUuid">The authorizing group UUID</param>
        /// <param name="password">The provided password</param>
        /// <returns>True if permission is granted</returns>
        Task<bool> ValidateEntityPermissionAsync(Guid accountId, string entity, string? targetId, string groupUuid, string password);
    }
}