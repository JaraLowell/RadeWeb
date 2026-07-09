using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public interface IFriendOnlineStateService
    {
        void ResetAccount(Guid accountId);
        void EnsureFriendsTracked(Guid accountId, IEnumerable<string> friendIds);
        void SetFriendOnline(Guid accountId, string friendId, bool isOnline);
        bool IsFriendOnline(Guid accountId, string friendId, bool useGlobal = true);
    }

    public class FriendOnlineStateService : IFriendOnlineStateService
    {
        private readonly object _sync = new();
        private readonly ConcurrentDictionary<Guid, Dictionary<string, bool>> _accountFriendStates = new();
        private readonly ConcurrentDictionary<string, int> _globalOnlineCounts = new();

        public void ResetAccount(Guid accountId)
        {
            lock (_sync)
            {
                if (!_accountFriendStates.TryRemove(accountId, out var existing))
                {
                    return;
                }

                foreach (var state in existing)
                {
                    if (state.Value)
                    {
                        DecrementGlobalOnlineCount(state.Key);
                    }
                }
            }
        }

        public void EnsureFriendsTracked(Guid accountId, IEnumerable<string> friendIds)
        {
            lock (_sync)
            {
                var accountStates = _accountFriendStates.GetOrAdd(accountId, _ => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

                foreach (var friendId in friendIds)
                {
                    var key = NormalizeId(friendId);
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    if (!accountStates.ContainsKey(key))
                    {
                        accountStates[key] = false;
                    }
                }
            }
        }

        public void SetFriendOnline(Guid accountId, string friendId, bool isOnline)
        {
            var key = NormalizeId(friendId);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            lock (_sync)
            {
                var accountStates = _accountFriendStates.GetOrAdd(accountId, _ => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
                accountStates.TryGetValue(key, out var wasOnline);

                if (wasOnline == isOnline)
                {
                    accountStates[key] = isOnline;
                    return;
                }

                accountStates[key] = isOnline;

                if (wasOnline)
                {
                    DecrementGlobalOnlineCount(key);
                }

                if (isOnline)
                {
                    _globalOnlineCounts.AddOrUpdate(key, 1, (_, count) => count + 1);
                }
            }
        }

        public bool IsFriendOnline(Guid accountId, string friendId, bool useGlobal = true)
        {
            var key = NormalizeId(friendId);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            lock (_sync)
            {
                if (_accountFriendStates.TryGetValue(accountId, out var accountStates) &&
                    accountStates.TryGetValue(key, out var isOnline) &&
                    isOnline)
                {
                    return true;
                }

                if (!useGlobal)
                {
                    return false;
                }

                return _globalOnlineCounts.TryGetValue(key, out var onlineCount) && onlineCount > 0;
            }
        }

        private void DecrementGlobalOnlineCount(string friendId)
        {
            if (!_globalOnlineCounts.TryGetValue(friendId, out var currentCount))
            {
                return;
            }

            var updated = currentCount - 1;
            if (updated <= 0)
            {
                _globalOnlineCounts.TryRemove(friendId, out _);
                return;
            }

            _globalOnlineCounts[friendId] = updated;
        }

        private static string NormalizeId(string friendId)
        {
            return string.IsNullOrWhiteSpace(friendId)
                ? string.Empty
                : friendId.Trim().ToLowerInvariant();
        }
    }
}
