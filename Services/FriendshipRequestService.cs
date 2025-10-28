using Microsoft.EntityFrameworkCore;
using OpenMetaverse;
using RadegastWeb.Core;
using RadegastWeb.Data;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for handling friendship requests
    /// </summary>
    public class FriendshipRequestService : IFriendshipRequestService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IAccountService _accountService;
        private readonly ILogger<FriendshipRequestService> _logger;
        
        // In-memory store for active friendship requests (could be moved to database if needed)
        private readonly Dictionary<string, FriendshipRequestDto> _activeRequests = new();
        private readonly object _requestsLock = new object();
        
        public event EventHandler<FriendshipRequestEventArgs>? FriendshipRequestReceived;
        
        public FriendshipRequestService(
            IServiceProvider serviceProvider,
            IAccountService accountService,
            ILogger<FriendshipRequestService> logger)
        {
            _serviceProvider = serviceProvider;
            _accountService = accountService;
            _logger = logger;
        }
        
        public Task<IEnumerable<FriendshipRequestDto>> GetActiveFriendshipRequestsAsync(Guid accountId)
        {
            lock (_requestsLock)
            {
                var activeRequests = _activeRequests.Values
                    .Where(r => r.AccountId == accountId && !r.IsResponded && r.ExpiresAt > DateTime.UtcNow)
                    .ToList();
                    
                return Task.FromResult(activeRequests.AsEnumerable());
            }
        }
        
        public async Task<bool> RespondToFriendshipRequestAsync(FriendshipRequestResponseRequest request)
        {
            try
            {
                FriendshipRequestDto? friendshipRequest;
                lock (_requestsLock)
                {
                    if (!_activeRequests.TryGetValue(request.RequestId, out friendshipRequest))
                    {
                        _logger.LogWarning("Friendship request {RequestId} not found", request.RequestId);
                        return false;
                    }
                    
                    if (friendshipRequest.IsResponded)
                    {
                        _logger.LogWarning("Friendship request {RequestId} already responded to", request.RequestId);
                        return false;
                    }
                    
                    // Mark as responded
                    friendshipRequest.IsResponded = true;
                }
                
                // Get the account instance
                var instance = _accountService.GetInstance(request.AccountId);
                if (instance == null || !instance.IsConnected)
                {
                    _logger.LogError("Account {AccountId} not found or not connected", request.AccountId);
                    return false;
                }
                
                // Parse the agent ID and session ID
                if (!UUID.TryParse(friendshipRequest.FromAgentId, out var fromAgentId) ||
                    !UUID.TryParse(friendshipRequest.SessionId, out var sessionId))
                {
                    _logger.LogError("Invalid agent ID or session ID in friendship request {RequestId}", request.RequestId);
                    return false;
                }
                
                // Send the response to Second Life
                if (request.Accept)
                {
                    instance.Client.Friends.AcceptFriendship(fromAgentId, sessionId);
                    _logger.LogInformation("Accepted friendship offer from {FromAgentName} ({FromAgentId}) for account {AccountId}",
                        friendshipRequest.FromAgentName, friendshipRequest.FromAgentId, request.AccountId);
                }
                else
                {
                    instance.Client.Friends.DeclineFriendship(fromAgentId, sessionId);
                    _logger.LogInformation("Declined friendship offer from {FromAgentName} ({FromAgentId}) for account {AccountId}",
                        friendshipRequest.FromAgentName, friendshipRequest.FromAgentId, request.AccountId);
                }
                
                // Update the interactive notice if one exists
                await UpdateInteractiveNoticeAsync(request.AccountId, request.RequestId, request.Accept);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to friendship request {RequestId} for account {AccountId}",
                    request.RequestId, request.AccountId);
                return false;
            }
        }
        
        public Task<FriendshipRequestDto> StoreFriendshipRequestAsync(FriendshipRequestDto request)
        {
            lock (_requestsLock)
            {
                _activeRequests[request.RequestId] = request;
            }
            
            _logger.LogInformation("Stored friendship request {RequestId} from {FromAgentName} for account {AccountId}",
                request.RequestId, request.FromAgentName, request.AccountId);
                
            // Fire the event
            FriendshipRequestReceived?.Invoke(this, new FriendshipRequestEventArgs(request));
            
            return Task.FromResult(request);
        }
        
        public Task CleanupExpiredRequestsAsync()
        {
            lock (_requestsLock)
            {
                var expiredRequests = _activeRequests
                    .Where(kvp => kvp.Value.ExpiresAt <= DateTime.UtcNow)
                    .Select(kvp => kvp.Key)
                    .ToList();
                    
                foreach (var requestId in expiredRequests)
                {
                    _activeRequests.Remove(requestId);
                    _logger.LogDebug("Removed expired friendship request {RequestId}", requestId);
                }
            }
            
            return Task.CompletedTask;
        }
        
        private async Task UpdateInteractiveNoticeAsync(Guid accountId, string requestId, bool accepted)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                using var context = scope.ServiceProvider.GetRequiredService<RadegastDbContext>();
                
                var notice = await context.Notices
                    .FirstOrDefaultAsync(n => n.AccountId == accountId && 
                                            n.ExternalRequestId == requestId && 
                                            n.Type == "FriendshipOffer");
                
                if (notice != null)
                {
                    notice.HasResponse = true;
                    notice.AcceptedResponse = accepted;
                    notice.RespondedAt = DateTime.UtcNow;
                    notice.IsRead = true; // Mark as read when responded
                    
                    await context.SaveChangesAsync();
                    _logger.LogDebug("Updated interactive notice for friendship request {RequestId}", requestId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating interactive notice for friendship request {RequestId}", requestId);
            }
        }
    }
}