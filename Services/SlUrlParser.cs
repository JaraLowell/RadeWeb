using System.Text.RegularExpressions;
using System.Web;
using OpenMetaverse;

namespace RadegastWeb.Services
{
    public class SlUrlParser : ISlUrlParser
    {
        private readonly ILogger<SlUrlParser> _logger;
        private readonly INameResolutionService _nameResolutionService;

        // URL detection regex
        private static readonly Regex UrlRegex = new(
            @"(https?://[^ \r\n]+)|(\[secondlife://[^ \]\r\n]* ?(?:[^\]\r\n]*)])|(secondlife://[^ \r\n]*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        // Map link regex (slurl.com and maps.secondlife.com)
        private static readonly Regex MapLinkRegex = new(
            @"^((https?://(slurl\.com|maps\.secondlife\.com)/secondlife/)(?<region>[^ /]+)(/(?<coords>\d+)){0,3})",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase
        );

        // Comprehensive SLURL pattern based on Radegast's parser
        private static readonly Regex SlUrlPattern = new(
            @"(?<startingbrace>\[)?(" +
                @"(?<regionuri>secondlife://(?<region_name>[^\]/ ]+)(/(?<local_x>[0-9]+))?(/(?<local_y>[0-9]+))?(/(?<local_z>[0-9]+))?)|" +
                @"(?<appuri>secondlife:///app/(" +
                    @"(?<appcommand>agent)/(?<agent_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/(?<action>[a-z]+)|" +
                    @"(?<appcommand>apperance)/show|" +
                    @"(?<appcommand>balance)/request|" +
                    @"(?<appcommand>chat)/(?<channel>\d+)/(?<text>[^\] ]+)|" +
                    @"(?<appcommand>classified)/(?<classified_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/about|" +
                    @"(?<appcommand>event)/(?<event_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/about|" +
                    @"(?<appcommand>group)/(" +
                        @"(?<group_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/(?<action>[a-z]+)|" +
                        @"(?<action>create)|" +
                        @"(?<action>list/show))|" +
                    @"(?<appcommand>help)/?<help_query>([^\] ]+)|" +
                    @"(?<appcommand>inventory)/(" +
                        @"(?<inventory_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/(?<action>select)/?" +
                            @"([?&](" +
                                @"name=(?<name>[^& ]+)" +
                            @"))*|" +
                        @"(?<action>show))|" +
                    @"(?<appcommand>maptrackavatar)/(?<friend_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})|" +
                    @"(?<appcommand>objectim)/(?<object_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/?" +
                        @"([?&](" +
                            @"name=(?<name>[^& ]+)|" +
                            @"owner=(?<owner>[^& ]+)|" +
                            @"groupowned=(?<groupowned>true)|" +
                            @"slurl=(?<region_name>[^\]/ ]+)(/(?<x>[0-9]+\.?[0-9]*))?(/(?<y>[0-9]+\.?[0-9]*))?(/(?<z>[0-9]+\.?[0-9]*))?" +
                        @"))*|" +
                    @"(?<appcommand>parcel)/(?<parcel_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/about|" +
                    @"(?<appcommand>search)/(?<category>[a-z]+)/(?<search_term>[^\]/ ]+)|" +
                    @"(?<appcommand>sharewithavatar)/(?<agent_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})|" +
                    @"(?<appcommand>teleport)/(?<region_name>[^\]/ ]+)(/(?<local_x>[0-9]+))?(/(?<local_y>[0-9]+))?(/(?<local_z>[0-9]+))?|" +
                    @"(?<appcommand>voicecallavatar)/(?<agent_id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})|" +
                    @"(?<appcommand>wear_folder)/?folder_id=(?<inventory_folder_uuid>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})|" +
                    @"(?<appcommand>worldmap)/(?<region_name>[^\]/ ]+)(/(?<local_x>[0-9]+))?(/(?<local_y>[0-9]+))?(/(?<local_z>[0-9]+))?)))" +
            @"( (?<endingbrace>[^\]]*)\])?",
            RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled
        );

        public SlUrlParser(ILogger<SlUrlParser> logger, INameResolutionService nameResolutionService)
        {
            _logger = logger;
            _nameResolutionService = nameResolutionService;
        }

        public async Task<ParsedUrlInfo> ParseUrlAsync(string url, Guid accountId)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return new ParsedUrlInfo { DisplayText = url, OriginalUrl = url, IsClickable = false };
            }

            // Check if resolution is disabled
            if (!_nameResolutionService.IsResolutionEnabled(accountId))
            {
                return new ParsedUrlInfo { DisplayText = url, OriginalUrl = url, IsClickable = true };
            }

            // Try map link first
            if (TryParseMapLink(url) is MapLinkInfo mapInfo)
            {
                return new ParsedUrlInfo
                {
                    DisplayText = mapInfo.ToString(),
                    OriginalUrl = url,
                    IsClickable = true,
                    StyleClass = "map-link",
                    ClickAction = "map"
                };
            }

            // Try SLURL pattern
            var match = SlUrlPattern.Match(url);
            if (!match.Success)
            {
                return new ParsedUrlInfo { DisplayText = url, OriginalUrl = url, IsClickable = true };
            }

            // Handle custom named links in brackets [secondlife://... Custom Name]
            if (match.Groups["startingbrace"].Success && match.Groups["endingbrace"].Length > 0)
            {
                return new ParsedUrlInfo
                {
                    DisplayText = HttpUtility.UrlDecode(match.Groups["endingbrace"].Value),
                    OriginalUrl = url,
                    IsClickable = true,
                    StyleClass = "custom-link"
                };
            }

            // Handle region URIs
            if (match.Groups["regionuri"].Success)
            {
                return ParseRegionUri(match, url, accountId);
            }

            // Handle app URIs
            if (match.Groups["appuri"].Success)
            {
                return await ParseAppUriAsync(match, url, accountId);
            }

            return new ParsedUrlInfo { DisplayText = url, OriginalUrl = url, IsClickable = true };
        }

        public async Task<string> ProcessChatMessageAsync(string message, Guid accountId)
        {
            if (string.IsNullOrWhiteSpace(message) || !ContainsSlUrls(message))
            {
                return message;
            }

            var urls = ExtractUrls(message);
            var processedMessage = message;

            foreach (var url in urls)
            {
                try
                {
                    var parsedInfo = await ParseUrlAsync(url, accountId);
                    if (!string.IsNullOrEmpty(parsedInfo.DisplayText) && parsedInfo.DisplayText != url)
                    {
                        // Replace the URL with the display text, but keep it clickable
                        var replacement = $"<a href=\"{url}\" class=\"slurl-link {parsedInfo.StyleClass}\" data-action=\"{parsedInfo.ClickAction}\">{parsedInfo.DisplayText}</a>";
                        processedMessage = processedMessage.Replace(url, replacement);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing URL {Url} in chat message", url);
                }
            }

            return processedMessage;
        }

        public bool ContainsSlUrls(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && UrlRegex.IsMatch(text);
        }

        public IEnumerable<string> ExtractUrls(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Enumerable.Empty<string>();

            var matches = UrlRegex.Matches(text);
            return matches.Cast<Match>().Select(m => m.Value).Distinct();
        }

        public MapLinkInfo? TryParseMapLink(string url)
        {
            var match = MapLinkRegex.Match(url);
            if (!match.Success)
                return null;

            var region = "";
            var coords = new List<int>();

            var regionMatch = match.Groups["region"];
            if (regionMatch.Success)
            {
                region = regionMatch.Value;
            }

            var coordsMatch = match.Groups["coords"];
            if (coordsMatch.Success)
            {
                foreach (Capture coordRaw in coordsMatch.Captures)
                {
                    if (int.TryParse(coordRaw.Value, out int coord))
                    {
                        coords.Add(coord);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            var x = coords.Count > 0 ? coords[0] : (int?)null;
            var y = coords.Count > 1 ? coords[1] : (int?)null;
            var z = coords.Count > 2 ? coords[2] : (int?)null;

            return new MapLinkInfo
            {
                RegionName = HttpUtility.UrlDecode(region),
                X = x,
                Y = y,
                Z = z
            };
        }

        private ParsedUrlInfo ParseRegionUri(Match match, string originalUrl, Guid accountId)
        {
            var regionName = HttpUtility.UrlDecode(match.Groups["region_name"].Value);
            var coordinateString = "";

            if (match.Groups["local_x"].Success)
            {
                coordinateString += " (" + match.Groups["local_x"].Value;
            }
            if (match.Groups["local_y"].Success)
            {
                coordinateString += "," + match.Groups["local_y"].Value;
            }
            if (match.Groups["local_z"].Success)
            {
                coordinateString += "," + match.Groups["local_z"].Value;
            }
            if (coordinateString != "")
            {
                coordinateString += ")";
            }

            return new ParsedUrlInfo
            {
                DisplayText = regionName + coordinateString,
                OriginalUrl = originalUrl,
                IsClickable = true,
                StyleClass = "region-link",
                ClickAction = "teleport"
            };
        }

        private async Task<ParsedUrlInfo> ParseAppUriAsync(Match match, string originalUrl, Guid accountId)
        {
            var appCommand = match.Groups["appcommand"].Value;

            return appCommand switch
            {
                "agent" => await ParseAgentLinkAsync(match, originalUrl, accountId),
                "group" => await ParseGroupLinkAsync(match, originalUrl, accountId),
                "parcel" => await ParseParcelLinkAsync(match, originalUrl, accountId),
                "objectim" => ParseObjectImLink(match, originalUrl),
                "teleport" => ParseTeleportLink(match, originalUrl),
                "worldmap" => ParseWorldMapLink(match, originalUrl),
                "inventory" => ParseInventoryLink(match, originalUrl),
                _ => new ParsedUrlInfo 
                { 
                    DisplayText = originalUrl, 
                    OriginalUrl = originalUrl, 
                    IsClickable = true,
                    StyleClass = "unknown-link"
                }
            };
        }

        private async Task<ParsedUrlInfo> ParseAgentLinkAsync(Match match, string originalUrl, Guid accountId)
        {
            if (!UUID.TryParse(match.Groups["agent_id"].Value, out UUID agentId))
            {
                return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
            }

            var action = match.Groups["action"].Value;
            var resolveType = action switch
            {
                "displayname" => ResolveType.AgentDisplayName,
                "username" => ResolveType.AgentUsername,
                _ => ResolveType.AgentDefaultName
            };

            try
            {
                var resolvedName = await _nameResolutionService.ResolveAsync(accountId, agentId, resolveType);
                var displayText = action switch
                {
                    "about" or "inspect" or "completename" or "displayname" or "username" => resolvedName,
                    "im" => $"IM {resolvedName}",
                    "offerteleport" => $"Offer Teleport to {resolvedName}",
                    "pay" => $"Pay {resolvedName}",
                    "requestfriend" => $"Friend Request {resolvedName}",
                    "mute" => $"Mute {resolvedName}",
                    "unmute" => $"Unmute {resolvedName}",
                    "mention" => $"@{resolvedName}",
                    _ => resolvedName
                };

                return new ParsedUrlInfo
                {
                    DisplayText = displayText,
                    OriginalUrl = originalUrl,
                    IsClickable = true,
                    StyleClass = "agent-link",
                    ClickAction = action,
                    ResolvedId = agentId
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving agent name for {AgentId}", agentId);
                return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
            }
        }

        private async Task<ParsedUrlInfo> ParseGroupLinkAsync(Match match, string originalUrl, Guid accountId)
        {
            var action = match.Groups["action"].Value;

            if (action == "about" || action == "inspect")
            {
                if (!UUID.TryParse(match.Groups["group_id"].Value, out UUID groupId))
                {
                    return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
                }

                try
                {
                    var resolvedName = await _nameResolutionService.ResolveAsync(accountId, groupId, ResolveType.Group);
                    return new ParsedUrlInfo
                    {
                        DisplayText = resolvedName,
                        OriginalUrl = originalUrl,
                        IsClickable = true,
                        StyleClass = "group-link",
                        ClickAction = "group",
                        ResolvedId = groupId
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error resolving group name for {GroupId}", groupId);
                }
            }

            return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
        }

        private async Task<ParsedUrlInfo> ParseParcelLinkAsync(Match match, string originalUrl, Guid accountId)
        {
            if (!UUID.TryParse(match.Groups["parcel_id"].Value, out UUID parcelId))
            {
                return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
            }

            try
            {
                var resolvedName = await _nameResolutionService.ResolveAsync(accountId, parcelId, ResolveType.Parcel);
                return new ParsedUrlInfo
                {
                    DisplayText = resolvedName,
                    OriginalUrl = originalUrl,
                    IsClickable = true,
                    StyleClass = "parcel-link",
                    ClickAction = "parcel",
                    ResolvedId = parcelId
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving parcel name for {ParcelId}", parcelId);
                return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
            }
        }

        private ParsedUrlInfo ParseObjectImLink(Match match, string originalUrl)
        {
            var name = match.Groups["name"];
            if (name.Success && !string.IsNullOrEmpty(name.Value))
            {
                return new ParsedUrlInfo
                {
                    DisplayText = HttpUtility.UrlDecode(name.Value),
                    OriginalUrl = originalUrl,
                    IsClickable = true,
                    StyleClass = "object-link",
                    ClickAction = "objectim"
                };
            }

            return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
        }

        private ParsedUrlInfo ParseTeleportLink(Match match, string originalUrl)
        {
            var regionName = HttpUtility.UrlDecode(match.Groups["region_name"].Value);
            var coordinateString = "";

            if (match.Groups["local_x"].Success)
            {
                coordinateString += " (" + match.Groups["local_x"].Value;
            }
            if (match.Groups["local_y"].Success)
            {
                coordinateString += "," + match.Groups["local_y"].Value;
            }
            if (match.Groups["local_z"].Success)
            {
                coordinateString += "," + match.Groups["local_z"].Value;
            }
            if (coordinateString != "")
            {
                coordinateString += ")";
            }

            return new ParsedUrlInfo
            {
                DisplayText = $"Teleport to {regionName}{coordinateString}",
                OriginalUrl = originalUrl,
                IsClickable = true,
                StyleClass = "teleport-link",
                ClickAction = "teleport"
            };
        }

        private ParsedUrlInfo ParseWorldMapLink(Match match, string originalUrl)
        {
            var regionName = HttpUtility.UrlDecode(match.Groups["region_name"].Value);
            var x = match.Groups["local_x"].Success ? match.Groups["local_x"].Value : "128";
            var y = match.Groups["local_y"].Success ? match.Groups["local_y"].Value : "128";
            var z = match.Groups["local_z"].Success ? match.Groups["local_z"].Value : "0";

            return new ParsedUrlInfo
            {
                DisplayText = $"Show Map for {regionName} ({x},{y},{z})",
                OriginalUrl = originalUrl,
                IsClickable = true,
                StyleClass = "map-link",
                ClickAction = "map"
            };
        }

        private ParsedUrlInfo ParseInventoryLink(Match match, string originalUrl)
        {
            var action = match.Groups["action"].Value;
            if (action == "select" && match.Groups["name"].Success)
            {
                return new ParsedUrlInfo
                {
                    DisplayText = HttpUtility.UrlDecode(match.Groups["name"].Value),
                    OriginalUrl = originalUrl,
                    IsClickable = true,
                    StyleClass = "inventory-link",
                    ClickAction = "inventory"
                };
            }

            return new ParsedUrlInfo { DisplayText = originalUrl, OriginalUrl = originalUrl, IsClickable = true };
        }
    }
}