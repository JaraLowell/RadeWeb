using OpenMetaverse;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Represents an avatar detected through coarse location updates
    /// Used for extended range radar detection beyond draw distance
    /// </summary>
    public class CoarseLocationAvatar
    {
        public UUID ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public Vector3 Position { get; set; }
        public DateTime LastUpdate { get; set; }
        public ulong SimHandle { get; set; }
        public bool IsDetailed { get; set; } // True if we also have detailed Avatar object
        
        public CoarseLocationAvatar()
        {
            LastUpdate = DateTime.UtcNow;
        }
        
        public CoarseLocationAvatar(UUID id, Vector3 position, ulong simHandle, string name = "")
        {
            ID = id;
            Position = position;
            SimHandle = simHandle;
            Name = name;
            LastUpdate = DateTime.UtcNow;
            IsDetailed = false;
        }
    }
}