namespace RadegastWeb.Models
{
    public class AccountStatus
    {
        public Guid AccountId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public string Status { get; set; } = "Offline";
        public string? CurrentRegion { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public string? AvatarUuid { get; set; }
        public string GridUrl { get; set; } = string.Empty;
    }
    
    public class LoginRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string GridUrl { get; set; } = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
    }
    
    public class ChatMessageDto
    {
        public string SenderName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string ChatType { get; set; } = "Normal"; // Normal, Whisper, Shout, IM, Group, System
        public string? Channel { get; set; }
        public DateTime Timestamp { get; set; }
        public string? RegionName { get; set; }
        public Guid AccountId { get; set; }
        public string? SenderId { get; set; } // UUID of sender
        public string? TargetId { get; set; } // For IM/Group chats
        public string? SessionId { get; set; } // For organizing IM/Group sessions
        public string? SessionName { get; set; } // Display name for IM/Group
    }
    
    public class SendChatRequest
    {
        public Guid AccountId { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ChatType { get; set; } = "Normal"; // Normal, Whisper, Shout, IM
        public int Channel { get; set; } = 0;
        public string? TargetId { get; set; } // For IM messages
        public string? SessionId { get; set; } // For group messages
    }
    
    public class AvatarDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public float Distance { get; set; }
        public string Status { get; set; } = "Online"; // Online, Away, Busy
        public string? GroupTitle { get; set; }
        public Guid AccountId { get; set; }
    }
    
    public class RegionInfoDto
    {
        public string Name { get; set; } = string.Empty;
        public string? MaturityLevel { get; set; }
        public int AvatarCount { get; set; }
        public string? RegionType { get; set; }
        public Guid AccountId { get; set; }
        public float RegionX { get; set; }
        public float RegionY { get; set; }
    }

    public class ChatSessionDto
    {
        public string SessionId { get; set; } = string.Empty;
        public string SessionName { get; set; } = string.Empty;
        public string ChatType { get; set; } = string.Empty; // IM, Group
        public string? TargetId { get; set; }
        public int UnreadCount { get; set; }
        public DateTime LastActivity { get; set; }
        public Guid AccountId { get; set; }
        public bool IsActive { get; set; }
    }

    public class SendIMRequest
    {
        public string TargetId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class SetPresenceRequest
    {
        public bool IsEnabled { get; set; }
    }

    public class NoticeReceivedEventDto
    {
        public NoticeDto Notice { get; set; } = new();
        public string SessionId { get; set; } = string.Empty;
        public string DisplayMessage { get; set; } = string.Empty;
    }
}