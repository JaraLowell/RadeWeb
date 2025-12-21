using System.Xml.Serialization;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Represents an attachment item worn by the avatar
    /// </summary>
    [XmlRoot("Attachment")]
    public class AttachmentItem
    {
        /// <summary>
        /// UUID of the attachment item
        /// </summary>
        [XmlElement("UUID")]
        public string Uuid { get; set; } = string.Empty;
        
        /// <summary>
        /// Name of the attachment item
        /// </summary>
        [XmlElement("Name")]
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// Attachment point where the item is attached (e.g., "Right Hand", "Head")
        /// </summary>
        [XmlElement("AttachmentPoint")]
        public string AttachmentPoint { get; set; } = string.Empty;
        
        /// <summary>
        /// Whether this attachment can be touched
        /// </summary>
        [XmlElement("IsTouchable")]
        public bool IsTouchable { get; set; }
        
        /// <summary>
        /// UUID of the primitive object (for touching)
        /// </summary>
        [XmlElement("PrimUUID")]
        public string PrimUuid { get; set; } = string.Empty;
        
        /// <summary>
        /// When this attachment information was cached
        /// </summary>
        [XmlElement("CachedAt")]
        public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    }
    
    /// <summary>
    /// DTO for transferring attachment information to the web client
    /// </summary>
    public class AttachmentDto
    {
        public string Uuid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string AttachmentPoint { get; set; } = string.Empty;
        public bool IsTouchable { get; set; }
        public string PrimUuid { get; set; } = string.Empty;
    }
    
    /// <summary>
    /// Collection of attachments for XML serialization
    /// </summary>
    [XmlRoot("Attachments")]
    public class AttachmentCollection
    {
        [XmlElement("Attachment")]
        public List<AttachmentItem> Items { get; set; } = new List<AttachmentItem>();
        
        [XmlElement("LastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
