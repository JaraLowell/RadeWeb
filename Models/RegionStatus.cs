using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Database model for storing region status history
    /// </summary>
    public class RegionStatus
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        [Required]
        [StringLength(255)]
        public string RegionName { get; set; } = string.Empty;
        
        [StringLength(500)]
        public string? GridUrl { get; set; }
        
        /// <summary>
        /// True if region is online/accessible
        /// </summary>
        public bool IsOnline { get; set; }
        
        /// <summary>
        /// Region handle (64-bit encoded position)
        /// </summary>
        public ulong? RegionHandle { get; set; }
        
        /// <summary>
        /// Region UUID
        /// </summary>
        public string? RegionUuid { get; set; }
        
        /// <summary>
        /// X coordinate in grid units
        /// </summary>
        public uint? LocationX { get; set; }
        
        /// <summary>
        /// Y coordinate in grid units
        /// </summary>
        public uint? LocationY { get; set; }
        
        /// <summary>
        /// Region access level (PG, Mature, Adult)
        /// </summary>
        public string? AccessLevel { get; set; }
        
        /// <summary>
        /// Number of agents/avatars in the region - PRIMARY DATA WE TRACK
        /// This is what Firestorm shows on the world map
        /// </summary>
        public int? AgentCount { get; set; }
        
        /// <summary>
        /// Region size X (e.g., 256, 512 for var regions)
        /// </summary>
        public uint? SizeX { get; set; }
        
        /// <summary>
        /// Region size Y
        /// </summary>
        public uint? SizeY { get; set; }
        
        /// <summary>
        /// Response time in milliseconds
        /// </summary>
        public int? ResponseTimeMs { get; set; }
        
        /// <summary>
        /// Error message if check failed
        /// </summary>
        public string? ErrorMessage { get; set; }
        
        /// <summary>
        /// When this status was recorded
        /// </summary>
        public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    }
}
