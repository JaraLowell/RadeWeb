using OpenMetaverse;

namespace RadegastWeb.Models
{
    public class NoticeDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public string FromId { get; set; } = string.Empty;
        public string? GroupId { get; set; }
        public string? GroupName { get; set; }
        public DateTime Timestamp { get; set; }
        public NoticeType Type { get; set; }
        public bool HasAttachment { get; set; }
        public string? AttachmentName { get; set; }
        public string? AttachmentType { get; set; }
        public Guid AccountId { get; set; }
        public bool RequiresAcknowledgment { get; set; }
        public bool IsAcknowledged { get; set; }
        public bool IsRead { get; set; } = false;
        
        // SLT formatted timestamps for display
        public string? SLTTime { get; set; } // HH:mm:ss format
        public string? SLTDateTime { get; set; } // MMM dd, HH:mm:ss format
    }

    public enum NoticeType
    {
        Group,
        Region,
        System
    }

    public class NoticeReceivedEventArgs : EventArgs
    {
        public NoticeDto Notice { get; set; }
        public string SessionId { get; set; } = string.Empty; // "local-chat" for region notices, "group-{groupId}" for group notices
        public string DisplayMessage { get; set; } = string.Empty; // Formatted message for chat display

        public NoticeReceivedEventArgs(NoticeDto notice, string sessionId, string displayMessage)
        {
            Notice = notice;
            SessionId = sessionId;
            DisplayMessage = displayMessage;
        }
    }
}