using OpenMetaverse;

namespace RadegastWeb.Services
{
    public interface INameResolutionService
    {
        /// <summary>
        /// Resolves an agent's name by UUID with caching and timeout handling
        /// </summary>
        /// <param name="accountId">Account ID for context</param>
        /// <param name="agentId">Agent UUID to resolve</param>
        /// <param name="resolveType">Type of name resolution</param>
        /// <param name="timeoutMs">Timeout in milliseconds (default 5000)</param>
        /// <returns>Resolved name or UUID string if failed</returns>
        Task<string> ResolveAgentNameAsync(Guid accountId, UUID agentId, ResolveType resolveType = ResolveType.AgentDefaultName, int timeoutMs = 5000);

        /// <summary>
        /// Resolves a group's name by UUID with caching and timeout handling
        /// </summary>
        /// <param name="accountId">Account ID for context</param>
        /// <param name="groupId">Group UUID to resolve</param>
        /// <param name="timeoutMs">Timeout in milliseconds (default 5000)</param>
        /// <returns>Resolved group name or UUID string if failed</returns>
        Task<string> ResolveGroupNameAsync(Guid accountId, UUID groupId, int timeoutMs = 5000);

        /// <summary>
        /// Resolves a parcel's name by UUID with caching and timeout handling
        /// </summary>
        /// <param name="accountId">Account ID for context</param>
        /// <param name="parcelId">Parcel UUID to resolve</param>
        /// <param name="timeoutMs">Timeout in milliseconds (default 5000)</param>
        /// <returns>Resolved parcel name or UUID string if failed</returns>
        Task<string> ResolveParcelNameAsync(Guid accountId, UUID parcelId, int timeoutMs = 5000);

        /// <summary>
        /// Generic resolve method that routes to specific resolver based on type
        /// </summary>
        /// <param name="accountId">Account ID for context</param>
        /// <param name="id">UUID to resolve</param>
        /// <param name="type">Type of resolution</param>
        /// <param name="timeoutMs">Timeout in milliseconds</param>
        /// <returns>Resolved name or UUID string if failed</returns>
        Task<string> ResolveAsync(Guid accountId, UUID id, ResolveType type, int timeoutMs = 5000);

        /// <summary>
        /// Checks if name resolution is enabled for the given account
        /// </summary>
        /// <param name="accountId">Account ID to check</param>
        /// <returns>True if resolution is enabled</returns>
        bool IsResolutionEnabled(Guid accountId);

        /// <summary>
        /// Gets a cached name if available
        /// </summary>
        /// <param name="accountId">Account ID for context</param>
        /// <param name="id">UUID to look up</param>
        /// <param name="type">Type of resolution</param>
        /// <returns>Cached name or null if not available</returns>
        string? GetCachedName(Guid accountId, UUID id, ResolveType type);

        /// <summary>
        /// Constant for incomplete name resolution (similar to Radegast)
        /// </summary>
        public const string INCOMPLETE_NAME = "Loading...";

        /// <summary>
        /// Registers a WebRadegastInstance for name resolution
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="instance">WebRadegastInstance</param>
        void RegisterInstance(Guid accountId, object instance);

        /// <summary>
        /// Unregisters a WebRadegastInstance
        /// </summary>
        /// <param name="accountId">Account ID</param>
        void UnregisterInstance(Guid accountId);
    }
}