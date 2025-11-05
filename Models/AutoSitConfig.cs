using System.Text.Json.Serialization;

namespace RadegastWeb.Models
{
    /// <summary>
    /// Configuration for auto-sit functionality
    /// </summary>
    public class AutoSitConfig
    {
        /// <summary>
        /// Whether auto-sit is enabled for this account
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// UUID of the object to automatically sit on after login
        /// </summary>
        [JsonPropertyName("targetUuid")]
        public string? TargetUuid { get; set; }

        /// <summary>
        /// Delay in seconds before attempting to auto-sit after login (default: 180 = 3 minutes)
        /// </summary>
        [JsonPropertyName("delaySeconds")]
        public int DelaySeconds { get; set; } = 180;

        /// <summary>
        /// When this configuration was last updated
        /// </summary>
        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Number of retry attempts if initial sit fails (default: 3)
        /// </summary>
        [JsonPropertyName("maxRetries")]
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Delay between retry attempts in seconds (default: 60)
        /// </summary>
        [JsonPropertyName("retryDelaySeconds")]
        public int RetryDelaySeconds { get; set; } = 60;
    }
}