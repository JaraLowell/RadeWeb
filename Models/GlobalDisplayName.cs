using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Global display name cache that stores display names for all avatars
    /// across all accounts without account-specific associations.
    /// This prevents cache invalidation when accounts login/logout.
    /// </summary>
    public class GlobalDisplayName
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        [Required]
        public string AvatarId { get; set; } = string.Empty; // UUID as string - this is the primary key for lookups
        
        [Required]
        [StringLength(200)]
        public string DisplayNameValue { get; set; } = string.Empty;
        
        [Required]
        [StringLength(100)]
        public string UserName { get; set; } = string.Empty; // first.last format
        
        [Required]
        [StringLength(100)]
        public string LegacyFirstName { get; set; } = string.Empty;
        
        [Required]
        [StringLength(100)]
        public string LegacyLastName { get; set; } = string.Empty;
        
        public string LegacyFullName => FormatLegacyName(LegacyFirstName, LegacyLastName);
        
        /// <summary>
        /// Formats the legacy name, removing "Resident" as last name if present
        /// </summary>
        /// <param name="firstName">First name</param>
        /// <param name="lastName">Last name</param>
        /// <returns>Formatted name without "Resident" suffix</returns>
        private static string FormatLegacyName(string firstName, string lastName)
        {
            if (string.IsNullOrEmpty(firstName))
                return string.Empty;
                
            if (string.IsNullOrEmpty(lastName) || lastName.Equals("Resident", StringComparison.OrdinalIgnoreCase))
                return firstName;
                
            return $"{firstName} {lastName}";
        }
        
        public bool IsDefaultDisplayName { get; set; } = true;
        
        public DateTime NextUpdate { get; set; } = DateTime.UtcNow.AddHours(24);
        
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        
        public DateTime CachedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Converts to DisplayName for compatibility with existing code
        /// </summary>
        public DisplayName ToDisplayName()
        {
            return new DisplayName
            {
                Id = this.Id,
                AvatarId = this.AvatarId,
                DisplayNameValue = this.DisplayNameValue,
                UserName = this.UserName,
                LegacyFirstName = this.LegacyFirstName,
                LegacyLastName = this.LegacyLastName,
                IsDefaultDisplayName = this.IsDefaultDisplayName,
                NextUpdate = this.NextUpdate,
                LastUpdated = this.LastUpdated,
                CachedAt = this.CachedAt
                // Note: AccountId removed from DisplayName model - no longer needed
            };
        }
        
        /// <summary>
        /// Creates from DisplayName for compatibility
        /// </summary>
        public static GlobalDisplayName FromDisplayName(DisplayName displayName)
        {
            return new GlobalDisplayName
            {
                AvatarId = displayName.AvatarId,
                DisplayNameValue = displayName.DisplayNameValue,
                UserName = displayName.UserName,
                LegacyFirstName = displayName.LegacyFirstName,
                LegacyLastName = displayName.LegacyLastName,
                IsDefaultDisplayName = displayName.IsDefaultDisplayName,
                NextUpdate = displayName.NextUpdate,
                LastUpdated = displayName.LastUpdated,
                CachedAt = displayName.CachedAt
            };
        }
    }
}