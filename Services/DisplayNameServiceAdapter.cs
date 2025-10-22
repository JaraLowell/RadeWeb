using RadegastWeb.Models;
using OpenMetaverse;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Adapter to make UnifiedDisplayNameService compatible with existing IDisplayNameService interface
    /// </summary>
    public class DisplayNameServiceAdapter : IDisplayNameService
    {
        private readonly IUnifiedDisplayNameService _unifiedService;

        public DisplayNameServiceAdapter(IUnifiedDisplayNameService unifiedService)
        {
            _unifiedService = unifiedService;
        }

        public event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged
        {
            add => _unifiedService.DisplayNameChanged += value;
            remove => _unifiedService.DisplayNameChanged -= value;
        }

        public Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
            => _unifiedService.GetDisplayNameAsync(avatarId, mode, fallbackName);

        public Task<string> GetLegacyNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
            => _unifiedService.GetLegacyNameAsync(avatarId, fallbackName);

        public Task<string> GetUserNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
            => _unifiedService.GetUserNameAsync(avatarId, fallbackName);

        public Task RefreshDisplayNameAsync(Guid accountId, string avatarId)
            => _unifiedService.RefreshDisplayNameAsync(accountId, avatarId);

        public Task PreloadDisplayNamesAsync(Guid accountId, IEnumerable<string> avatarIds)
            => _unifiedService.PreloadDisplayNamesAsync(avatarIds);

        public async Task<bool> UpdateDisplayNamesAsync(Guid accountId, Dictionary<UUID, AgentDisplayName> displayNames)
        {
            await _unifiedService.UpdateDisplayNamesAsync(displayNames);
            return true;
        }

        public async Task<bool> UpdateLegacyNamesAsync(Guid accountId, Dictionary<UUID, string> legacyNames)
        {
            await _unifiedService.UpdateLegacyNamesAsync(legacyNames);
            return true;
        }

        public Task CleanExpiredCacheAsync(Guid accountId)
            => _unifiedService.CleanExpiredCacheAsync(accountId);

        public Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId)
            => _unifiedService.GetCachedNamesAsync(accountId);

        public void CleanupAccount(Guid accountId)
            => _unifiedService.CleanupAccount(accountId);
    }
}