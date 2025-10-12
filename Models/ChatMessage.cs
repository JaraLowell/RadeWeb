using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    public class ChatMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        public Guid AccountId { get; set; }
        
        [Required]
        [StringLength(500)]
        public string SenderName { get; set; } = string.Empty;
        
        [Required]
        public string Message { get; set; } = string.Empty;
        
        public string ChatType { get; set; } = "Normal"; // Normal, Whisper, Shout, IM, System
        
        public string? Channel { get; set; } // For group chat, local chat, etc.
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        public string? SenderUuid { get; set; }
        
        public string? SenderId { get; set; } // UUID of sender for IM messages
        
        public string? TargetId { get; set; } // UUID of target for IM messages
        
        public string? SessionId { get; set; } // Session ID for IM/Group chats
        
        public string? SessionName { get; set; } // Display name for IM/Group session
        
        public string? RegionName { get; set; }
        
        // Navigation properties
        public virtual Account Account { get; set; } = null!;
    }
}