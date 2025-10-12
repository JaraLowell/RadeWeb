using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    public class Notice
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        public Guid AccountId { get; set; }
        
        [Required]
        [StringLength(500)]
        public string Title { get; set; } = string.Empty;
        
        [Required]
        public string Message { get; set; } = string.Empty;
        
        [Required]
        [StringLength(200)]
        public string FromName { get; set; } = string.Empty;
        
        public string FromId { get; set; } = string.Empty;
        
        public string? GroupId { get; set; }
        
        [StringLength(200)]
        public string? GroupName { get; set; }
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        [Required]
        [StringLength(50)]
        public string Type { get; set; } = string.Empty; // Group, Region, System
        
        public bool HasAttachment { get; set; }
        
        [StringLength(200)]
        public string? AttachmentName { get; set; }
        
        [StringLength(100)]
        public string? AttachmentType { get; set; }
        
        public bool RequiresAcknowledgment { get; set; }
        
        public bool IsAcknowledged { get; set; }
        
        public bool IsRead { get; set; } = false;
        
        // Navigation properties
        public virtual Account Account { get; set; } = null!;
    }
}