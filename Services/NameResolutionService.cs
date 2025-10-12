using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using OpenMetaverse;
using RadegastWeb.Core;

namespace RadegastWeb.Services
{
    public class NameResolutionService : INameResolutionService
    {
        private readonly ILogger<NameResolutionService> _logger;
        private readonly IMemoryCache _cache;
        private readonly ConcurrentDictionary<Guid, object> _instances;
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(15);

        public NameResolutionService(
            ILogger<NameResolutionService> logger,
            IMemoryCache cache)
        {
            _logger = logger;
            _cache = cache;
            _instances = new ConcurrentDictionary<Guid, object>();
        }

        public void RegisterInstance(Guid accountId, object instance)
        {
            if (instance is WebRadegastInstance webInstance)
            {
                _instances.AddOrUpdate(accountId, webInstance, (key, existing) => webInstance);
                _logger.LogDebug("Registered Radegast instance for account {AccountId}", accountId);
            }
        }

        public void UnregisterInstance(Guid accountId)
        {
            _instances.TryRemove(accountId, out _);
            _logger.LogDebug("Unregistered Radegast instance for account {AccountId}", accountId);
        }

        public async Task<string> ResolveAgentNameAsync(Guid accountId, UUID agentId, ResolveType resolveType = ResolveType.AgentDefaultName, int timeoutMs = 5000)
        {
            var cacheKey = $"agent_{accountId}_{agentId}_{resolveType}";
            
            // Check cache first
            if (_cache.TryGetValue(cacheKey, out string? cachedName) && !string.IsNullOrEmpty(cachedName))
            {
                return cachedName;
            }

            if (!_instances.TryGetValue(accountId, out var instanceObj) || 
                !(instanceObj is WebRadegastInstance instance) || 
                !instance.IsConnected)
            {
                return agentId.ToString();
            }

            try
            {
                string resolvedName = INameResolutionService.INCOMPLETE_NAME;
                var client = instance.Client;

                using var cts = new CancellationTokenSource(timeoutMs);
                var tcs = new TaskCompletionSource<string>();

                // Set up event handler for name response
                EventHandler<UUIDNameReplyEventArgs>? nameHandler = null;
                nameHandler = (sender, e) =>
                {
                    try
                    {
                        if (e.Names.ContainsKey(agentId))
                        {
                            resolvedName = e.Names[agentId];
                            tcs.TrySetResult(resolvedName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in name reply handler for agent {AgentId}", agentId);
                    }
                    finally
                    {
                        if (nameHandler != null)
                        {
                            client.Avatars.UUIDNameReply -= nameHandler;
                        }
                    }
                };

                client.Avatars.UUIDNameReply += nameHandler;

                // For display names, we need a different approach
                if (resolveType == ResolveType.AgentDisplayName)
                {
                    // We'll handle display names through the normal name resolution for now
                    // and integrate with the display name service later if needed
                }

                // Request name lookup
                var requestList = new List<UUID> { agentId };
                client.Avatars.RequestAvatarNames(requestList);

                // Wait for response or timeout
                try
                {
                    await tcs.Task.WaitAsync(cts.Token);
                    resolvedName = tcs.Task.Result;
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timeout resolving agent name for {AgentId} on account {AccountId}", agentId, accountId);
                    resolvedName = agentId.ToString();
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Cancelled resolving agent name for {AgentId} on account {AccountId}", agentId, accountId);
                    resolvedName = agentId.ToString();
                }
                finally
                {
                    client.Avatars.UUIDNameReply -= nameHandler;
                }

                // Cache the result (even if it's the UUID string)
                if (!string.IsNullOrEmpty(resolvedName) && resolvedName != INameResolutionService.INCOMPLETE_NAME)
                {
                    _cache.Set(cacheKey, resolvedName, _cacheExpiry);
                }

                return resolvedName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving agent name for {AgentId} on account {AccountId}", agentId, accountId);
                return agentId.ToString();
            }
        }

        public async Task<string> ResolveGroupNameAsync(Guid accountId, UUID groupId, int timeoutMs = 5000)
        {
            var cacheKey = $"group_{accountId}_{groupId}";
            
            // Check cache first
            if (_cache.TryGetValue(cacheKey, out string? cachedName) && !string.IsNullOrEmpty(cachedName))
            {
                return cachedName;
            }

            if (!_instances.TryGetValue(accountId, out var instanceObj) || 
                !(instanceObj is WebRadegastInstance instance) || 
                !instance.IsConnected)
            {
                return groupId.ToString();
            }

            try
            {
                string resolvedName = INameResolutionService.INCOMPLETE_NAME;
                var client = instance.Client;

                using var cts = new CancellationTokenSource(timeoutMs);
                var tcs = new TaskCompletionSource<string>();

                // Set up event handler for group name response
                EventHandler<GroupNamesEventArgs>? groupNameHandler = null;
                groupNameHandler = (sender, e) =>
                {
                    try
                    {
                        if (e.GroupNames.ContainsKey(groupId))
                        {
                            resolvedName = e.GroupNames[groupId];
                            tcs.TrySetResult(resolvedName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in group name reply handler for group {GroupId}", groupId);
                    }
                    finally
                    {
                        if (groupNameHandler != null)
                        {
                            client.Groups.GroupNamesReply -= groupNameHandler;
                        }
                    }
                };

                client.Groups.GroupNamesReply += groupNameHandler;

                // Request group name lookup
                var requestList = new List<UUID> { groupId };
                client.Groups.RequestGroupNames(requestList);

                // Wait for response or timeout
                try
                {
                    await tcs.Task.WaitAsync(cts.Token);
                    resolvedName = tcs.Task.Result;
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timeout resolving group name for {GroupId} on account {AccountId}", groupId, accountId);
                    resolvedName = groupId.ToString();
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Cancelled resolving group name for {GroupId} on account {AccountId}", groupId, accountId);
                    resolvedName = groupId.ToString();
                }
                finally
                {
                    client.Groups.GroupNamesReply -= groupNameHandler;
                }

                // Cache the result
                if (!string.IsNullOrEmpty(resolvedName) && resolvedName != INameResolutionService.INCOMPLETE_NAME)
                {
                    _cache.Set(cacheKey, resolvedName, _cacheExpiry);
                }

                return resolvedName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving group name for {GroupId} on account {AccountId}", groupId, accountId);
                return groupId.ToString();
            }
        }

        public async Task<string> ResolveParcelNameAsync(Guid accountId, UUID parcelId, int timeoutMs = 5000)
        {
            var cacheKey = $"parcel_{accountId}_{parcelId}";
            
            // Check cache first
            if (_cache.TryGetValue(cacheKey, out string? cachedName) && !string.IsNullOrEmpty(cachedName))
            {
                return cachedName;
            }

            if (!_instances.TryGetValue(accountId, out var instanceObj) || 
                !(instanceObj is WebRadegastInstance instance) || 
                !instance.IsConnected)
            {
                return parcelId.ToString();
            }

            try
            {
                string resolvedName = INameResolutionService.INCOMPLETE_NAME;
                var client = instance.Client;

                using var cts = new CancellationTokenSource(timeoutMs);
                var tcs = new TaskCompletionSource<string>();

                // Set up event handler for parcel info response
                EventHandler<ParcelInfoReplyEventArgs>? parcelHandler = null;
                parcelHandler = (sender, e) =>
                {
                    try
                    {
                        if (e.Parcel.ID == parcelId)
                        {
                            resolvedName = e.Parcel.Name ?? "Unknown Parcel";
                            tcs.TrySetResult(resolvedName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in parcel info reply handler for parcel {ParcelId}", parcelId);
                    }
                    finally
                    {
                        if (parcelHandler != null)
                        {
                            client.Parcels.ParcelInfoReply -= parcelHandler;
                        }
                    }
                };

                client.Parcels.ParcelInfoReply += parcelHandler;

                // Request parcel info
                client.Parcels.RequestParcelInfo(parcelId);

                // Wait for response or timeout
                try
                {
                    await tcs.Task.WaitAsync(cts.Token);
                    resolvedName = tcs.Task.Result;
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timeout resolving parcel name for {ParcelId} on account {AccountId}", parcelId, accountId);
                    resolvedName = parcelId.ToString();
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Cancelled resolving parcel name for {ParcelId} on account {AccountId}", parcelId, accountId);
                    resolvedName = parcelId.ToString();
                }
                finally
                {
                    client.Parcels.ParcelInfoReply -= parcelHandler;
                }

                // Cache the result
                if (!string.IsNullOrEmpty(resolvedName) && resolvedName != INameResolutionService.INCOMPLETE_NAME)
                {
                    _cache.Set(cacheKey, resolvedName, _cacheExpiry);
                }

                return resolvedName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving parcel name for {ParcelId} on account {AccountId}", parcelId, accountId);
                return parcelId.ToString();
            }
        }

        public async Task<string> ResolveAsync(Guid accountId, UUID id, ResolveType type, int timeoutMs = 5000)
        {
            return type switch
            {
                ResolveType.AgentDefaultName or ResolveType.AgentDisplayName or ResolveType.AgentUsername 
                    => await ResolveAgentNameAsync(accountId, id, type, timeoutMs),
                ResolveType.Group 
                    => await ResolveGroupNameAsync(accountId, id, timeoutMs),
                ResolveType.Parcel 
                    => await ResolveParcelNameAsync(accountId, id, timeoutMs),
                _ => id.ToString()
            };
        }

        public bool IsResolutionEnabled(Guid accountId)
        {
            // For now, always enabled if we have a connected instance
            return _instances.TryGetValue(accountId, out var instanceObj) && 
                   instanceObj is WebRadegastInstance instance && 
                   instance.IsConnected;
        }

        public string? GetCachedName(Guid accountId, UUID id, ResolveType type)
        {
            var cacheKey = type switch
            {
                ResolveType.Group => $"group_{accountId}_{id}",
                ResolveType.Parcel => $"parcel_{accountId}_{id}",
                _ => $"agent_{accountId}_{id}_{type}"
            };

            return _cache.TryGetValue(cacheKey, out string? cachedName) ? cachedName : null;
        }
    }
}