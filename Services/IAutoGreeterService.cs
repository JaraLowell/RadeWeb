namespace RadegastWeb.Services
{
    /// <summary>
    /// Service interface for managing auto-greeter functionality
    /// </summary>
    public interface IAutoGreeterService
    {
        /// <summary>
        /// Process a new avatar that has entered radar range
        /// </summary>
        /// <param name="avatarId">UUID of the avatar</param>
        /// <param name="displayName">Display name of the avatar</param>
        /// <param name="distance">Distance in meters</param>
        /// <param name="accountId">Account ID that detected the avatar</param>
        Task ProcessNewAvatarAsync(string avatarId, string displayName, double distance, Guid accountId);
        
        /// <summary>
        /// Update the last seen time for an avatar (called from any presence source)
        /// This keeps the tracking data fresh and prevents re-greeting of avatars that are still present
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="avatarId">Avatar UUID</param>
        void UpdateLastSeen(Guid accountId, string avatarId);
        
        /// <summary>
        /// Clear greeted avatars when changing regions
        /// </summary>
        /// <param name="accountId">Account ID</param>
        void ClearGreetedAvatars(Guid accountId);
        
        /// <summary>
        /// Check if an avatar has already been greeted
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="avatarId">Avatar UUID</param>
        /// <returns>True if already greeted</returns>
        bool HasBeenGreeted(Guid accountId, string avatarId);
        
        /// <summary>
        /// Check if an avatar has received an initial greeting (persists until they leave)
        /// Used to avoid re-greeting avatars that just changed state (seated -> standing)
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="avatarId">Avatar UUID</param>
        /// <returns>True if avatar has received an initial greeting</returns>
        bool HasHadInitialGreeting(Guid accountId, string avatarId);
        
        /// <summary>
        /// Track when an avatar leaves the area
        /// </summary>
        /// <param name="avatarId">UUID of the avatar</param>
        /// <param name="accountId">Account ID</param>
        void TrackAvatarDeparture(string avatarId, Guid accountId);
        
        /// <summary>
        /// Clean up old avatar tracking data
        /// </summary>
        /// <param name="accountId">Account ID</param>
        Task CleanupOldTrackingDataAsync(Guid accountId);
        
        /// <summary>
        /// Detect avatars that have left by comparing current nearby list with tracked avatars
        /// </summary>
        /// <param name="accountId">Account ID</param>
        /// <param name="currentNearbyAvatarIds">List of currently nearby avatar UUIDs</param>
        void DetectDepartures(Guid accountId, IEnumerable<string> currentNearbyAvatarIds);
        
        /// <summary>
        /// Clean up all tracking data for an account (called on logout/disconnect)
        /// </summary>
        /// <param name="accountId">Account ID</param>
        void CleanupAccount(Guid accountId);
    }
}
