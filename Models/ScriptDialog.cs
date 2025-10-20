using OpenMetaverse;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Represents a script dialog received from Second Life
    /// </summary>
    public class ScriptDialogDto
    {
        /// <summary>
        /// Unique identifier for this dialog
        /// </summary>
        public string DialogId { get; set; } = string.Empty;
        
        /// <summary>
        /// Account this dialog belongs to
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// The dialog message text
        /// </summary>
        public string Message { get; set; } = string.Empty;
        
        /// <summary>
        /// Name of the object that triggered the dialog
        /// </summary>
        public string ObjectName { get; set; } = string.Empty;
        
        /// <summary>
        /// UUID of the object that triggered the dialog
        /// </summary>
        public string ObjectId { get; set; } = string.Empty;
        
        /// <summary>
        /// UUID of the object owner
        /// </summary>
        public string OwnerId { get; set; } = string.Empty;
        
        /// <summary>
        /// Owner's first name
        /// </summary>
        public string OwnerFirstName { get; set; } = string.Empty;
        
        /// <summary>
        /// Owner's last name
        /// </summary>
        public string OwnerLastName { get; set; } = string.Empty;
        
        /// <summary>
        /// Full owner name (calculated)
        /// </summary>
        public string OwnerName => $"{OwnerFirstName} {OwnerLastName}".Trim();
        
        /// <summary>
        /// Channel number for responses
        /// </summary>
        public int Channel { get; set; }
        
        /// <summary>
        /// List of button labels
        /// </summary>
        public List<string> Buttons { get; set; } = new();
        
        /// <summary>
        /// Image UUID to display (optional)
        /// </summary>
        public string? ImageId { get; set; }
        
        /// <summary>
        /// Whether this dialog expects text input (llTextBox)
        /// </summary>
        public bool IsTextInput { get; set; }
        
        /// <summary>
        /// When the dialog was received
        /// </summary>
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Whether the dialog has been responded to
        /// </summary>
        public bool IsResponded { get; set; }
        
        /// <summary>
        /// When the dialog expires (if applicable)
        /// </summary>
        public DateTime? ExpiresAt { get; set; }
        
        // SLT formatted timestamps for display
        public string? SLTReceivedAt { get; set; } // MMM dd, HH:mm:ss format
        public string? SLTExpiresAt { get; set; } // MMM dd, HH:mm:ss format (if applicable)
    }
    
    /// <summary>
    /// Request to respond to a script dialog
    /// </summary>
    public class ScriptDialogResponseRequest
    {
        /// <summary>
        /// Account ID
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// Dialog ID being responded to
        /// </summary>
        public string DialogId { get; set; } = string.Empty;
        
        /// <summary>
        /// Index of the button clicked (for button dialogs)
        /// </summary>
        public int ButtonIndex { get; set; }
        
        /// <summary>
        /// Text of the button clicked
        /// </summary>
        public string ButtonText { get; set; } = string.Empty;
        
        /// <summary>
        /// Text input (for text box dialogs)
        /// </summary>
        public string? TextInput { get; set; }
    }
    
    /// <summary>
    /// Request to dismiss a script dialog without responding
    /// </summary>
    public class ScriptDialogDismissRequest
    {
        /// <summary>
        /// Account ID
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// Dialog ID being dismissed
        /// </summary>
        public string DialogId { get; set; } = string.Empty;
    }
    
    /// <summary>
    /// Script permissions request dialog
    /// </summary>
    public class ScriptPermissionDto
    {
        /// <summary>
        /// Unique identifier for this permission request
        /// </summary>
        public string RequestId { get; set; } = string.Empty;
        
        /// <summary>
        /// Account this request belongs to
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// Name of the object requesting permissions
        /// </summary>
        public string ObjectName { get; set; } = string.Empty;
        
        /// <summary>
        /// UUID of the object requesting permissions
        /// </summary>
        public string ObjectId { get; set; } = string.Empty;
        
        /// <summary>
        /// Task/Item UUID
        /// </summary>
        public string ItemId { get; set; } = string.Empty;
        
        /// <summary>
        /// Name of the object owner
        /// </summary>
        public string ObjectOwner { get; set; } = string.Empty;
        
        /// <summary>
        /// Permissions being requested
        /// </summary>
        public ScriptPermission Permissions { get; set; }
        
        /// <summary>
        /// Human-readable permissions description
        /// </summary>
        public string PermissionsDescription { get; set; } = string.Empty;
        
        /// <summary>
        /// When the request was received
        /// </summary>
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Whether the request has been responded to
        /// </summary>
        public bool IsResponded { get; set; }
    }
    
    /// <summary>
    /// Response to a script permission request
    /// </summary>
    public class ScriptPermissionResponseRequest
    {
        /// <summary>
        /// Account ID
        /// </summary>
        public Guid AccountId { get; set; }
        
        /// <summary>
        /// Permission request ID being responded to
        /// </summary>
        public string RequestId { get; set; } = string.Empty;
        
        /// <summary>
        /// Whether to grant the permissions
        /// </summary>
        public bool Grant { get; set; }
        
        /// <summary>
        /// Whether to mute the object instead
        /// </summary>
        public bool Mute { get; set; }
    }
    
    /// <summary>
    /// Event args for script dialog events
    /// </summary>
    public class ScriptDialogEventArgs : EventArgs
    {
        public ScriptDialogDto Dialog { get; set; }
        
        public ScriptDialogEventArgs(ScriptDialogDto dialog)
        {
            Dialog = dialog;
        }
    }
    
    /// <summary>
    /// Event args for script permission events
    /// </summary>
    public class ScriptPermissionEventArgs : EventArgs
    {
        public ScriptPermissionDto Permission { get; set; }
        
        public ScriptPermissionEventArgs(ScriptPermissionDto permission)
        {
            Permission = permission;
        }
    }
}