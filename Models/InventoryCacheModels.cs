using System.Xml.Serialization;

namespace RadegastWeb.Models
{
    [XmlRoot("InventoryNode")]
    public class InventoryCacheNode
    {
        [XmlElement("UUID")]
        public string Uuid { get; set; } = string.Empty;

        [XmlElement("ParentUUID")]
        public string ParentUuid { get; set; } = string.Empty;

        [XmlElement("Name")]
        public string Name { get; set; } = string.Empty;

        [XmlElement("IsFolder")]
        public bool IsFolder { get; set; }

        [XmlElement("TypeName")]
        public string TypeName { get; set; } = string.Empty;

        [XmlElement("FolderType")]
        public string FolderType { get; set; } = string.Empty;

        [XmlElement("AssetType")]
        public string AssetType { get; set; } = string.Empty;

        [XmlElement("IsLink")]
        public bool IsLink { get; set; }

        [XmlElement("IsWorn")]
        public bool IsWorn { get; set; }

        [XmlElement("WornAttachmentPoint")]
        public string WornAttachmentPoint { get; set; } = string.Empty;

        [XmlElement("IconClass")]
        public string IconClass { get; set; } = "fas fa-file";

        [XmlArray("Children")]
        [XmlArrayItem("Node")]
        public List<InventoryCacheNode> Children { get; set; } = new();
    }

    [XmlRoot("InventoryCache")]
    public class InventoryCacheCollection
    {
        [XmlElement("LastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [XmlArray("RootNodes")]
        [XmlArrayItem("Node")]
        public List<InventoryCacheNode> RootNodes { get; set; } = new();
    }
}