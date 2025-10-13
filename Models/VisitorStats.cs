using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Represents visitor statistics for tracking unique avatars per region per day
    /// </summary>
    public class VisitorStats
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        [Required]
        [StringLength(36)]
        public string AvatarId { get; set; } = string.Empty;
        
        [Required]
        [StringLength(200)]
        public string RegionName { get; set; } = string.Empty;
        
        [Required]
        public ulong SimHandle { get; set; }
        
        [Required]
        public DateTime VisitDate { get; set; } // Date only (time truncated to midnight)
        
        [Required]
        public DateTime FirstSeenAt { get; set; } // Exact timestamp when first seen
        
        [Required]
        public DateTime LastSeenAt { get; set; } // Last time this avatar was seen on this date
        
        [StringLength(200)]
        public string? AvatarName { get; set; }
        
        [StringLength(200)]
        public string? DisplayName { get; set; }
        
        /// <summary>
        /// Grid coordinates of the region (extracted from SimHandle)
        /// </summary>
        public uint RegionX { get; set; }
        
        /// <summary>
        /// Grid coordinates of the region (extracted from SimHandle)
        /// </summary>
        public uint RegionY { get; set; }
    }
    
    /// <summary>
    /// DTO for visitor statistics aggregated by date
    /// </summary>
    public class DailyVisitorStatsDto
    {
        public DateTime Date { get; set; }
        public string RegionName { get; set; } = string.Empty;
        public int UniqueVisitors { get; set; }
        public int TotalVisits { get; set; } // Total number of visit records (may be higher if same avatar visited multiple times)
    }
    
    /// <summary>
    /// DTO for visitor statistics summary
    /// </summary>
    public class VisitorStatsSummaryDto
    {
        public string RegionName { get; set; } = string.Empty;
        public List<DailyVisitorStatsDto> DailyStats { get; set; } = new();
        public int TotalUniqueVisitors { get; set; }
        public int TotalVisits { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
    }
    
    /// <summary>
    /// DTO for unique visitor information
    /// </summary>
    public class UniqueVisitorDto
    {
        public string AvatarId { get; set; } = string.Empty;
        public string? AvatarName { get; set; }
        public string? DisplayName { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public int VisitCount { get; set; }
        public List<string> RegionsVisited { get; set; } = new();
    }
}