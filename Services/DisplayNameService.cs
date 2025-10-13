using OpenMetaverse;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
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

    public class DisplayNameService : IDisplayNameService
    {
        private readonly ILogger<DisplayNameService> _logger;
        private readonly IGlobalDisplayNameCache _globalCache;
        public event EventHandler<DisplayNameChangedEventArgs>? DisplayNameChanged;
        
        public DisplayNameService(ILogger<DisplayNameService> logger, IGlobalDisplayNameCache globalCache)
        {
            _logger = logger;
            _globalCache = globalCache;
            _globalCache.DisplayNameChanged += (s, e) => DisplayNameChanged?.Invoke(this, e);
        }

        public Task<string> GetDisplayNameAsync(Guid accountId, string avatarId, NameDisplayMode mode = NameDisplayMode.Smart, string? fallbackName = null)
            => _globalCache.GetDisplayNameAsync(avatarId, mode, fallbackName);

        public Task<string> GetLegacyNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
            => _globalCache.GetLegacyNameAsync(avatarId, fallbackName);

        public Task<string> GetUserNameAsync(Guid accountId, string avatarId, string? fallbackName = null)
            => _globalCache.GetUserNameAsync(avatarId, fallbackName);

        public Task RefreshDisplayNameAsync(Guid accountId, string avatarId)
            => _globalCache.PreloadDisplayNamesAsync(new[] { avatarId });

        public Task PreloadDisplayNamesAsync(Guid accountId, IEnumerable<string> avatarIds)
            => _globalCache.PreloadDisplayNamesAsync(avatarIds);

        public async Task<bool> UpdateDisplayNamesAsync(Guid accountId, Dictionary<UUID, AgentDisplayName> displayNames)
        {
            await _globalCache.UpdateDisplayNamesAsync(displayNames);
            return true;
        }

        public async Task<bool> UpdateLegacyNamesAsync(Guid accountId, Dictionary<UUID, string> legacyNames)
        {
            await _globalCache.UpdateLegacyNamesAsync(legacyNames);
            return true;
        }

        public Task CleanExpiredCacheAsync(Guid accountId)
        {
            _globalCache.CleanExpiredCache();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<DisplayName>> GetCachedNamesAsync(Guid accountId)
            => Task.FromResult(Enumerable.Empty<DisplayName>());

        public void CleanupAccount(Guid accountId) { }
    }
}
