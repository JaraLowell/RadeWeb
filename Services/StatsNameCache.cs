using Microsoft.EntityFrameworkCore;
using RadegastWeb.Data;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Interface for managing persistent avatar names specifically for stats display.
    /// Unlike GlobalDisplayNameCache which expires after 48 hours, this service
    /// preserves names indefinitely for historical statistics reporting.
    /// </summary>
    public interface IStatsNameCache
    {
        /// <summary>
        /// Store or update an avatar's names for stats display
        /// </summary>
        Task StoreNameAsync(string avatarId, string? displayName, string? avatarName);
        
        /// <summary>
        /// Get the best available name for an avatar from the stats cache
        /// </summary>
        Task<string> GetBestNameAsync(string avatarId);
        
        /// <summary>
        /// Get multiple names efficiently for stats display
        /// </summary>
        Task<Dictionary<string, string>> GetBestNamesAsync(IEnumerable<string> avatarIds);
        
        /// <summary>
        /// Update names from current cache if they're better than stored names
        /// </summary>
        Task UpdateFromCacheAsync(string avatarId, IGlobalDisplayNameCache globalCache);
        
        /// <summary>
        /// Clean up names for avatars not seen in visitor stats for more than specified days
        /// </summary>
        Task CleanupOldNamesAsync(int keepDays = 60);
    }

    /// <summary>
    /// Service for managing persistent avatar names specifically for stats display.
    /// This service stores names indefinitely and avoids the memory pressure of
    /// resolving thousands of historical names on every stats page load.
    /// </summary>
    public class StatsNameCache : IStatsNameCache
    {
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        private readonly ILogger<StatsNameCache> _logger;

        public StatsNameCache(IDbContextFactory<RadegastDbContext> dbContextFactory, ILogger<StatsNameCache> logger)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }

        public async Task StoreNameAsync(string avatarId, string? displayName, string? avatarName)
        {
            if (string.IsNullOrEmpty(avatarId))
                return;

            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                var existing = await context.StatsDisplayNames
                    .FirstOrDefaultAsync(sdn => sdn.AvatarId == avatarId);

                if (existing != null)
                {
                    // Update if we have better names
                    bool updated = false;
                    
                    if (IsNameBetter(displayName, existing.DisplayName))
                    {
                        existing.DisplayName = displayName;
                        updated = true;
                    }
                    
                    if (IsNameBetter(avatarName, existing.AvatarName))
                    {
                        existing.AvatarName = avatarName;
                        updated = true;
                    }
                    
                    if (updated)
                    {
                        existing.LastUpdated = DateTime.UtcNow;
                        await context.SaveChangesAsync();
                        _logger.LogDebug("Updated stats name for {AvatarId}", avatarId);
                    }
                }
                else
                {
                    // Create new entry
                    var statsName = new StatsDisplayName
                    {
                        AvatarId = avatarId,
                        DisplayName = displayName,
                        AvatarName = avatarName,
                        LastUpdated = DateTime.UtcNow
                    };
                    
                    context.StatsDisplayNames.Add(statsName);
                    await context.SaveChangesAsync();
                    _logger.LogDebug("Stored new stats name for {AvatarId}", avatarId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing stats name for {AvatarId}", avatarId);
            }
        }

        public async Task<string> GetBestNameAsync(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId))
                return "Unknown User";

            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                var statsName = await context.StatsDisplayNames
                    .FirstOrDefaultAsync(sdn => sdn.AvatarId == avatarId);

                return statsName?.BestName ?? $"Avatar {avatarId.Substring(0, Math.Min(8, avatarId.Length))}...";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stats name for {AvatarId}", avatarId);
                return $"Avatar {avatarId.Substring(0, Math.Min(8, avatarId.Length))}...";
            }
        }

        public async Task<Dictionary<string, string>> GetBestNamesAsync(IEnumerable<string> avatarIds)
        {
            var result = new Dictionary<string, string>();
            var avatarIdList = avatarIds.ToList();
            
            if (!avatarIdList.Any())
                return result;

            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                var statsNames = await context.StatsDisplayNames
                    .Where(sdn => avatarIdList.Contains(sdn.AvatarId))
                    .ToListAsync();

                // Add found names
                foreach (var statsName in statsNames)
                {
                    result[statsName.AvatarId] = statsName.BestName;
                }
                
                // Add fallback names for missing avatars
                foreach (var avatarId in avatarIdList)
                {
                    if (!result.ContainsKey(avatarId))
                    {
                        result[avatarId] = $"Avatar {avatarId.Substring(0, Math.Min(8, avatarId.Length))}...";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stats names for {Count} avatars", avatarIdList.Count);
                
                // Fallback for all avatars on error
                foreach (var avatarId in avatarIdList)
                {
                    result[avatarId] = $"Avatar {avatarId.Substring(0, Math.Min(8, avatarId.Length))}...";
                }
            }

            return result;
        }

        public async Task UpdateFromCacheAsync(string avatarId, IGlobalDisplayNameCache globalCache)
        {
            if (string.IsNullOrEmpty(avatarId))
                return;

            try
            {
                // Get current cached names
                var displayName = await globalCache.GetDisplayNameAsync(avatarId, Models.NameDisplayMode.Smart);
                var avatarName = await globalCache.GetLegacyNameAsync(avatarId);
                
                // Only store if we got valid names
                if (!string.IsNullOrEmpty(displayName) && displayName != "Loading..." ||
                    !string.IsNullOrEmpty(avatarName) && avatarName != "Unknown User")
                {
                    await StoreNameAsync(avatarId, displayName, avatarName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not update stats name from cache for {AvatarId}", avatarId);
            }
        }

        public async Task CleanupOldNamesAsync(int keepDays = 60)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                // Find avatars that haven't been seen in visitor stats for more than keepDays
                var cutoffDate = DateTime.UtcNow.Date.AddDays(-keepDays);
                
                // Get all avatar IDs that have been seen recently (within keepDays)
                var recentAvatarIds = await context.VisitorStats
                    .Where(vs => vs.VisitDate >= cutoffDate)
                    .Select(vs => vs.AvatarId)
                    .Distinct()
                    .ToListAsync();
                
                // Find stats names for avatars NOT in the recent list
                var oldStatsNames = await context.StatsDisplayNames
                    .Where(sdn => !recentAvatarIds.Contains(sdn.AvatarId))
                    .ToListAsync();
                
                if (oldStatsNames.Any())
                {
                    context.StatsDisplayNames.RemoveRange(oldStatsNames);
                    await context.SaveChangesAsync();
                    
                    _logger.LogInformation("Cleaned up {Count} old stats display names for avatars not seen in {Days} days", 
                        oldStatsNames.Count, keepDays);
                }
                else
                {
                    _logger.LogDebug("No old stats display names to clean up (keeping {Days} days)", keepDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old stats display names");
            }
        }

        /// <summary>
        /// Determines if a new name is better than an existing name
        /// </summary>
        private static bool IsNameBetter(string? newName, string? existingName)
        {
            // New name is empty or invalid
            if (string.IsNullOrEmpty(newName) || 
                newName == "Loading..." || 
                newName == "???" ||
                newName == "Unknown User")
            {
                return false;
            }
            
            // No existing name, so new name is better
            if (string.IsNullOrEmpty(existingName))
                return true;
            
            // Existing name is a placeholder, new name is better
            if (existingName == "Loading..." || 
                existingName == "???" || 
                existingName == "Unknown User")
            {
                return true;
            }
            
            // Both are valid, prefer the new one if it's longer (more descriptive)
            return newName.Length > existingName.Length;
        }
    }
}