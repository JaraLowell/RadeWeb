using OpenMetaverse;
using RadegastWeb.Models;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public interface ITeleportRequestService
    {
        /// <summary>
        /// Event fired when a teleport request is received
        /// </summary>
        event EventHandler<TeleportRequestEventArgs>? TeleportRequestReceived;
        
        /// <summary>
        /// Event fired when a teleport request is closed/expired
        /// </summary>
        event EventHandler<string>? TeleportRequestClosed;
        
        /// <summary>
        /// Handle a received teleport request
        /// </summary>
        Task HandleTeleportRequestAsync(Guid accountId, UUID fromAgentId, string fromAgentName, string message, UUID sessionId);
        
        /// <summary>
        /// Respond to a teleport request
        /// </summary>
        Task<bool> RespondToTeleportRequestAsync(TeleportRequestResponseRequest request);
        
        /// <summary>
        /// Get active teleport requests for an account
        /// </summary>
        Task<IEnumerable<TeleportRequestDto>> GetActiveTeleportRequestsAsync(Guid accountId);
        
        /// <summary>
        /// Clean up expired teleport requests
        /// </summary>
        Task CleanupExpiredRequestsAsync();
    }
    
    public class TeleportRequestService : ITeleportRequestService
    {
        private readonly ILogger<TeleportRequestService> _logger;
        private readonly IAccountService _accountService;
        private readonly ConcurrentDictionary<string, TeleportRequestDto> _activeRequests = new();
        private readonly System.Threading.Timer _cleanupTimer;
        
        public event EventHandler<TeleportRequestEventArgs>? TeleportRequestReceived;
        public event EventHandler<string>? TeleportRequestClosed;
        
        public TeleportRequestService(ILogger<TeleportRequestService> logger, IAccountService accountService)
        {
            _logger = logger;
            _accountService = accountService;
            
            // Setup cleanup timer to run every 2 minutes
            _cleanupTimer = new System.Threading.Timer(
                async _ => await CleanupExpiredRequestsAsync(),
                null,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2)
            );
        }
        
        public async Task HandleTeleportRequestAsync(Guid accountId, UUID fromAgentId, string fromAgentName, string message, UUID sessionId)
        {
            try
            {
                var requestId = Guid.NewGuid().ToString();
                
                var request = new TeleportRequestDto
                {
                    RequestId = requestId,
                    AccountId = accountId,
                    FromAgentId = fromAgentId.ToString(),
                    FromAgentName = fromAgentName,
                    Message = message,
                    SessionId = sessionId.ToString(),
                    ReceivedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5) // Teleport offers expire after 5 minutes
                };
                
                // Store the request
                _activeRequests[requestId] = request;
                
                _logger.LogInformation("Teleport request received for account {AccountId} from {FromAgentName} ({FromAgentId})", 
                    accountId, fromAgentName, fromAgentId);
                
                // Fire event for SignalR broadcast
                TeleportRequestReceived?.Invoke(this, new TeleportRequestEventArgs(request));
                
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling teleport request for account {AccountId}", accountId);
            }
        }
        
        public Task<bool> RespondToTeleportRequestAsync(TeleportRequestResponseRequest request)
        {
            try
            {
                if (!_activeRequests.TryGetValue(request.RequestId, out var teleportRequest))
                {
                    _logger.LogWarning("Teleport request {RequestId} not found for account {AccountId}", request.RequestId, request.AccountId);
                    return Task.FromResult(false);
                }
                
                if (teleportRequest.AccountId != request.AccountId)
                {
                    _logger.LogWarning("Account {AccountId} attempted to respond to teleport request {RequestId} belonging to account {RequestAccountId}", 
                        request.AccountId, request.RequestId, teleportRequest.AccountId);
                    return Task.FromResult(false);
                }
                
                if (teleportRequest.IsResponded)
                {
                    _logger.LogWarning("Teleport request {RequestId} has already been responded to", request.RequestId);
                    return Task.FromResult(false);
                }
                
                var instance = _accountService.GetInstance(request.AccountId);
                if (instance == null || !instance.IsConnected)
                {
                    _logger.LogWarning("Account {AccountId} not found or not connected", request.AccountId);
                    return Task.FromResult(false);
                }
                
                // Send the teleport lure response to Second Life
                var fromAgentId = UUID.Parse(teleportRequest.FromAgentId);
                var sessionId = UUID.Parse(teleportRequest.SessionId);
                
                instance.Client.Self.TeleportLureRespond(fromAgentId, sessionId, request.Accept);
                
                _logger.LogInformation("Sent teleport response ({Accept}) to {FromAgentName} for account {AccountId}", 
                    request.Accept ? "Accept" : "Decline", teleportRequest.FromAgentName, request.AccountId);
                
                // Mark as responded and remove from active requests
                teleportRequest.IsResponded = true;
                _activeRequests.TryRemove(request.RequestId, out _);
                
                // Fire event for cleanup
                TeleportRequestClosed?.Invoke(this, request.RequestId);
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to teleport request {RequestId} for account {AccountId}", 
                    request.RequestId, request.AccountId);
                return Task.FromResult(false);
            }
        }
        
        public Task<IEnumerable<TeleportRequestDto>> GetActiveTeleportRequestsAsync(Guid accountId)
        {
            var requests = _activeRequests.Values
                .Where(r => r.AccountId == accountId && !r.IsResponded && r.ExpiresAt > DateTime.UtcNow)
                .OrderBy(r => r.ReceivedAt)
                .AsEnumerable();
            
            return Task.FromResult(requests);
        }
        
        public Task CleanupExpiredRequestsAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredRequests = _activeRequests.Values
                    .Where(r => r.ExpiresAt < now)
                    .ToList();
                
                foreach (var request in expiredRequests)
                {
                    _activeRequests.TryRemove(request.RequestId, out _);
                    _logger.LogDebug("Cleaned up expired teleport request {RequestId} for account {AccountId}", 
                        request.RequestId, request.AccountId);
                    
                    // Fire event for cleanup
                    TeleportRequestClosed?.Invoke(this, request.RequestId);
                }
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during teleport request cleanup");
                return Task.CompletedTask;
            }
        }
        
        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
    }
}