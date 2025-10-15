using Microsoft.AspNetCore.SignalR;
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
        private readonly ConcurrentDictionary<string, (IChatMessageProcessor processor, int priority)> _processors;

        public ChatProcessingService(
            IServiceProvider serviceProvider,
            ILogger<ChatProcessingService> logger,
            IHubContext<RadegastHub, IRadegastHubClient> hubContext)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _hubContext = hubContext;
            _processors = new ConcurrentDictionary<string, (IChatMessageProcessor, int)>();

            // Register built-in processors
            RegisterBuiltInProcessors();
        }

        private void RegisterBuiltInProcessors()
        {
            // Register processors in priority order (lower numbers first)
            RegisterProcessor(new UrlProcessingProcessor(_serviceProvider, _logger), 10);
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
                            if (message.ChatType == "Normal")
                            {
                                context.AccountInstance.SendChat(result.ResponseMessage);
                            }
                            else if (message.ChatType == "IM" && !string.IsNullOrEmpty(message.SenderId))
                            {
                                context.AccountInstance.SendIM(result.ResponseMessage, message.SenderId);
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
                        SessionName = message.SessionName
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
            // Only process whisper messages for Corrade commands
            if (message.ChatType != "Whisper" || string.IsNullOrEmpty(message.SenderId))
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
            // Only process normal chat from agents for AI
            if (message.ChatType != "Normal" || string.IsNullOrEmpty(message.SenderId))
                return ChatProcessingResult.CreateSuccess();

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
}