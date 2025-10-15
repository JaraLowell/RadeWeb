using System.ComponentModel.DataAnnotations;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Configuration for Corrade plugin settings
    /// </summary>
    public class CorradeConfig
    {
        /// <summary>
        /// The account ID that this Corrade instance is linked to.
        /// Only whispers received by this account will be processed for Corrade commands.
        /// If null or empty, all accounts will process whispers (legacy behavior).
        /// </summary>
        public string? LinkedAccountId { get; set; }
        
        /// <summary>
        /// Whether to allow objects (not just avatars) to send Corrade commands via whispers
        /// </summary>
        public bool AllowObjectCommands { get; set; } = false;
        
        public List<CorradeGroup> Groups { get; set; } = new();
    }

    /// <summary>
    /// Represents a group configuration for Corrade commands
    /// </summary>
    public class CorradeGroup
    {
        [Required]
        public string GroupUuid { get; set; } = string.Empty;
        
        [Required]
        public string Password { get; set; } = string.Empty;
        
        public string? GroupName { get; set; }
        
        /// <summary>
        /// Whether this group allows relaying messages to local chat
        /// </summary>
        public bool AllowLocalChat { get; set; } = true;
        
        /// <summary>
        /// Whether this group allows relaying messages to other groups
        /// </summary>
        public bool AllowGroupRelay { get; set; } = true;
        
        /// <summary>
        /// Whether this group allows relaying messages to individual avatars
        /// </summary>
        public bool AllowAvatarIM { get; set; } = true;
    }

    /// <summary>
    /// Parsed Corrade command from incoming whisper
    /// </summary>
    public class CorradeCommand
    {
        public string Command { get; set; } = string.Empty;
        public string? GroupUuid { get; set; }
        public string? Password { get; set; }
        public string? Entity { get; set; }
        public string? Message { get; set; }
        public string? TargetUuid { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new();
        public bool IsValid { get; set; }
        public string? ValidationError { get; set; }
    }

    /// <summary>
    /// Result of processing a Corrade command
    /// </summary>
    public class CorradeCommandResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? ErrorCode { get; set; }
        public CorradeCommand? ProcessedCommand { get; set; }
    }
}