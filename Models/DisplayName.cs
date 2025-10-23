using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Display name model - now represents global display names (no account association)
    /// This is compatible with GlobalDisplayName for easier migration.
    /// </summary>
    public class DisplayName
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        [Required]
        public string AvatarId { get; set; } = string.Empty; // UUID as string
        
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
        
        // Note: AccountId removed - display names are now global
        // Use GlobalDisplayName directly if you need database operations
    }
    
    public enum NameDisplayMode
    {
        Standard = 0,           // No display names - legacy names only
        Smart = 1,              // Display name with (legacy name) if both available and different
        OnlyDisplayName = 2,    // Only show display name
        DisplayNameAndUserName = 3  // Always show "DisplayName (username)"
    }
}