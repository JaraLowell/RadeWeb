using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Persistent storage for avatar names specifically for stats display.
    /// Unlike GlobalDisplayNames which expires after 48 hours, this table preserves
    /// names indefinitely for historical statistics reporting.
    /// </summary>
    public class StatsDisplayName
    {
        [Required]
        [StringLength(36)]
        public string AvatarId { get; set; } = string.Empty;
        
        [StringLength(200)]
        public string? DisplayName { get; set; }
        
        [StringLength(200)]
        public string? AvatarName { get; set; }
        
        [Required]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Gets the best available name for display, prioritizing DisplayName over AvatarName
        /// </summary>
        public string BestName
        {
            get
            {
                if (!string.IsNullOrEmpty(DisplayName) && 
                    DisplayName != "Loading..." && 
                    DisplayName != "???")
                {
                    return DisplayName;
                }
                
                if (!string.IsNullOrEmpty(AvatarName) && 
                    AvatarName != "Unknown User" && 
                    AvatarName != "Loading...")
                {
                    return AvatarName;
                }
                
                return $"Avatar {AvatarId.Substring(0, 8)}...";
            }
        }
    }
}