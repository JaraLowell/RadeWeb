using OpenMetaverse;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Detailed region statistics based on Radegast's UpdateSimDisplay implementation
    /// Contains simulation performance data, statistics, and region information
    /// </summary>
    public class RegionStatsDto
    {
        public Guid AccountId { get; set; }
        
        // Basic region information
        public string RegionName { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductSku { get; set; } = string.Empty;
        public string SimVersion { get; set; } = string.Empty;
        public string DataCenter { get; set; } = string.Empty;
        public int CPUClass { get; set; }
        public float CPURatio { get; set; }
        public string MaturityLevel { get; set; } = string.Empty;
        
        // Simulation performance statistics
        public float TimeDilation { get; set; }
        public float FPS { get; set; }
        public float PhysicsFPS { get; set; }
        
        // Agent and object counts
        public int MainAgents { get; set; }
        public int ChildAgents { get; set; }
        public int Objects { get; set; }
        public int ActiveObjects { get; set; }
        public int ActiveScripts { get; set; }
        
        // Network and processing times (in milliseconds)
        public float TotalFrameTime { get; set; }
        public float NetTime { get; set; }
        public float PhysicsTime { get; set; }
        public float SimTime { get; set; }
        public float AgentTime { get; set; }
        public float ImagesTime { get; set; }
        public float ScriptTime { get; set; }
        public float SpareTime { get; set; }
        
        // Network statistics
        public int PendingDownloads { get; set; }
        public int PendingUploads { get; set; }
        public int PendingLocalUploads { get; set; }
        
        // Position information
        public float RegionX { get; set; }
        public float RegionY { get; set; }
        public Vector3 MyPosition { get; set; }
        
        // Update timestamp
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        
        // Helper properties for UI display
        public string RegionCoordinates => $"({RegionX:0}, {RegionY:0})";
        public string MyPositionText => $"({MyPosition.X:0}, {MyPosition.Y:0}, {MyPosition.Z:0})";
        public float TotalAgents => MainAgents + ChildAgents;
        public float PerformancePercentage => Math.Max(0f, Math.Min(100f, (SpareTime / (1000f / 45f)) * 100f));
        public string PerformanceStatus
        {
            get
            {
                var percentage = PerformancePercentage;
                return percentage switch
                {
                    >= 80f => "Excellent",
                    >= 60f => "Good", 
                    >= 40f => "Fair",
                    >= 20f => "Poor",
                    _ => "Critical"
                };
            }
        }
    }
}