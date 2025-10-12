using OpenMetaverse;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    public interface ISlUrlParser
    {
        /// <summary>
        /// Parses a SLURL and returns the display text with optional styling information
        /// </summary>
        /// <param name="url">The URL to parse</param>
        /// <param name="accountId">Account ID for name resolution context</param>
        /// <returns>Parsed URL information</returns>
        Task<ParsedUrlInfo> ParseUrlAsync(string url, Guid accountId);

        /// <summary>
        /// Processes a chat message and replaces SLURLs with display names
        /// </summary>
        /// <param name="message">The chat message to process</param>
        /// <param name="accountId">Account ID for name resolution context</param>
        /// <returns>Processed message with URLs replaced</returns>
        Task<string> ProcessChatMessageAsync(string message, Guid accountId);

        /// <summary>
        /// Checks if a string contains any recognizable SLURLs
        /// </summary>
        /// <param name="text">Text to check</param>
        /// <returns>True if contains SLURLs</returns>
        bool ContainsSlUrls(string text);

        /// <summary>
        /// Extracts all SLURLs from a text string
        /// </summary>
        /// <param name="text">Text to extract URLs from</param>
        /// <returns>List of found URLs</returns>
        IEnumerable<string> ExtractUrls(string text);

        /// <summary>
        /// Attempts to parse a map link (slurl.com or maps.secondlife.com)
        /// </summary>
        /// <param name="url">Map URL to parse</param>
        /// <returns>Map link information if valid</returns>
        MapLinkInfo? TryParseMapLink(string url);
    }

    public class ParsedUrlInfo
    {
        public string DisplayText { get; set; } = string.Empty;
        public string OriginalUrl { get; set; } = string.Empty;
        public string? ClickAction { get; set; } // For future use in frontend
        public bool IsClickable { get; set; } = true;
        public string? StyleClass { get; set; } // CSS class for styling
        public UUID? ResolvedId { get; set; } // The resolved UUID if applicable
    }

    public class MapLinkInfo
    {
        public string RegionName { get; set; } = string.Empty;
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Z { get; set; }

        public override string ToString()
        {
            var extraRegionInfo = "";
            if (Z != null)
            {
                extraRegionInfo += $" ({X ?? 0},{Y ?? 0},{Z})";
            }
            else if (Y != null)
            {
                extraRegionInfo += $" ({X ?? 0},{Y})";
            }
            else if (X != null)
            {
                extraRegionInfo += $" ({X})";
            }

            return RegionName + extraRegionInfo;
        }
    }

    public enum ResolveType
    {
        /// <summary>
        /// Client specified name format (default display name or username fallback)
        /// </summary>
        AgentDefaultName,
        /// <summary>
        /// Display name only
        /// </summary>
        AgentDisplayName,
        /// <summary>
        /// Username (first.last) only
        /// </summary>
        AgentUsername,
        /// <summary>
        /// Group name
        /// </summary>
        Group,
        /// <summary>
        /// Parcel name
        /// </summary>
        Parcel
    }
}