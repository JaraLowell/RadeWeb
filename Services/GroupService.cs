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
                    return;
                }

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading groups cache for account {AccountId}", accountId);
                _accountGroups[accountId] = new Dictionary<UUID, Group>();
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
                
                // Save to disk cache
                await SaveGroupsCacheAsync(accountId, groups);
                
                // Convert to DTOs and fire event
                var groupDtos = groups.Values.Select(g => GroupDtoExtensions.FromGroup(g, accountId));
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
                    return GroupDtoExtensions.FromGroup(group, accountId);
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
                return groups.Values.Select(g => GroupDtoExtensions.FromGroup(g, accountId));
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

        public void CleanupAccount(Guid accountId)
        {
            try
            {
                _accountGroups.TryRemove(accountId, out _);
                _lastCacheUpdate.TryRemove(accountId, out _);
                
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
        public static GroupDto FromGroup(this GroupDto _, Group group, Guid accountId)
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
                AccountId = accountId
            };
        }

        public static GroupDto FromGroup(Group group, Guid accountId)
        {
            return new GroupDto().FromGroup(group, accountId);
        }
    }
}