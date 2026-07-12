using Microsoft.AspNetCore.SignalR;
using LibreMetaverse;
using RadegastWeb.Core;
using RadegastWeb.Hubs;
using RadegastWeb.Models;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Unified chat processing service that coordinates all chat-related processing
    /// </summary>
    public class ChatProcessingService : IChatProcessingService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ChatProcessingService> _logger;
        private readonly IHubContext<RadegastHub, IRadegastHubClient> _hubContext;
        private readonly ISLTimeService _sltTimeService;
        private readonly ConcurrentDictionary<string, (IChatMessageProcessor processor, int priority)> _processors;

        public ChatProcessingService(
            IServiceProvider serviceProvider,
            ILogger<ChatProcessingService> logger,
            IHubContext<RadegastHub, IRadegastHubClient> hubContext,
            ISLTimeService sltTimeService)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _hubContext = hubContext;
            _sltTimeService = sltTimeService;
            _processors = new ConcurrentDictionary<string, (IChatMessageProcessor, int)>();

            // Register built-in processors
            RegisterBuiltInProcessors();
        }

        private void RegisterBuiltInProcessors()
        {
            // Register processors in priority order (lower numbers first)
            RegisterProcessor(new GroupIgnoreFilterProcessor(_serviceProvider, _logger), 5); // First to filter out ignored groups
            RegisterProcessor(new UrlProcessingProcessor(_serviceProvider, _logger), 10);
            RegisterProcessor(new GroupTeleportCommandProcessor(_serviceProvider, _logger), 12); // Process teleport commands early
            RegisterProcessor(new AvatarCommandRelayProcessor(_serviceProvider, _logger), 15); // Process avatar command relays
            RegisterProcessor(new IMRelayProcessor(_serviceProvider, _logger), 16); // Process IM relays before database save
            RegisterProcessor(new NameResolutionProcessor(_serviceProvider, _logger), 18); // Improve sender names before save
            RegisterProcessor(new DatabaseSaveProcessor(_serviceProvider, _logger), 20);
            RegisterProcessor(new SignalRBroadcastProcessor(_hubContext, _logger), 30);
            RegisterProcessor(new CorradeCommandProcessor(_serviceProvider, _logger), 40);
            RegisterProcessor(new AiChatProcessor(_serviceProvider, _logger), 50);
        }

        public async Task ProcessChatMessageAsync(ChatMessageDto message, Guid accountId)
        {
            var context = new ChatProcessingContext
            {
                AccountId = accountId
            };

            try
            {
                // Get account instance
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                context.AccountInstance = accountService.GetInstance(accountId);

                // Get recent chat history for context
                var chatHistoryService = scope.ServiceProvider.GetRequiredService<IChatHistoryService>();
                context.RecentHistory = await chatHistoryService.GetChatHistoryAsync(accountId, message.SessionId ?? "local-chat", 10);

                // Get processors ordered by priority
                var orderedProcessors = _processors.Values
                    .OrderBy(p => p.priority)
                    .Select(p => p.processor)
                    .ToList();

                // Process through pipeline
                foreach (var processor in orderedProcessors)
                {
                    try
                    {
                        var result = await processor.ProcessAsync(message, context);
                        
                        if (!result.Success)
                        {
                            _logger.LogWarning("Chat processor {ProcessorName} failed: {ErrorMessage}", 
                                processor.Name, result.ErrorMessage);
                        }

                        // Update message if processor modified it
                        if (result.ModifiedMessage != null)
                        {
                            message = result.ModifiedMessage;
                        }

                        // Handle response message
                        if (!string.IsNullOrEmpty(result.ResponseMessage) && context.AccountInstance != null)
                        {
                            // Send response back to chat
                            if (string.Equals(message.ChatType, "Normal", StringComparison.OrdinalIgnoreCase))
                            {
                                context.AccountInstance.SendChat(result.ResponseMessage);
                            }
                            else if (string.Equals(message.ChatType, "IM", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrEmpty(message.SenderId))
                            {
                                context.AccountInstance.SendIM(message.SenderId, result.ResponseMessage);
                            }
                            else if (string.Equals(message.ChatType, "Group", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrEmpty(message.TargetId))
                            {
                                context.AccountInstance.SendGroupIM(message.TargetId, result.ResponseMessage);
                            }
                            // Add more chat types as needed
                        }

                        // Stop processing if requested
                        if (!result.ContinueProcessing)
                        {
                            _logger.LogDebug("Chat processing stopped by {ProcessorName}", processor.Name);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in chat processor {ProcessorName}", processor.Name);
                        // Continue with other processors even if one fails
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in chat processing pipeline for account {AccountId}", accountId);
            }
        }

        public void RegisterProcessor(IChatMessageProcessor processor, int priority = 100)
        {
            var key = $"{processor.GetType().Name}_{priority}";
            _processors.TryAdd(key, (processor, priority));
            _logger.LogDebug("Registered chat processor {ProcessorName} with priority {Priority}", 
                processor.Name, priority);
        }

        public void UnregisterProcessor(IChatMessageProcessor processor)
        {
            var toRemove = _processors.Where(kvp => kvp.Value.processor == processor).ToList();
            foreach (var kvp in toRemove)
            {
                _processors.TryRemove(kvp.Key, out _);
                _logger.LogDebug("Unregistered chat processor {ProcessorName}", processor.Name);
            }
        }
    }

    /// <summary>
    /// Processor for handling URL parsing and replacement
    /// </summary>
    internal class UrlProcessingProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public UrlProcessingProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "URL Processing";
        public int Priority => 10;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var urlParser = scope.ServiceProvider.GetRequiredService<ISlUrlParser>();
                var sltTimeService = scope.ServiceProvider.GetRequiredService<ISLTimeService>();
                
                var processedMessage = await urlParser.ProcessChatMessageAsync(message.Message, context.AccountId);
                
                if (processedMessage != message.Message)
                {
                    var modifiedMessage = new ChatMessageDto
                    {
                        SenderName = message.SenderName,
                        Message = processedMessage,
                        ChatType = message.ChatType,
                        Channel = message.Channel,
                        Timestamp = message.Timestamp,
                        RegionName = message.RegionName,
                        AccountId = message.AccountId,
                        SenderId = message.SenderId,
                        TargetId = message.TargetId,
                        SessionId = message.SessionId,
                        SessionName = message.SessionName,
                        SLTTime = sltTimeService.FormatSLT(message.Timestamp, "HH:mm:ss"),
                        SLTDateTime = sltTimeService.FormatSLTWithDate(message.Timestamp, "MMM dd, HH:mm:ss")
                    };
                    
                    return new ChatProcessingResult
                    {
                        Success = true,
                        ContinueProcessing = true,
                        ModifiedMessage = modifiedMessage
                    };
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing URLs in chat message");
                return ChatProcessingResult.CreateSuccess(); // Continue even if URL processing fails
            }
        }
    }

    /// <summary>
    /// Processor for resolving and improving sender names in chat messages
    /// Particularly useful for relay messages that may initially show as \"Unknown\"
    /// </summary>
    internal class NameResolutionProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public NameResolutionProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "Name Resolution";
        public int Priority => 18;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            // Only process IM messages that have a SenderId
            if (message.ChatType != "IM" || string.IsNullOrEmpty(message.SenderId))
                return ChatProcessingResult.CreateSuccess();

            // Check if sender name needs improvement (is placeholder or generic)
            var needsImprovement = string.IsNullOrWhiteSpace(message.SenderName) ||
                                 message.SenderName == "Unknown" ||
                                 message.SenderName == "Unknown User" ||
                                 message.SenderName.StartsWith("Resolving...") ||
                                 message.SenderName == "Loading..." ||
                                 message.SenderName == "???";

            if (!needsImprovement)
                return ChatProcessingResult.CreateSuccess();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var nameResolutionService = scope.ServiceProvider.GetRequiredService<INameResolutionService>();
                var globalDisplayNameCache = scope.ServiceProvider.GetRequiredService<IGlobalDisplayNameCache>();

                if (!UUID.TryParse(message.SenderId, out var senderUuid))
                    return ChatProcessingResult.CreateSuccess();

                // Try to get a better name from cache first (fast)
                var cachedName = await globalDisplayNameCache.GetDisplayNameAsync(
                    message.SenderId, NameDisplayMode.Smart, message.SenderName);
                
                string improvedName = message.SenderName;
                
                if (!string.IsNullOrWhiteSpace(cachedName) && 
                    cachedName != "Loading..." && 
                    cachedName != "???" &&
                    !cachedName.StartsWith("Resolving..."))
                {
                    improvedName = cachedName;
                    _logger.LogDebug("Improved sender name from '{OldName}' to '{NewName}' using cache for {SenderId}", 
                        message.SenderName, improvedName, message.SenderId);
                }
                else
                {
                    // Try name resolution service (may be slower)
                    try
                    {
                        var resolvedName = await nameResolutionService.ResolveAgentNameAsync(
                            context.AccountId, senderUuid, ResolveType.AgentDefaultName, 2000); // 2 second timeout
                        
                        if (!string.IsNullOrWhiteSpace(resolvedName) && 
                            resolvedName != "Loading..." && 
                            resolvedName != "???" &&
                            !resolvedName.StartsWith("Resolving...") &&
                            resolvedName != senderUuid.ToString())
                        {
                            improvedName = resolvedName;
                            _logger.LogDebug("Improved sender name from '{OldName}' to '{NewName}' using resolution service for {SenderId}", 
                                message.SenderName, improvedName, message.SenderId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error resolving sender name for {SenderId}", message.SenderId);
                    }
                }

                // If we got a better name, create modified message
                if (improvedName != message.SenderName)
                {
                    var sltTimeService = scope.ServiceProvider.GetRequiredService<ISLTimeService>();
                    
                    var modifiedMessage = new ChatMessageDto
                    {
                        SenderName = improvedName,
                        Message = message.Message,
                        ChatType = message.ChatType,
                        Channel = message.Channel,
                        Timestamp = message.Timestamp,
                        RegionName = message.RegionName,
                        AccountId = message.AccountId,
                        SenderId = message.SenderId,
                        TargetId = message.TargetId,
                        SessionId = message.SessionId,
                        SessionName = message.SessionName,
                        SLTTime = sltTimeService.FormatSLT(message.Timestamp, "HH:mm:ss"),
                        SLTDateTime = sltTimeService.FormatSLTWithDate(message.Timestamp, "MMM dd, HH:mm:ss")
                    };
                    
                    return new ChatProcessingResult
                    {
                        Success = true,
                        ContinueProcessing = true,
                        ModifiedMessage = modifiedMessage
                    };
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in name resolution processor for sender {SenderId}", message.SenderId);
                return ChatProcessingResult.CreateSuccess(); // Continue processing even if name resolution fails
            }
        }
    }

    /// <summary>
    /// Processor for saving messages to database
    /// </summary>
    internal class DatabaseSaveProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public DatabaseSaveProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "Database Save";
        public int Priority => 20;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            if (context.MessageSaved)
                return ChatProcessingResult.CreateSuccess();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var chatHistoryService = scope.ServiceProvider.GetRequiredService<IChatHistoryService>();
                await chatHistoryService.SaveChatMessageAsync(message);
                context.MessageSaved = true;
                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving chat message to database");
                return ChatProcessingResult.CreateError("Failed to save to database");
            }
        }
    }

    /// <summary>
    /// Processor for broadcasting messages via SignalR
    /// </summary>
    internal class SignalRBroadcastProcessor : IChatMessageProcessor
    {
        private readonly IHubContext<RadegastHub, IRadegastHubClient> _hubContext;
        private readonly ILogger _logger;

        public SignalRBroadcastProcessor(IHubContext<RadegastHub, IRadegastHubClient> hubContext, ILogger logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        public string Name => "SignalR Broadcast";
        public int Priority => 30;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            if (context.MessageBroadcast)
                return ChatProcessingResult.CreateSuccess();

            try
            {
                await _hubContext.Clients
                    .Group($"account_{message.AccountId}")
                    .ReceiveChat(message);
                context.MessageBroadcast = true;
                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting chat message via SignalR");
                return ChatProcessingResult.CreateError("Failed to broadcast message");
            }
        }
    }

    /// <summary>
    /// Processor for handling Corrade commands
    /// </summary>
    internal class CorradeCommandProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public CorradeCommandProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "Corrade Commands";
        public int Priority => 40;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            // Only process IM messages for Corrade commands
            if (message.ChatType != "IM" || string.IsNullOrEmpty(message.SenderId))
                return ChatProcessingResult.CreateSuccess();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var corradeService = scope.ServiceProvider.GetRequiredService<ICorradeService>();
                
                if (corradeService.IsWhisperCorradeCommand(message.Message))
                {
                    var result = await corradeService.ProcessWhisperCommandAsync(
                        context.AccountId,
                        message.SenderId,
                        message.SenderName,
                        message.Message);

                    _logger.LogInformation("Processed Corrade command from {SenderName}: Success={Success}, Message={Message}",
                        message.SenderName, result.Success, result.Message);

                    // Return response if available (Corrade uses Message property for responses)
                    if (result.Success && !string.IsNullOrEmpty(result.Message))
                    {
                        return ChatProcessingResult.CreateSuccessWithResponse(result.Message);
                    }
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Corrade command from {SenderName}", message.SenderName);
                return ChatProcessingResult.CreateSuccess(); // Continue processing even if Corrade fails
            }
        }
    }

    /// <summary>
    /// Processor for AI chat responses
    /// </summary>
    internal class AiChatProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public AiChatProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "AI Chat";
        public int Priority => 50;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            // Process local chat by default, and group chat when enabled in AI config.
            var isNormalChat = string.Equals(message.ChatType, "Normal", StringComparison.OrdinalIgnoreCase);
            var isGroupChat = string.Equals(message.ChatType, "Group", StringComparison.OrdinalIgnoreCase);
            if ((!isNormalChat && !isGroupChat) || string.IsNullOrEmpty(message.SenderId))
                return ChatProcessingResult.CreateSuccess();

            // Never run AI on messages authored by this same avatar, including outgoing local/group posts.
            if (context.AccountInstance != null &&
                string.Equals(message.SenderId, context.AccountInstance.Client.Self.AgentID.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return ChatProcessingResult.CreateSuccess();
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var aiChatService = scope.ServiceProvider.GetRequiredService<IAiChatService>();
                
                if (!aiChatService.IsEnabled)
                    return ChatProcessingResult.CreateSuccess();

                var aiResponse = await aiChatService.ProcessChatMessageAsync(message, context.RecentHistory);
                
                if (!string.IsNullOrEmpty(aiResponse))
                {
                    _logger.LogDebug("AI bot responding to {SenderName}: {Response}", 
                        message.SenderName, aiResponse);
                    return ChatProcessingResult.CreateSuccessWithResponse(aiResponse);
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing AI chat response for message from {SenderName}", message.SenderName);
                return ChatProcessingResult.CreateSuccess(); // Continue processing even if AI fails
            }
        }
    }

    /// <summary>
    /// Processor for relaying incoming IMs to specified relay avatar
    /// </summary>
    internal class IMRelayProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;
        
        // Second Life NULL_KEY constant
        private const string NULL_KEY = "00000000-0000-0000-0000-000000000000";

        public IMRelayProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "IM Relay";
        public int Priority => 15;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            // Only process incoming IM messages (not our own outgoing IMs)
            if (message.ChatType != "IM" || string.IsNullOrEmpty(message.SenderId))
                return ChatProcessingResult.CreateSuccess();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
                
                // Get the account to check for AvatarRelayUuid
                var account = await accountService.GetAccountAsync(context.AccountId);
                if (account == null)
                {
                    _logger.LogWarning("Account {AccountId} not found for IM relay", context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Check if AvatarRelayUuid is valid (not null, empty, or NULL_KEY)
                if (string.IsNullOrEmpty(account.AvatarRelayUuid) || account.AvatarRelayUuid == NULL_KEY)
                {
                    _logger.LogDebug("Account {AccountId} has no valid relay avatar configured", context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Skip relaying if this is a Corrade command - don't relay commands to the relay avatar
                var corradeService = scope.ServiceProvider.GetRequiredService<ICorradeService>();
                if (corradeService.IsWhisperCorradeCommand(message.Message))
                {
                    _logger.LogDebug("Skipping IM relay for Corrade command on account {AccountId}", context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Prevent sending IM to oneself
                if (context.AccountInstance != null && 
                    account.AvatarRelayUuid.Equals(context.AccountInstance.Client.Self.AgentID.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Account {AccountId} has AvatarRelayUuid set to own avatar ID {OwnId} - cannot send IM to self", 
                        context.AccountId, context.AccountInstance.Client.Self.AgentID);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Skip relaying if this IM is from our own account (avoid loops)
                if (context.AccountInstance != null && 
                    message.SenderId == context.AccountInstance.Client.Self.AgentID.ToString())
                {
                    _logger.LogDebug("Skipping IM relay for own message on account {AccountId}", context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Skip relaying if this IM is from the relay avatar itself (avoid loops)
                if (message.SenderId.Equals(account.AvatarRelayUuid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping IM relay from relay avatar itself on account {AccountId}", context.AccountId);
                    return ChatProcessingResult.CreateSuccess();
                }

                // Truncate message to 800 characters max
                var relayMessage = message.Message;
                if (relayMessage.Length > 803)
                {
                    relayMessage = relayMessage.Substring(0, 800) + "...";
                }

                // Format the relay message
                var formattedMessage = $"[IM RELAY] secondlife:///app/agent/{message.SenderId}/about containing: {relayMessage}";

                // Ensure relay avatar name is cached before sending IM
                var nameResolutionService = scope.ServiceProvider.GetRequiredService<INameResolutionService>();
                var globalDisplayNameCache = scope.ServiceProvider.GetRequiredService<IGlobalDisplayNameCache>();
                
                // Pre-cache the relay avatar's name so it shows correctly in our chat history
                var relayAvatarName = await nameResolutionService.ResolveAgentNameAsync(
                    context.AccountId, 
                    new UUID(account.AvatarRelayUuid), 
                    ResolveType.AgentDefaultName, 
                    3000); // 3 second timeout
                
                // Also cache in global display name cache for consistency
                await globalDisplayNameCache.RequestDisplayNamesAsync(
                    new List<string> { account.AvatarRelayUuid }, 
                    context.AccountId);
                
                _logger.LogDebug("Cached relay avatar name '{RelayName}' for outgoing IM relay to {RelayUuid} on account {AccountId}", 
                    relayAvatarName, account.AvatarRelayUuid, context.AccountId);

                // Send the IM to the relay avatar
                if (context.AccountInstance != null && context.AccountInstance.IsConnected)
                {
                    context.AccountInstance.SendIM(account.AvatarRelayUuid!, formattedMessage);
                    _logger.LogInformation("Relayed IM from {SenderName} to relay avatar {RelayUuid} for account {AccountId}", 
                        message.SenderName, account.AvatarRelayUuid, context.AccountId);
                }
                else
                {
                    _logger.LogWarning("Cannot relay IM - account {AccountId} instance is not connected", context.AccountId);
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing IM relay for account {AccountId}", context.AccountId);
                return ChatProcessingResult.CreateSuccess(); // Continue processing even if relay fails
            }
        }
    }

    /// <summary>
    /// Processor for filtering out messages from ignored groups early in the pipeline
    /// </summary>
    internal class GroupIgnoreFilterProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public GroupIgnoreFilterProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "Group Ignore Filter";
        public int Priority => 5;

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            // Only check group messages
            if (message.ChatType?.ToLower() != "group" || string.IsNullOrEmpty(message.TargetId))
                return ChatProcessingResult.CreateSuccess();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var groupService = scope.ServiceProvider.GetRequiredService<IGroupService>();
                
                var isIgnored = await groupService.IsGroupIgnoredAsync(context.AccountId, message.TargetId);
                
                if (isIgnored)
                {
                    _logger.LogDebug("Filtering out message from ignored group {GroupId} on account {AccountId}", 
                        message.TargetId, context.AccountId);
                    
                    // Stop processing entirely - this message should not be processed further
                    return ChatProcessingResult.CreateSuccessStop();
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking group ignore status for {GroupId} on account {AccountId}", 
                    message.TargetId, context.AccountId);
                
                // On error, continue processing to avoid breaking the pipeline
                return ChatProcessingResult.CreateSuccess();
            }
        }
    }

    /// <summary>
    /// Processor for handling group chat and IM teleport commands (e.g., "azza tp")
    /// Detects when someone requests a teleport from a specific account by name
    /// </summary>
    internal class GroupTeleportCommandProcessor : IChatMessageProcessor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public GroupTeleportCommandProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public string Name => "Group/IM Teleport Command";
        public int Priority => 12; // Run after ignore filter but before most other processors

        public async Task<ChatProcessingResult> ProcessAsync(ChatMessageDto message, ChatProcessingContext context)
        {
            // Only process group messages and IMs (ignore open chat as person would already be near)
            var chatTypeLower = message.ChatType?.ToLower() ?? "";
            if (chatTypeLower != "group" && chatTypeLower != "im")
                return ChatProcessingResult.CreateSuccess();

            // Ignore messages from the account itself
            if (context.AccountInstance != null && 
                message.SenderId == context.AccountInstance.Client.Self.AgentID.ToString())
                return ChatProcessingResult.CreateSuccess();

            try
            {
                // Check if message matches the pattern "[name] tp" (case-insensitive)
                var messageLower = message.Message?.ToLower().Trim() ?? "";
                
                // Match patterns like "azza tp", "john tp", etc.
                var words = messageLower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (words.Length == 2 && words[1] == "tp")
                {
                    var requestedName = words[0];
                    
                    _logger.LogDebug("Detected teleport request for '{RequestedName}' in {ChatType} from {SenderName}", 
                        requestedName, message.ChatType, message.SenderName);

                    using var scope = _serviceProvider.CreateScope();
                    var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();

                    // Get all accounts and filter by both first name and display name
                    var allAccounts = await accountService.GetAccountsAsync();
                    var matchingAccounts = allAccounts.Where(a =>
                    {
                        // Check if first name matches
                        if (a.FirstName.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
                            return true;
                        
                        // Check if first word of display name matches
                        if (!string.IsNullOrWhiteSpace(a.DisplayName))
                        {
                            var displayNameFirstWord = a.DisplayName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (displayNameFirstWord != null && 
                                displayNameFirstWord.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        
                        return false;
                    });
                    
                    var connectedAccounts = matchingAccounts
                        .Where(a => accountService.GetInstance(a.Id)?.IsConnected == true)
                        .ToList();

                    if (!connectedAccounts.Any())
                    {
                        _logger.LogDebug("No connected accounts found with first name or display name matching '{RequestedName}'", requestedName);
                        return ChatProcessingResult.CreateSuccess();
                    }

                    // Use the first matching connected account
                    var accountToUse = connectedAccounts.First();
                    
                    // Get the requester's agent ID from the sender ID
                    if (string.IsNullOrEmpty(message.SenderId))
                    {
                        _logger.LogWarning("Cannot send teleport lure - sender ID is missing");
                        return ChatProcessingResult.CreateSuccess();
                    }

                    // Send the teleport lure
                    var success = await accountService.SendTeleportLureAsync(
                        accountToUse.Id, 
                        message.SenderId, 
                        "Join me!");

                    if (success)
                    {
                        var matchedName = accountToUse.FirstName.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
                            ? $"FirstName: {accountToUse.FirstName}"
                            : $"DisplayName: {accountToUse.DisplayName}";
                        
                        _logger.LogInformation(
                            "Sent teleport lure from {AccountName} (matched {MatchedName}, AccountId: {AccountId}) to {RequesterName} ({RequesterId}) in response to {ChatType} request",
                            accountToUse.DisplayName ?? accountToUse.FirstName, matchedName, accountToUse.Id, message.SenderName, message.SenderId, message.ChatType);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Failed to send teleport lure from {AccountName} ({AccountId}) to {RequesterName} ({RequesterId}) via {ChatType}",
                            accountToUse.DisplayName ?? accountToUse.FirstName, accountToUse.Id, message.SenderName, message.SenderId, message.ChatType);
                    }
                }

                return ChatProcessingResult.CreateSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing group teleport command for message from {SenderName}", 
                    message.SenderName);
                return ChatProcessingResult.CreateSuccess(); // Continue processing on error
            }
        }
    }
}