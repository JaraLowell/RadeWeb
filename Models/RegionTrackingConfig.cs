using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Configuration model for regions to track
    /// </summary>
    public class RegionTrackingConfig
    {
        public List<TrackedRegion> Regions { get; set; } = new();
        public int PollingIntervalMinutes { get; set; } = 5;
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Individual region to track
    /// </summary>
    public class TrackedRegion
    {
        [Required]
        public string RegionName { get; set; } = string.Empty;
        
        public string? GridUrl { get; set; }
        
        public string? Description { get; set; }
        
        public bool Enabled { get; set; } = true;
    }
}
