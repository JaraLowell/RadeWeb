using RadegastWeb.Core;
using RadegastWeb.Models;
using Microsoft.Extensions.DependencyInjection;
using OpenMetaverse;
using System.Text.RegularExpressions;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Processor for handling command relay from the configured AvatarRelayUuid
    /// Processes IM commands like //sit, //stand, //say, //im from the relay avatar
    /// </summary>
    internal class AvatarCommandRelayProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        // Command patterns
        private static readonly Regex SitCommandRegex = new(@"^//sit\s+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StandCommandRegex = new(@"^//stand$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SayCommandRegex = new(@"^//say\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ImCommandRegex = new(@"^//im\s+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public AvatarCommandRelayProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "Avatar Command Relay";
        public int Priority => 15; // Process after Corrade but before general processing

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            // Only process incoming IM messages (not our own outgoing IMs)
            if (message.ChatType?.ToLower() != "im" || string.IsNullOrEmpty(message.SenderId))
                return ChatProcessingResult.CreateSuccess();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                
                // Get the account to check for AvatarRelayUuid
                var account = await accountService.GetAccountAsync(context.AccountId);
                if (account == null)
                {
                    _logger.LogWarning("Account {AccountId} not found for command relay", context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Check if AvatarRelayUuid is configured
                if (string.IsNullOrEmpty(account.AvatarRelayUuid) || 
                    account.AvatarRelayUuid == "00000000-0000-0000-0000-000000000000")
                {
                    _logger.LogDebug("Account {AccountId} has no valid relay avatar configured for command processing", context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Only process IMs from the configured relay avatar
                if (!message.SenderId.Equals(account.AvatarRelayUuid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("IM from {SenderId} ignored - not from relay avatar {RelayUuid} for account {AccountId}", 
                        message.SenderId, account.AvatarRelayUuid, context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Ensure we have a connected account instance
                if (context.AccountInstance == null || !context.AccountInstance.IsConnected)
                {
                    _logger.LogWarning("Account {AccountId} instance not connected, cannot process relay command", context.AccountId);
                    // Send feedback to relay avatar about connection issue
                    return ChatProcessingResult.CreateSuccess();
                }

                _logger.LogInformation("Processing command relay from {SenderName} ({SenderId}) for account {AccountId}: {Message}", 
                    message.SenderName, message.SenderId, context.AccountId, message.Message);

                // Process the command
                var commandResult = await ProcessRelayCommand(context.AccountInstance, message.Message);
                
                if (commandResult.Success)
                {
                    _logger.LogInformation("Successfully executed relay command for account {AccountId}: {Message}", 
                        context.AccountId, commandResult.Message);
                    
                    // Send success feedback to relay avatar
                    if (!string.IsNullOrEmpty(commandResult.Message))
                    {
                        context.AccountInstance.SendIM(account.AvatarRelayUuid, $"✓ {commandResult.Message}");
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to execute relay command for account {AccountId}: {ErrorMessage}", 
                        context.AccountId, commandResult.ErrorMessage);
                    
                    // Send error feedback to relay avatar
                    context.AccountInstance.SendIM(account.AvatarRelayUuid, $"✗ {commandResult.ErrorMessage}");
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command relay for account {AccountId}", context.AccountId);
                return ChatProcessingResult.CreateSuccess(); // Continue processing even if relay fails
            }
        }

        /// <summary>
        /// Process a relay command and execute the appropriate action
        /// </summary>
        /// <param name="accountInstance">The connected account instance</param>
        /// <param name="commandMessage">The command message to process</param>
        /// <returns>Command execution result</returns>
        private Task<RelayCommandResult> ProcessRelayCommand(WebRadegastInstance accountInstance, string commandMessage)
        {
            var trimmedCommand = commandMessage.Trim();

            // Check for sit command: //sit uuid
            var sitMatch = SitCommandRegex.Match(trimmedCommand);
            if (sitMatch.Success)
            {
                var targetUuidStr = sitMatch.Groups[1].Value;
                if (UUID.TryParse(targetUuidStr, out var targetUuid))
                {
                    var success = accountInstance.SetSitting(true, targetUuid);
                    if (success)
                    {
                        return Task.FromResult(RelayCommandResult.CreateSuccess($"Sitting on object {targetUuid}"));
                    }
                    else
                    {
                        return Task.FromResult(RelayCommandResult.CreateError($"Failed to sit on object {targetUuid} - object not found or unreachable"));
                    }
                }
                else
                {
                    return Task.FromResult(RelayCommandResult.CreateError($"Invalid UUID format: {targetUuidStr}"));
                }
            }

            // Check for stand command: //stand
            var standMatch = StandCommandRegex.Match(trimmedCommand);
            if (standMatch.Success)
            {
                var success = accountInstance.SetSitting(false);
                if (success)
                {
                    return Task.FromResult(RelayCommandResult.CreateSuccess("Standing up"));
                }
                else
                {
                    return Task.FromResult(RelayCommandResult.CreateError("Failed to stand up"));
                }
            }

            // Check for say command: //say <message>
            var sayMatch = SayCommandRegex.Match(trimmedCommand);
            if (sayMatch.Success)
            {
                var messageText = sayMatch.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(messageText))
                {
                    accountInstance.SendChat(messageText, OpenMetaverse.ChatType.Normal, 0);
                    return Task.FromResult(RelayCommandResult.CreateSuccess($"Said in local chat: {messageText}"));
                }
                else
                {
                    return Task.FromResult(RelayCommandResult.CreateError("Say command requires a message"));
                }
            }

            // Check for IM command: //im uuid message
            var imMatch = ImCommandRegex.Match(trimmedCommand);
            if (imMatch.Success)
            {
                var targetUuidStr = imMatch.Groups[1].Value;
                var messageText = imMatch.Groups[2].Value;
                
                if (UUID.TryParse(targetUuidStr, out var targetUuid))
                {
                    if (!string.IsNullOrWhiteSpace(messageText))
                    {
                        accountInstance.SendIM(targetUuid.ToString(), messageText);
                        return Task.FromResult(RelayCommandResult.CreateSuccess($"Sent IM to {targetUuid}: {messageText}"));
                    }
                    else
                    {
                        return Task.FromResult(RelayCommandResult.CreateError("IM command requires a message"));
                    }
                }
                else
                {
                    return Task.FromResult(RelayCommandResult.CreateError($"Invalid UUID format: {targetUuidStr}"));
                }
            }

            // Command not recognized
            return Task.FromResult(RelayCommandResult.CreateError($"Unknown command: {trimmedCommand}. Supported commands: //sit <uuid>, //stand, //say <message>, //im <uuid> <message>"));
        }
    }

    /// <summary>
    /// Result of processing a relay command
    /// </summary>
    internal class RelayCommandResult
    {
        public bool Success { get; private set; }
        public string? Message { get; private set; }
        public string? ErrorMessage { get; private set; }

        private RelayCommandResult(bool success, string? message, string? errorMessage)
        {
            Success = success;
            Message = message;
            ErrorMessage = errorMessage;
        }

        public static RelayCommandResult CreateSuccess(string message) => new(true, message, null);
        public static RelayCommandResult CreateError(string errorMessage) => new(false, null, errorMessage);
    }
}