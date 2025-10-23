using OpenMetaverse;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Interface for display name services (legacy compatibility)
    /// </summary>
    public interface IDisplayNameService
    {
        Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null);
        Task<string> GetLegacyNameAsync(Guid accountId, string avatarId, string? fallbackName = null);
        Task<string> GetUserNameAsync(Guid accountId, string avatarId, string? fallbackName = null);
        Task RefreshDisplayNameAsync(Guid accountId, string avatarId);
        Task PreloadDisplayNamesAsync(Guid accountId, IEnumerable<string> avatarIds);
        Task<bool> UpdateDisplayNamesAsync(Guid accountId, Dictionary<UUID, AgentDisplayName> displayNames);
        Task<bool> UpdateLegacyNamesAsync(Guid accountId, Dictionary<UUID, string> legacyNames);
        Task CleanExpiredCacheAsync(Guid accountId);
        Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId);
        void CleanupAccount(Guid accountId);
        
        event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
    }
}