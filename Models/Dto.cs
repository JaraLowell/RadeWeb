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
        public bool HasAiBotActive { get; set; } = false;
        public bool HasCorradeActive { get; set; } = false;
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
        
        // SLT formatted timestamps for display
        public string? SLTTime { get; set; } // HH:mm:ss format
        public string? SLTDateTime { get; set; } // MMM dd, HH:mm:ss format
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
        public PositionDto? Position { get; set; }
    }

    public class PositionDto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
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

    public class SitRequest
    {
        public string? ObjectId { get; set; } // UUID string, null or empty for ground
    }

    public class RadarStatsDto
    {
        public int DetailedAvatarCount { get; set; }
        public int CoarseLocationAvatarCount { get; set; }
        public int TotalUniqueAvatars { get; set; }
        public double MaxDetectionRange { get; set; }
        public int SimAvatarCount { get; set; }
    }

    public class SitStateChangedEventArgs : EventArgs
    {
        public bool IsSitting { get; set; }
        public string? ObjectId { get; set; } // UUID string of object being sat on, null if on ground
        public uint LocalId { get; set; } // Local ID of object (0 if on ground)

        public SitStateChangedEventArgs(bool isSitting, string? objectId = null, uint localId = 0)
        {
            IsSitting = isSitting;
            ObjectId = objectId;
            LocalId = localId;
        }
    }

    public class ObjectInfo
    {
        public string Id { get; set; } = string.Empty; // UUID as string
        public uint LocalId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public OpenMetaverse.Vector3 Position { get; set; }
        public string OwnerId { get; set; } = string.Empty; // UUID as string
        public bool CanSit { get; set; }
    }
}