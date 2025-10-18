using OpenMetaverse;
using RadegastWeb.Models;
using System.Collections.Concurrent;
using System.Text.Json;

namespace RadegastWeb.Services
{
    public class GroupService : IGroupService
    {
        private readonly ILogger<GroupService> _logger;
        private readonly ConcurrentDictionary<Guid, Dictionary<UUID, Group>> _accountGroups = new();
        private readonly ConcurrentDictionary<Guid, DateTime> _lastCacheUpdate = new();
        private readonly ConcurrentDictionary<Guid, HashSet<UUID>> _ignoredGroups = new();
        private readonly SemaphoreSlim _cacheSemaphore = new(1, 1);

        public event EventHandler<GroupsUpdatedEventArgs>? GroupsUpdated;

        public GroupService(ILogger<GroupService> logger)
        {
            _logger = logger;
        }

        public async Task LoadGroupsCacheAsync(Guid accountId)
        {
            await _cacheSemaphore.WaitAsync();
            try
            {
                var cacheFilePath = GetGroupsCacheFilePath(accountId);
                
                if (!File.Exists(cacheFilePath))
                {
                    _logger.LogDebug("No groups cache file found for account {AccountId}", accountId);
                    _accountGroups[accountId] = new Dictionary<UUID, Group>();
                }
                else
                {
                    _logger.LogDebug("Loading groups cache from {CacheFilePath}", cacheFilePath);
                    
                    var jsonContent = await File.ReadAllTextAsync(cacheFilePath);
                    var groupsData = JsonSerializer.Deserialize<List<SerializableGroup>>(jsonContent);
                    
                    if (groupsData != null)
                    {
                        var groups = new Dictionary<UUID, Group>();
                        
                        foreach (var groupData in groupsData)
                        {
                            groups[groupData.Id] = groupData.ToGroup();
                        }
                        
                        _accountGroups[accountId] = groups;
                        _lastCacheUpdate[accountId] = DateTime.UtcNow;
                        
                        _logger.LogInformation("Loaded {Count} groups from cache for account {AccountId}", 
                            groups.Count, accountId);
                    }
                    else
                    {
                        _accountGroups[accountId] = new Dictionary<UUID, Group>();
                    }
                }

                // Load ignored groups from separate file
                await LoadIgnoredGroupsCacheAsync(accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading groups cache for account {AccountId}", accountId);
                _accountGroups[accountId] = new Dictionary<UUID, Group>();
                _ignoredGroups[accountId] = new HashSet<UUID>();
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        public async Task SaveGroupsCacheAsync(Guid accountId, Dictionary<UUID, Group> groups)
        {
            await _cacheSemaphore.WaitAsync();
            try
            {
                var cacheFilePath = GetGroupsCacheFilePath(accountId);
                var cacheDir = Path.GetDirectoryName(cacheFilePath);
                
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                // Save groups without ignore status (ignore status is in separate file)
                var groupsData = groups.Values.Select(g => SerializableGroup.FromGroup(g)).ToList();
                
                var jsonContent = JsonSerializer.Serialize(groupsData, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                await File.WriteAllTextAsync(cacheFilePath, jsonContent);
                _lastCacheUpdate[accountId] = DateTime.UtcNow;
                
                _logger.LogDebug("Saved {Count} groups to cache for account {AccountId}", 
                    groups.Count, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving groups cache for account {AccountId}", accountId);
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        public async Task<Dictionary<UUID, Group>> GetCachedGroupsAsync(Guid accountId)
        {
            if (!_accountGroups.TryGetValue(accountId, out var groups))
            {
                await LoadGroupsCacheAsync(accountId);
                groups = _accountGroups.GetValueOrDefault(accountId, new Dictionary<UUID, Group>());
            }
            
            return new Dictionary<UUID, Group>(groups); // Return a copy
        }

        public async Task UpdateGroupsAsync(Guid accountId, Dictionary<UUID, Group> groups)
        {
            try
            {
                // Update in-memory cache
                _accountGroups[accountId] = new Dictionary<UUID, Group>(groups);
                
                // Save to disk cache (preserves ignore status)
                await SaveGroupsCacheAsync(accountId, groups);
                
                // Convert to DTOs with ignore status and fire event
                var ignoredGroups = _ignoredGroups.GetValueOrDefault(accountId, new HashSet<UUID>());
                var groupDtos = groups.Values.Select(g => 
                    GroupDtoExtensions.FromGroup(g, accountId, ignoredGroups.Contains(g.ID)));
                GroupsUpdated?.Invoke(this, new GroupsUpdatedEventArgs(accountId, groupDtos));
                
                _logger.LogInformation("Updated {Count} groups for account {AccountId}", 
                    groups.Count, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating groups for account {AccountId}", accountId);
            }
        }

        public async Task<GroupDto?> GetGroupAsync(Guid accountId, string groupId)
        {
            try
            {
                if (!UUID.TryParse(groupId, out var groupUuid))
                {
                    return null;
                }

                var groups = await GetCachedGroupsAsync(accountId);
                if (groups.TryGetValue(groupUuid, out var group))
                {
                    var ignoredGroups = _ignoredGroups.GetValueOrDefault(accountId, new HashSet<UUID>());
                    return GroupDtoExtensions.FromGroup(group, accountId, ignoredGroups.Contains(groupUuid));
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting group {GroupId} for account {AccountId}", groupId, accountId);
                return null;
            }
        }

        public async Task<IEnumerable<GroupDto>> GetGroupsAsync(Guid accountId)
        {
            try
            {
                var groups = await GetCachedGroupsAsync(accountId);
                var ignoredGroups = _ignoredGroups.GetValueOrDefault(accountId, new HashSet<UUID>());
                return groups.Values.Select(g => GroupDtoExtensions.FromGroup(g, accountId, ignoredGroups.Contains(g.ID)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting groups for account {AccountId}", accountId);
                return Enumerable.Empty<GroupDto>();
            }
        }

        public async Task<string> GetGroupNameAsync(Guid accountId, string groupId, string fallbackName = "Unknown Group")
        {
            try
            {
                if (!UUID.TryParse(groupId, out var groupUuid))
                {
                    return fallbackName;
                }

                var groups = await GetCachedGroupsAsync(accountId);
                if (groups.TryGetValue(groupUuid, out var group))
                {
                    return group.Name;
                }
                
                return fallbackName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting group name for {GroupId} on account {AccountId}", groupId, accountId);
                return fallbackName;
            }
        }

        public async Task SetGroupIgnoreStatusAsync(Guid accountId, string groupId, bool isIgnored)
        {
            try
            {
                if (!UUID.TryParse(groupId, out var groupUuid))
                {
                    _logger.LogWarning("Invalid group ID: {GroupId}", groupId);
                    return;
                }

                // Get or create the ignored groups set for this account
                var ignoredGroups = _ignoredGroups.GetOrAdd(accountId, _ => new HashSet<UUID>());

                // Update the ignored status
                if (isIgnored)
                {
                    ignoredGroups.Add(groupUuid);
                    _logger.LogInformation("Group {GroupId} marked as ignored for account {AccountId}", groupId, accountId);
                }
                else
                {
                    ignoredGroups.Remove(groupUuid);
                    _logger.LogInformation("Group {GroupId} unmarked as ignored for account {AccountId}", groupId, accountId);
                }

                // Save the ignored groups to separate cache file
                await SaveIgnoredGroupsCacheAsync(accountId);

                // Fire groups updated event with updated ignore status
                var groups = await GetCachedGroupsAsync(accountId);
                var groupDtos = groups.Values.Select(g => 
                    GroupDtoExtensions.FromGroup(g, accountId, ignoredGroups.Contains(g.ID)));
                GroupsUpdated?.Invoke(this, new GroupsUpdatedEventArgs(accountId, groupDtos));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting ignore status for group {GroupId} on account {AccountId}", groupId, accountId);
            }
        }

        public async Task<bool> IsGroupIgnoredAsync(Guid accountId, string groupId)
        {
            try
            {
                if (!UUID.TryParse(groupId, out var groupUuid))
                {
                    return false;
                }

                // Ensure groups are loaded
                if (!_ignoredGroups.ContainsKey(accountId))
                {
                    await LoadGroupsCacheAsync(accountId);
                }

                var ignoredGroups = _ignoredGroups.GetValueOrDefault(accountId, new HashSet<UUID>());
                return ignoredGroups.Contains(groupUuid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking ignore status for group {GroupId} on account {AccountId}", groupId, accountId);
                return false;
            }
        }

        public void CleanupAccount(Guid accountId)
        {
            try
            {
                _accountGroups.TryRemove(accountId, out _);
                _lastCacheUpdate.TryRemove(accountId, out _);
                _ignoredGroups.TryRemove(accountId, out _);
                
                _logger.LogDebug("Cleaned up groups cache for account {AccountId}", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up groups cache for account {AccountId}", accountId);
            }
        }

        private static string GetGroupsCacheFilePath(Guid accountId)
        {
            return Path.Combine("data", "accounts", accountId.ToString(), "cache", "groups.json");
        }

        private static string GetIgnoredGroupsCacheFilePath(Guid accountId)
        {
            return Path.Combine("data", "accounts", accountId.ToString(), "cache", "ignored_groups.json");
        }

        private async Task LoadIgnoredGroupsCacheAsync(Guid accountId)
        {
            try
            {
                var cacheFilePath = GetIgnoredGroupsCacheFilePath(accountId);
                
                if (!File.Exists(cacheFilePath))
                {
                    _logger.LogDebug("No ignored groups cache file found for account {AccountId}", accountId);
                    _ignoredGroups[accountId] = new HashSet<UUID>();
                    return;
                }

                _logger.LogDebug("Loading ignored groups cache from {CacheFilePath}", cacheFilePath);
                
                var jsonContent = await File.ReadAllTextAsync(cacheFilePath);
                var ignoredGroupIds = JsonSerializer.Deserialize<List<string>>(jsonContent);
                
                if (ignoredGroupIds != null)
                {
                    var ignoredGroups = new HashSet<UUID>();
                    foreach (var groupIdStr in ignoredGroupIds)
                    {
                        if (UUID.TryParse(groupIdStr, out var groupId))
                        {
                            ignoredGroups.Add(groupId);
                        }
                    }
                    
                    _ignoredGroups[accountId] = ignoredGroups;
                    _logger.LogInformation("Loaded {Count} ignored groups from cache for account {AccountId}", 
                        ignoredGroups.Count, accountId);
                }
                else
                {
                    _ignoredGroups[accountId] = new HashSet<UUID>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading ignored groups cache for account {AccountId}", accountId);
                _ignoredGroups[accountId] = new HashSet<UUID>();
            }
        }

        private async Task SaveIgnoredGroupsCacheAsync(Guid accountId)
        {
            try
            {
                var cacheFilePath = GetIgnoredGroupsCacheFilePath(accountId);
                var cacheDir = Path.GetDirectoryName(cacheFilePath);
                
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                var ignoredGroups = _ignoredGroups.GetValueOrDefault(accountId, new HashSet<UUID>());
                var ignoredGroupIds = ignoredGroups.Select(g => g.ToString()).ToList();
                
                var jsonContent = JsonSerializer.Serialize(ignoredGroupIds, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                await File.WriteAllTextAsync(cacheFilePath, jsonContent);
                
                _logger.LogDebug("Saved {Count} ignored groups to cache for account {AccountId}", 
                    ignoredGroups.Count, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving ignored groups cache for account {AccountId}", accountId);
            }
        }

        /// <summary>
        /// Serializable version of Group for JSON persistence
        /// </summary>
        private class SerializableGroup
        {
            public UUID Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Charter { get; set; } = string.Empty;
            public bool ShowInList { get; set; }
            public UUID InsigniaID { get; set; }
            public UUID FounderID { get; set; }
            public int MembershipFee { get; set; }
            public bool OpenEnrollment { get; set; }
            public int GroupMembershipCount { get; set; }
            public int GroupRolesCount { get; set; }
            public bool AcceptNotices { get; set; }
            public bool ListInProfile { get; set; }
            public bool MaturePublish { get; set; }
            // Note: GroupPowers property doesn't exist in libOpenMetaverse Group class

            public static SerializableGroup FromGroup(Group group)
            {
                return new SerializableGroup
                {
                    Id = group.ID,
                    Name = group.Name,
                    Charter = group.Charter,
                    ShowInList = group.ShowInList,
                    InsigniaID = group.InsigniaID,
                    FounderID = group.FounderID,
                    MembershipFee = group.MembershipFee,
                    OpenEnrollment = group.OpenEnrollment,
                    GroupMembershipCount = group.GroupMembershipCount,
                    GroupRolesCount = group.GroupRolesCount,
                    AcceptNotices = group.AcceptNotices,
                    ListInProfile = group.ListInProfile,
                    MaturePublish = group.MaturePublish
                };
            }

            public Group ToGroup()
            {
                return new Group
                {
                    ID = Id,
                    Name = Name,
                    Charter = Charter,
                    ShowInList = ShowInList,
                    InsigniaID = InsigniaID,
                    FounderID = FounderID,
                    MembershipFee = MembershipFee,
                    OpenEnrollment = OpenEnrollment,
                    GroupMembershipCount = GroupMembershipCount,
                    GroupRolesCount = GroupRolesCount,
                    AcceptNotices = AcceptNotices,
                    ListInProfile = ListInProfile,
                    MaturePublish = MaturePublish
                };
            }
        }
    }

    /// <summary>
    /// Extension methods for GroupDto
    /// </summary>
    public static class GroupDtoExtensions
    {
        public static GroupDto FromGroup(this GroupDto _, Group group, Guid accountId, bool isIgnored = false)
        {
            return new GroupDto
            {
                Id = group.ID.ToString(),
                Name = group.Name,
                Charter = group.Charter,
                ShowInList = group.ShowInList,
                InsigniaId = group.InsigniaID.ToString(),
                FounderId = group.FounderID.ToString(),
                MembershipFee = group.MembershipFee,
                OpenEnrollment = group.OpenEnrollment,
                MemberCount = group.GroupMembershipCount,
                RoleCount = group.GroupRolesCount,
                AcceptNotices = group.AcceptNotices,
                ListInProfile = group.ListInProfile,
                MaturePublish = group.MaturePublish,
                GroupPowers = "0", // Default to no powers since Group class doesn't expose this
                AccountId = accountId,
                IsIgnored = isIgnored
            };
        }

        public static GroupDto FromGroup(Group group, Guid accountId, bool isIgnored = false)
        {
            return new GroupDto().FromGroup(group, accountId, isIgnored);
        }
    }
}