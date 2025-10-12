using OpenMetaverse;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    public interface IGroupService
    {
        /// <summary>
        /// Load groups from cache for the specified account
        /// </summary>
        Task LoadGroupsCacheAsync(Guid accountId);

        /// <summary>
        /// Save groups to cache for the specified account
        /// </summary>
        Task SaveGroupsCacheAsync(Guid accountId, Dictionary<UUID, Group> groups);

        /// <summary>
        /// Get cached groups for the specified account
        /// </summary>
        Task<Dictionary<UUID, Group>> GetCachedGroupsAsync(Guid accountId);

        /// <summary>
        /// Update groups for the specified account
        /// </summary>
        Task UpdateGroupsAsync(Guid accountId, Dictionary<UUID, Group> groups);

        /// <summary>
        /// Get group by ID for the specified account
        /// </summary>
        Task<GroupDto?> GetGroupAsync(Guid accountId, string groupId);

        /// <summary>
        /// Get all groups for the specified account
        /// </summary>
        Task<IEnumerable<GroupDto>> GetGroupsAsync(Guid accountId);

        /// <summary>
        /// Get group name by ID for the specified account
        /// </summary>
        Task<string> GetGroupNameAsync(Guid accountId, string groupId, string fallbackName = "Unknown Group");

        /// <summary>
        /// Clean up account resources
        /// </summary>
        void CleanupAccount(Guid accountId);

        /// <summary>
        /// Event fired when groups are updated for an account
        /// </summary>
        event EventHandler<GroupsUpdatedEventArgs> GroupsUpdated;
    }

    public class GroupDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Charter { get; set; } = string.Empty;
        public bool ShowInList { get; set; }
        public string InsigniaId { get; set; } = string.Empty;
        public string FounderId { get; set; } = string.Empty;
        public int MembershipFee { get; set; }
        public bool OpenEnrollment { get; set; }
        public int MemberCount { get; set; }
        public int RoleCount { get; set; }
        public bool AcceptNotices { get; set; }
        public bool ListInProfile { get; set; }
        public bool MaturePublish { get; set; }
        public string GroupPowers { get; set; } = string.Empty;
        public Guid AccountId { get; set; }
    }

    public class GroupsUpdatedEventArgs : EventArgs
    {
        public Guid AccountId { get; set; }
        public IEnumerable<GroupDto> Groups { get; set; } = new List<GroupDto>();
        public int GroupCount { get; set; }

        public GroupsUpdatedEventArgs(Guid accountId, IEnumerable<GroupDto> groups)
        {
            AccountId = accountId;
            Groups = groups;
            GroupCount = groups.Count();
        }
    }
}