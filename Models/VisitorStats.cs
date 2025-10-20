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
        public int TrueUniqueVisitors { get; set; } // Visitors not seen in past 60 days
        public int TotalVisits { get; set; } // Total number of visit records (may be higher if same avatar visited multiple times)
        
        // SLT formatted date for display
        public string? SLTDate { get; set; } // MMM dd, yyyy format
    }
    
    /// <summary>
    /// DTO for visitor statistics summary
    /// </summary>
    public class VisitorStatsSummaryDto
    {
        public string RegionName { get; set; } = string.Empty;
        public List<DailyVisitorStatsDto> DailyStats { get; set; } = new();
        public int TotalUniqueVisitors { get; set; }
        public int TrueUniqueVisitors { get; set; } // Visitors not seen in past 60 days
        public int TotalVisits { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        
        // SLT formatted dates for display
        public string? SLTStartDate { get; set; } // MMM dd, yyyy format
        public string? SLTEndDate { get; set; } // MMM dd, yyyy format
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
        public bool IsTrueUnique { get; set; } // True if visitor has never been seen before this period
        public VisitorType VisitorType { get; set; } // Classification of visitor type
        
        // SLT formatted timestamps for display
        public string? SLTFirstSeen { get; set; } // MMM dd, yyyy HH:mm format
        public string? SLTLastSeen { get; set; } // MMM dd, yyyy HH:mm format
    }
    
    /// <summary>
    /// Visitor classification types
    /// </summary>
    public enum VisitorType
    {
        Brand_New,      // Never seen before anywhere
        Returning,      // Seen before but not recently  
        Regular,        // Seen recently (within last 30 days)
        Daily           // Seen today already
    }
    
    /// <summary>
    /// DTO for detailed visitor classification analysis
    /// </summary>
    public class VisitorClassificationDto
    {
        public string RegionName { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        
        // Counts by visitor type
        public int BrandNewVisitors { get; set; }      // Never seen before
        public int ReturningVisitors { get; set; }     // Last seen 31+ days ago
        public int RegularVisitors { get; set; }       // Last seen 1-30 days ago
        public int TotalUniqueVisitors { get; set; }   // All unique visitors in period
        
        // Daily breakdown
        public List<DailyClassificationDto> DailyBreakdown { get; set; } = new();
        
        // Visitor details
        public List<UniqueVisitorDto> VisitorDetails { get; set; } = new();
        
        // SLT formatted dates for display
        public string? SLTStartDate { get; set; } // MMM dd, yyyy format
        public string? SLTEndDate { get; set; } // MMM dd, yyyy format
    }
    
    /// <summary>
    /// Daily visitor classification breakdown
    /// </summary>
    public class DailyClassificationDto
    {
        public DateTime Date { get; set; }
        public int BrandNewVisitors { get; set; }
        public int ReturningVisitors { get; set; }
        public int RegularVisitors { get; set; }
        public int TotalUniqueVisitors { get; set; }
        
        // SLT formatted date for display
        public string? SLTDate { get; set; } // MMM dd, yyyy format
    }
    
    /// <summary>
    /// DTO for hourly visitor activity in SLT time
    /// </summary>
    public class HourlyVisitorStatsDto
    {
        public int Hour { get; set; } // 0-23 in SLT
        public string HourLabel { get; set; } = string.Empty; // "12:00 AM", "1:00 PM", etc.
        public int UniqueVisitors { get; set; }
        public int TotalVisits { get; set; }
        public double AverageVisitors { get; set; } // Average across all days in period
    }
    
    /// <summary>
    /// DTO for 24-hour activity summary
    /// </summary>
    public class HourlyActivitySummaryDto
    {
        public string RegionName { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int DaysAnalyzed { get; set; }
        public List<HourlyVisitorStatsDto> HourlyStats { get; set; } = new();
        public int PeakHour { get; set; } // Hour with most activity (0-23 SLT)
        public string PeakHourLabel { get; set; } = string.Empty;
        public double PeakHourAverage { get; set; } // Average visitors during peak hour
        public int QuietHour { get; set; } // Hour with least activity (0-23 SLT)  
        public string QuietHourLabel { get; set; } = string.Empty;
        public double QuietHourAverage { get; set; } // Average visitors during quiet hour
        
        // SLT formatted dates for display
        public string? SLTStartDate { get; set; } // MMM dd, yyyy format
        public string? SLTEndDate { get; set; } // MMM dd, yyyy format
    }
}