using RadegastWeb.Models;
using OpenMetaverse;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Compatibility adapter to make the new MasterDisplayNameService work with existing code 
    /// that expects the old IDisplayNameService interface.
    /// </summary>
    public class DisplayNameServiceCompatibilityAdapter : IDisplayNameService
    {
        private readonly IMasterDisplayNameService _masterService;

        public DisplayNameServiceCompatibilityAdapter(IMasterDisplayNameService masterService)
        {
            _masterService = masterService;
        }

        public event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged
        {
            add => _masterService.DisplayNameChanged += value;
            remove => _masterService.DisplayNameChanged -= value;
        }

        public Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
            => _masterService.GetDisplayNameAsync(avatarId, mode, fallbackName);

        public Task<string> GetLegacyNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
            => _masterService.GetLegacyNameAsync(avatarId, fallbackName);

        public Task<string> GetUserNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
            => _masterService.GetUserNameAsync(avatarId, fallbackName);

        public Task RefreshDisplayNameAsync(Guid accountId, string avatarId)
            => _masterService.PreloadDisplayNamesAsync(new[] { avatarId });

        public Task PreloadDisplayNamesAsync(Guid accountId, IEnumerable<string> avatarIds)
            => _masterService.PreloadDisplayNamesAsync(avatarIds);

        public async Task<bool> UpdateDisplayNamesAsync(Guid accountId, Dictionary<UUID, AgentDisplayName> displayNames)
        {
            await _masterService.UpdateDisplayNamesAsync(displayNames);
            return true;
        }

        public async Task<bool> UpdateLegacyNamesAsync(Guid accountId, Dictionary<UUID, string> legacyNames)
        {
            await _masterService.UpdateLegacyNamesAsync(legacyNames);
            return true;
        }

        public Task CleanExpiredCacheAsync(Guid accountId)
        {
            _masterService.CleanExpiredCache();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId)
        {
            // Return empty collection since this is handled by the global cache
            return Task.FromResult<IEnumerable<DisplayName>>(Array.Empty<DisplayName>());
        }

        public void CleanupAccount(Guid accountId)
        {
            _masterService.CleanupAccount(accountId);
        }
    }
}