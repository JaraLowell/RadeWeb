using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    public class Account
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        [Required]
        [StringLength(100)]
        public string FirstName { get; set; } = string.Empty;
        
        [Required]
        [StringLength(100)]
        public string LastName { get; set; } = string.Empty;
        
        [Required]
        public string Password { get; set; } = string.Empty;
        
        [StringLength(200)]
        public string DisplayName { get; set; } = string.Empty;
        
        public string GridUrl { get; set; } = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
        
        public bool IsConnected { get; set; } = false;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime? LastLoginAt { get; set; }
        
        public string? AvatarUuid { get; set; }
        
        public string? AvatarRelayUuid { get; set; }
        
        public string? CurrentRegion { get; set; }
        
        public string Status { get; set; } = "Offline";
        
        // Auto Greeter settings
        public bool AutoGreeterEnabled { get; set; } = false;
        
        [StringLength(500)]
        public string AutoGreeterMessage { get; set; } = "Greetings {name}, welcome!";
        
        // Auto Greeter Return settings
        public bool AutoGreeterReturnEnabled { get; set; } = false;
        
        [StringLength(500)]
        public string AutoGreeterReturnMessage { get; set; } = "Welcome back {name}!";
        
        public int AutoGreeterReturnTimeHours { get; set; } = 3;
        
        // Navigation properties
        public virtual ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();
        public virtual ICollection<Notice> Notices { get; set; } = new List<Notice>();
    }
}