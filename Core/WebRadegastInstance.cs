using OpenMetaverse;
using RadegastWeb.Models;
using RadegastWeb.Services;
using System.Collections.Concurrent;
using System.Linq;

namespace RadegastWeb.Core
{
    public class WebRadegastInstance : IDisposable
    {
        private readonly ILogger<WebRadegastInstance> _logger;
        private readonly IDisplayNameService _displayNameService;
        private readonly INoticeService _noticeService;
        private readonly ISlUrlParser _urlParser;
        private readonly INameResolutionService _nameResolutionService;
        private readonly IGroupService _groupService;
        private readonly IGlobalDisplayNameCache _globalDisplayNameCache;
        private readonly GridClient _client;
        private readonly string _accountId;
        private readonly string _cacheDir;
        private readonly string _logDir;
        private bool _disposed;
        
        // Constants for radar functionality (matching Radegast behavior)
        private const double MAX_DISTANCE = 362.0; // One sim corner-to-corner distance
        
        private readonly ConcurrentDictionary<UUID, Avatar> _nearbyAvatars = new();
        
        // Dictionary to store coarse location avatars (extended range detection)
        private readonly ConcurrentDictionary<UUID, CoarseLocationAvatar> _coarseLocationAvatars = new();
        private readonly ConcurrentDictionary<UUID, ulong> _avatarSimHandles = new();
        
        private readonly ConcurrentDictionary<string, ChatSessionDto> _chatSessions = new();
        private readonly ConcurrentDictionary<UUID, Group> _groups = new();
        private System.Threading.Timer? _displayNameRefreshTimer;

        public string AccountId => _accountId;
        public GridClient Client => _client;
        public bool IsConnected => _client.Network.Connected;
        public string Status { get; private set; } = "Offline";
        public Account AccountInfo { get; private set; }
        public IReadOnlyDictionary<UUID, Group> Groups => _groups;

        // Events for real-time updates
        public event EventHandler<ChatMessageDto>? ChatReceived;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<bool>? ConnectionChanged;
        public event EventHandler<AvatarDto>? AvatarAdded;
        public event EventHandler<string>? AvatarRemoved; // UUID string
        public event EventHandler<AvatarDto>? AvatarUpdated; // New event for display name changes
        public event EventHandler<RegionInfoDto>? RegionChanged;
        public event EventHandler<ChatSessionDto>? ChatSessionUpdated;
        public event EventHandler<NoticeReceivedEventArgs>? NoticeReceived;

        public WebRadegastInstance(Account account, ILogger<WebRadegastInstance> logger, IDisplayNameService displayNameService, INoticeService noticeService, ISlUrlParser urlParser, INameResolutionService nameResolutionService, IGroupService groupService, IGlobalDisplayNameCache globalDisplayNameCache)
        {
            _logger = logger;
            _displayNameService = displayNameService;
            _noticeService = noticeService;
            _urlParser = urlParser;
            _nameResolutionService = nameResolutionService;
            _groupService = groupService;
            _globalDisplayNameCache = globalDisplayNameCache;
            AccountInfo = account;
            _accountId = account.Id.ToString();
            
            // Register this instance with the name resolution service
            _nameResolutionService.RegisterInstance(Guid.Parse(_accountId), this);
            
            // Create isolated directories for this account
            _cacheDir = Path.Combine("data", "accounts", _accountId, "cache");
            _logDir = Path.Combine("data", "accounts", _accountId, "logs");
            
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(_logDir);

            _client = new GridClient();
            InitializeClient();
            RegisterClientEvents();
            
            // Start display name refresh timer (similar to Radegast's approach)
            _displayNameRefreshTimer = new System.Threading.Timer(
                RefreshNearbyDisplayNames, 
                null, 
                5000, // Initial delay - 5 seconds
                30000 // Refresh every 30 seconds
            );
            
            // Load groups cache when instance is created and populate the local groups dictionary
            _ = Task.Run(async () =>
            {
                try
                {
                    await _groupService.LoadGroupsCacheAsync(Guid.Parse(_accountId));
                    
                    // Load cached groups into our local dictionary for immediate availability
                    var cachedGroups = await _groupService.GetCachedGroupsAsync(Guid.Parse(_accountId));
                    foreach (var group in cachedGroups.Values)
                    {
                        _groups.TryAdd(group.ID, group);
                    }
                    
                    _logger.LogInformation("Groups cache loaded for account {AccountId}, populated {Count} cached groups", 
                        _accountId, cachedGroups.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error loading groups cache for account {AccountId}", _accountId);
                }
            });
        }

        private void InitializeClient()
        {
            _client.Settings.MULTIPLE_SIMS = false;
            _client.Settings.USE_INTERPOLATION_TIMER = false;
            _client.Settings.ALWAYS_REQUEST_OBJECTS = true;
            _client.Settings.ALWAYS_DECODE_OBJECTS = true;
            _client.Settings.OBJECT_TRACKING = true;
            _client.Settings.ENABLE_SIMSTATS = true;
            _client.Settings.SEND_AGENT_THROTTLE = true;
            _client.Settings.SEND_AGENT_UPDATES = true;
            _client.Settings.STORE_LAND_PATCHES = true;

            _client.Settings.USE_ASSET_CACHE = true;
            _client.Settings.ASSET_CACHE_DIR = _cacheDir;
            _client.Assets.Cache.AutoPruneEnabled = false;

            _client.Throttle.Total = 5000000f;
            _client.Settings.THROTTLE_OUTGOING_PACKETS = false;
            _client.Settings.LOGIN_TIMEOUT = 120 * 1000;
            _client.Settings.SIMULATOR_TIMEOUT = 180 * 1000;
            _client.Settings.MAX_CONCURRENT_TEXTURE_DOWNLOADS = 20;

            _client.Self.Movement.AutoResetControls = false;
            _client.Self.Movement.UpdateInterval = 250;
        }

        private void RegisterClientEvents()
        {
            _client.Network.LoginProgress += Network_LoginProgress;
            _client.Network.Disconnected += Network_Disconnected;
            _client.Network.LoggedOut += Network_LoggedOut;
            _client.Self.ChatFromSimulator += Self_ChatFromSimulator;
            _client.Self.IM += Self_IM;
            _client.Self.TeleportProgress += Self_TeleportProgress;
            _client.Self.AlertMessage += Self_AlertMessage;
            _client.Network.SimChanged += Network_SimChanged;
            _client.Objects.AvatarUpdate += Objects_AvatarUpdate;
            _client.Objects.KillObject += Objects_KillObject;
            _client.Objects.AvatarSitChanged += Objects_AvatarSitChanged;
            _client.Grid.CoarseLocationUpdate += Grid_CoarseLocationUpdate;
            _client.Groups.CurrentGroups += Groups_CurrentGroups;
            _client.Self.GroupChatJoined += Self_GroupChatJoined;
            
            // Register display name events for automatic cache updates
            _client.Avatars.UUIDNameReply += Avatars_UUIDNameReply;
            _client.Avatars.DisplayNameUpdate += Avatars_DisplayNameUpdate;
            
            // Register additional events that can provide name information
            _client.Directory.DirPeopleReply += Directory_DirPeopleReply;
            _client.Avatars.AvatarPickerReply += Avatars_AvatarPickerReply;
            _client.Groups.GroupNamesReply += Groups_GroupNamesReply;
            
            // Subscribe to display name changes from our service
            _displayNameService.DisplayNameChanged += DisplayNameService_DisplayNameChanged;
            
            // Subscribe to notice events
            _noticeService.NoticeReceived += NoticeService_NoticeReceived;
        }

        private void UnregisterClientEvents()
        {
            _client.Network.LoginProgress -= Network_LoginProgress;
            _client.Network.Disconnected -= Network_Disconnected;
            _client.Network.LoggedOut -= Network_LoggedOut;
            _client.Self.ChatFromSimulator -= Self_ChatFromSimulator;
            _client.Self.IM -= Self_IM;
            _client.Self.TeleportProgress -= Self_TeleportProgress;
            _client.Self.AlertMessage -= Self_AlertMessage;
            _client.Network.SimChanged -= Network_SimChanged;
            _client.Objects.AvatarUpdate -= Objects_AvatarUpdate;
            _client.Objects.KillObject -= Objects_KillObject;
            _client.Objects.AvatarSitChanged -= Objects_AvatarSitChanged;
            _client.Grid.CoarseLocationUpdate -= Grid_CoarseLocationUpdate;
            _client.Groups.CurrentGroups -= Groups_CurrentGroups;
            _client.Self.GroupChatJoined -= Self_GroupChatJoined;
            _client.Avatars.UUIDNameReply -= Avatars_UUIDNameReply;
            _client.Avatars.DisplayNameUpdate -= Avatars_DisplayNameUpdate;
            
            // Unregister additional name events
            _client.Directory.DirPeopleReply -= Directory_DirPeopleReply;
            _client.Avatars.AvatarPickerReply -= Avatars_AvatarPickerReply;
            _client.Groups.GroupNamesReply -= Groups_GroupNamesReply;
            
            // Unsubscribe from display name changes
            _displayNameService.DisplayNameChanged -= DisplayNameService_DisplayNameChanged;
            
            // Unsubscribe from notice events
            _noticeService.NoticeReceived -= NoticeService_NoticeReceived;
        }

        public async Task<bool> LoginAsync()
        {
            try
            {
                UpdateStatus("Connecting...");
                
                var loginParams = _client.Network.DefaultLoginParams(
                    AccountInfo.FirstName, 
                    AccountInfo.LastName, 
                    AccountInfo.Password, 
                    "RadegastWeb", 
                    "1.0.0");

                if (!string.IsNullOrEmpty(AccountInfo.GridUrl))
                {
                    loginParams.URI = AccountInfo.GridUrl;
                }

                _logger.LogInformation("Attempting login for {FirstName} {LastName}", 
                    AccountInfo.FirstName, AccountInfo.LastName);

                var loginResult = await Task.Run(() => _client.Network.Login(loginParams));
                
                if (loginResult)
                {
                    AccountInfo.IsConnected = true;
                    AccountInfo.LastLoginAt = DateTime.UtcNow;
                    AccountInfo.AvatarUuid = _client.Self.AgentID.ToString();
                    
                    // Update account's own display name with actual SL display name
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Wait a moment for the client to fully initialize
                            await Task.Delay(3000);
                            
                            // Get our own display name from the display name service
                            var ownDisplayName = await _displayNameService.GetDisplayNameAsync(
                                Guid.Parse(_accountId), 
                                _client.Self.AgentID.ToString(), 
                                NameDisplayMode.Smart, 
                                $"{AccountInfo.FirstName} {AccountInfo.LastName}");
                            
                            // Update the account's display name if it's different from the legacy format
                            var legacyName = $"{AccountInfo.FirstName} {AccountInfo.LastName}";
                            if (!string.IsNullOrEmpty(ownDisplayName) && ownDisplayName != legacyName && ownDisplayName != "Loading...")
                            {
                                AccountInfo.DisplayName = ownDisplayName;
                                _logger.LogInformation("Updated account display name to: {DisplayName}", ownDisplayName);
                                
                                // Trigger a status update to refresh the UI
                                UpdateStatus(AccountInfo.Status);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to update account's own display name");
                        }
                    });
                    
                    UpdateStatus("Connected");
                    ConnectionChanged?.Invoke(this, true);
                    return true;
                }
                else
                {
                    var errorMessage = _client.Network.LoginMessage ?? "Unknown login error";
                    _logger.LogError("Login failed for {FirstName} {LastName}: {Error}", 
                        AccountInfo.FirstName, AccountInfo.LastName, errorMessage);
                    UpdateStatus($"Login failed: {errorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during login for {FirstName} {LastName}", 
                    AccountInfo.FirstName, AccountInfo.LastName);
                UpdateStatus($"Login error: {ex.Message}");
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                UpdateStatus("Disconnecting...");
                await Task.Run(() => _client.Network.Logout());
                AccountInfo.IsConnected = false;
                UpdateStatus("Disconnected");
                ConnectionChanged?.Invoke(this, false);
                _nearbyAvatars.Clear();
                _chatSessions.Clear();
                _groups.Clear();
                
                // Clean up display name service resources
                _displayNameService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up notice service resources
                _noticeService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up group service resources
                _groupService.CleanupAccount(Guid.Parse(_accountId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during disconnect for account {AccountId}", _accountId);
            }
        }

        public void SendChat(string message, ChatType chatType = ChatType.Normal, int channel = 0)
        {
            if (!_client.Network.Connected)
            {
                _logger.LogWarning("Attempted to send chat while not connected");
                return;
            }

            try
            {
                _client.Self.Chat(message, channel, chatType);
                
                // Log our own message
                var chatMessage = new ChatMessageDto
                {
                    AccountId = Guid.Parse(_accountId),
                    SenderName = !string.IsNullOrEmpty(AccountInfo.DisplayName) && AccountInfo.DisplayName != $"{AccountInfo.FirstName} {AccountInfo.LastName}"
                        ? AccountInfo.DisplayName 
                        : $"{AccountInfo.FirstName} {AccountInfo.LastName}",
                    Message = message,
                    ChatType = chatType.ToString(),
                    Channel = channel.ToString(),
                    Timestamp = DateTime.UtcNow,
                    RegionName = _client.Network.CurrentSim?.Name,
                    SenderId = _client.Self.AgentID.ToString(),
                    SessionId = "local-chat"
                };
                
                ChatReceived?.Invoke(this, chatMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending chat message");
            }
        }

        public async void SendIM(string targetId, string message)
        {
            if (!_client.Network.Connected)
            {
                _logger.LogWarning("Attempted to send IM while not connected");
                return;
            }

            try
            {
                if (UUID.TryParse(targetId, out UUID targetUUID))
                {
                    _client.Self.InstantMessage(targetUUID, message);
                    
                    // Create session if it doesn't exist
                    var sessionId = $"im-{targetId}";
                    if (!_chatSessions.ContainsKey(sessionId))
                    {
                        // Get display name for the target using global cache first
                        var targetDisplayName = await _globalDisplayNameCache.GetDisplayNameAsync(targetId, NameDisplayMode.Smart);
                        if (targetDisplayName == "Loading..." || string.IsNullOrEmpty(targetDisplayName))
                        {
                            // Fall back to account-specific service
                            targetDisplayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), targetId);
                        }
                        
                        var session = new ChatSessionDto
                        {
                            SessionId = sessionId,
                            SessionName = targetDisplayName, // Use resolved display name
                            ChatType = "IM",
                            TargetId = targetId,
                            LastActivity = DateTime.UtcNow,
                            AccountId = Guid.Parse(_accountId),
                            IsActive = true
                        };
                        _chatSessions.TryAdd(sessionId, session);
                        ChatSessionUpdated?.Invoke(this, session);
                    }

                    // Log our own IM
                    var chatMessage = new ChatMessageDto
                    {
                        AccountId = Guid.Parse(_accountId),
                        SenderName = !string.IsNullOrEmpty(AccountInfo.DisplayName) && AccountInfo.DisplayName != $"{AccountInfo.FirstName} {AccountInfo.LastName}"
                            ? AccountInfo.DisplayName 
                            : $"{AccountInfo.FirstName} {AccountInfo.LastName}",
                        Message = message,
                        ChatType = "IM",
                        Channel = "IM",
                        Timestamp = DateTime.UtcNow,
                        SenderId = _client.Self.AgentID.ToString(),
                        TargetId = targetId,
                        SessionId = sessionId
                    };
                    
                    ChatReceived?.Invoke(this, chatMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending IM message");
            }
        }

        /// <summary>
        /// Join a group chat session on-demand (similar to Radegast behavior)
        /// </summary>
        /// <param name="groupId">The group UUID</param>
        /// <returns>True if join was requested, false if already joined or invalid group</returns>
        public bool RequestJoinGroupChatIfNeeded(UUID groupId)
        {
            if (!_client.Network.Connected)
            {
                _logger.LogWarning("Cannot join group chat - not connected");
                return false;
            }

            if (!_groups.ContainsKey(groupId))
            {
                _logger.LogWarning("Cannot join group chat - not a member of group {GroupId}", groupId);
                return false;
            }

            if (!_client.Self.GroupChatSessions.ContainsKey(groupId))
            {
                _logger.LogInformation("Joining group chat for {GroupName} ({GroupId})", _groups[groupId].Name, groupId);
                _client.Self.RequestJoinGroupChat(groupId);
                return true;
            }

            return false; // Already joined
        }
        
        /// <summary>
        /// Attempt to join a group chat session even if we don't have group info cached yet
        /// This is used when we receive group messages from unknown groups
        /// </summary>
        /// <param name="groupId">The group UUID</param>
        /// <returns>True if join was requested</returns>
        public bool TryJoinUnknownGroupChat(UUID groupId)
        {
            if (!_client.Network.Connected)
            {
                return false;
            }

            if (!_client.Self.GroupChatSessions.ContainsKey(groupId))
            {
                _logger.LogInformation("Attempting to join unknown group chat session {GroupId}", groupId);
                _client.Self.RequestJoinGroupChat(groupId);
                return true;
            }

            return false; // Already joined
        }

        public void SendGroupIM(string groupId, string message)
        {
            if (!_client.Network.Connected)
            {
                _logger.LogWarning("Attempted to send group IM while not connected");
                return;
            }

            try
            {
                if (UUID.TryParse(groupId, out UUID groupUUID))
                {
                    // Ensure we're in the group chat session (on-demand joining)
                    RequestJoinGroupChatIfNeeded(groupUUID);

                    _client.Self.InstantMessageGroup(groupUUID, message);
                    
                    // Create session if it doesn't exist
                    var sessionId = $"group-{groupId}";
                    if (!_chatSessions.ContainsKey(sessionId))
                    {
                        string groupName = "Unknown Group";
                        if (_groups.TryGetValue(groupUUID, out var group))
                        {
                            groupName = group.Name;
                        }

                        var session = new ChatSessionDto
                        {
                            SessionId = sessionId,
                            SessionName = groupName,
                            ChatType = "Group",
                            TargetId = groupId,
                            LastActivity = DateTime.UtcNow,
                            AccountId = Guid.Parse(_accountId),
                            IsActive = true
                        };
                        _chatSessions.TryAdd(sessionId, session);
                        ChatSessionUpdated?.Invoke(this, session);
                    }

                    // Log our own group message
                    var chatMessage = new ChatMessageDto
                    {
                        AccountId = Guid.Parse(_accountId),
                        SenderName = !string.IsNullOrEmpty(AccountInfo.DisplayName) && AccountInfo.DisplayName != $"{AccountInfo.FirstName} {AccountInfo.LastName}"
                            ? AccountInfo.DisplayName 
                            : $"{AccountInfo.FirstName} {AccountInfo.LastName}",
                        Message = message,
                        ChatType = "Group",
                        Channel = "Group",
                        Timestamp = DateTime.UtcNow,
                        SenderId = _client.Self.AgentID.ToString(),
                        TargetId = groupId,
                        SessionId = sessionId
                    };
                    
                    ChatReceived?.Invoke(this, chatMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending group IM message");
            }
        }

        public async Task<IEnumerable<AvatarDto>> GetNearbyAvatarsAsync()
        {
            // First, proactively ensure all nearby avatar display names are loading
            var allAvatarIds = new List<string>();
            
            // Collect IDs from both detailed and coarse location avatars
            allAvatarIds.AddRange(_nearbyAvatars.Keys.Select(id => id.ToString()));
            allAvatarIds.AddRange(_coarseLocationAvatars.Keys.Select(id => id.ToString()));
            
            if (allAvatarIds.Count > 0)
            {
                // Fire and forget - start loading names in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), allAvatarIds.Distinct().ToList());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error preloading display names in GetNearbyAvatarsAsync");
                    }
                });
            }

            var avatarTasks = new List<Task<AvatarDto>>();
            var processedIds = new HashSet<UUID>();

            // Process detailed avatars first (they have priority)
            foreach (var avatar in _nearbyAvatars.Values)
            {
                processedIds.Add(avatar.ID);
                avatarTasks.Add(CreateAvatarDtoAsync(avatar));
            }

            // Process coarse location avatars that don't have detailed info
            foreach (var coarseAvatar in _coarseLocationAvatars.Values)
            {
                if (!processedIds.Contains(coarseAvatar.ID))
                {
                    avatarTasks.Add(CreateAvatarDtoFromCoarseAsync(coarseAvatar));
                }
            }

            var avatars = await Task.WhenAll(avatarTasks);
            return avatars.OrderBy(a => a.Distance);
        }

        public IEnumerable<AvatarDto> GetNearbyAvatars()
        {
            var processedIds = new HashSet<UUID>();
            var avatars = new List<AvatarDto>();

            // Process detailed avatars first (they have priority)
            foreach (var avatar in _nearbyAvatars.Values)
            {
                processedIds.Add(avatar.ID);
                avatars.Add(CreateAvatarDto(avatar));
            }

            // Process coarse location avatars that don't have detailed info
            foreach (var coarseAvatar in _coarseLocationAvatars.Values)
            {
                if (!processedIds.Contains(coarseAvatar.ID))
                {
                    avatars.Add(CreateAvatarDtoFromCoarse(coarseAvatar));
                }
            }

            return avatars.OrderBy(a => a.Distance);
        }

        private async Task<AvatarDto> CreateAvatarDtoAsync(Avatar avatar)
        {
            var avatarName = avatar.Name ?? "Unknown";
            var displayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), avatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
            var legacyName = await _displayNameService.GetLegacyNameAsync(Guid.Parse(_accountId), avatar.ID.ToString(), avatarName);
            
            return new AvatarDto
            {
                Id = avatar.ID.ToString(),
                Name = legacyName,
                DisplayName = displayName,
                Distance = CalculateHorizontalDistance(_client.Self.SimPosition, avatar.Position),
                Status = "Online", // TODO: Get actual status if available
                AccountId = Guid.Parse(_accountId)
            };
        }

        private AvatarDto CreateAvatarDto(Avatar avatar)
        {
            var avatarName = avatar.Name ?? "Unknown";
            string displayName = avatarName;
            
            // Try to get display name from global cache synchronously
            try
            {
                // Check if we have the display name in global cache
                var cachedName = _globalDisplayNameCache.GetCachedDisplayName(avatar.ID.ToString());
                if (!string.IsNullOrEmpty(cachedName) && cachedName != "Loading...")
                {
                    displayName = cachedName;
                }
                // If not in cache, use the avatar name and trigger async loading for next time
                else
                {
                    // Fire and forget - load for next time
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _globalDisplayNameCache.GetDisplayNameAsync(avatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Background display name load failed for {AvatarId}", avatar.ID);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error accessing global display name cache for {AvatarId}", avatar.ID);
            }

            return new AvatarDto
            {
                Id = avatar.ID.ToString(),
                Name = avatarName,
                DisplayName = displayName,
                Distance = CalculateHorizontalDistance(_client.Self.SimPosition, avatar.Position),
                Status = "Online", // TODO: Get actual status if available
                AccountId = Guid.Parse(_accountId)
            };
        }

        private async Task<AvatarDto> CreateAvatarDtoFromCoarseAsync(CoarseLocationAvatar coarseAvatar)
        {
            var avatarName = coarseAvatar.Name;
            if (string.IsNullOrEmpty(avatarName) || avatarName == "Loading...")
            {
                avatarName = "Loading...";
                // Try to get the name asynchronously
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), coarseAvatar.ID.ToString(), NameDisplayMode.Smart, "");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error loading name for coarse avatar {AvatarId}", coarseAvatar.ID);
                    }
                });
            }

            var displayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), coarseAvatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
            var legacyName = await _displayNameService.GetLegacyNameAsync(Guid.Parse(_accountId), coarseAvatar.ID.ToString(), avatarName);
            
            return new AvatarDto
            {
                Id = coarseAvatar.ID.ToString(),
                Name = legacyName,
                DisplayName = displayName,
                Distance = CalculateHorizontalDistance(_client.Self.SimPosition, coarseAvatar.Position),
                Status = "Online", // Coarse location avatars are assumed online
                AccountId = Guid.Parse(_accountId)
            };
        }

        private AvatarDto CreateAvatarDtoFromCoarse(CoarseLocationAvatar coarseAvatar)
        {
            var avatarName = coarseAvatar.Name;
            if (string.IsNullOrEmpty(avatarName) || avatarName == "Loading...")
            {
                avatarName = "Loading...";
                // Request name lookup
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), coarseAvatar.ID.ToString(), NameDisplayMode.Smart, "");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error loading name for coarse avatar {AvatarId}", coarseAvatar.ID);
                    }
                });
            }

            // Try to get display name from global cache
            var displayName = avatarName;
            try
            {
                var cachedName = _globalDisplayNameCache.GetCachedDisplayName(coarseAvatar.ID.ToString());
                if (!string.IsNullOrEmpty(cachedName) && cachedName != "Loading...")
                {
                    displayName = cachedName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error accessing global display name cache for coarse avatar {AvatarId}", coarseAvatar.ID);
            }

            return new AvatarDto
            {
                Id = coarseAvatar.ID.ToString(),
                Name = avatarName,
                DisplayName = displayName,
                Distance = CalculateHorizontalDistance(_client.Self.SimPosition, coarseAvatar.Position),
                Status = "Online", // Coarse location avatars are assumed online
                AccountId = Guid.Parse(_accountId)
            };
        }

        public IEnumerable<ChatSessionDto> GetChatSessions()
        {
            return _chatSessions.Values.OrderByDescending(s => s.LastActivity);
        }

        /// <summary>
        /// Get all groups for this account
        /// </summary>
        public async Task<IEnumerable<GroupDto>> GetGroupsAsync()
        {
            try
            {
                return await _groupService.GetGroupsAsync(Guid.Parse(_accountId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting groups for account {AccountId}", _accountId);
                return Enumerable.Empty<GroupDto>();
            }
        }

        /// <summary>
        /// Get a specific group by ID
        /// </summary>
        public async Task<GroupDto?> GetGroupAsync(string groupId)
        {
            try
            {
                return await _groupService.GetGroupAsync(Guid.Parse(_accountId), groupId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting group {GroupId} for account {AccountId}", groupId, _accountId);
                return null;
            }
        }

        /// <summary>
        /// Get group name by ID (with caching)
        /// </summary>
        public async Task<string> GetGroupNameAsync(string groupId, string fallbackName = "Unknown Group")
        {
            try
            {
                return await _groupService.GetGroupNameAsync(Guid.Parse(_accountId), groupId, fallbackName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting group name for {GroupId} on account {AccountId}", groupId, _accountId);
                return fallbackName;
            }
        }

        public void TriggerStatusUpdate()
        {
            UpdateStatus(Status);
        }

        /// <summary>
        /// Manually refresh display names for all nearby avatars
        /// Useful for immediate updates when requested by the UI
        /// </summary>
        public async Task RefreshNearbyAvatarDisplayNamesAsync()
        {
            try
            {
                if (!_client.Network.Connected || _nearbyAvatars.Count == 0)
                {
                    _logger.LogDebug("Cannot refresh display names - not connected or no nearby avatars");
                    return;
                }

                var avatarIds = _nearbyAvatars.Keys.Select(id => id.ToString()).ToList();
                await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), avatarIds);
                
                _logger.LogInformation("Manually refreshed display names for {Count} nearby avatars", avatarIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error manually refreshing nearby avatar display names for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Get radar statistics for debugging and monitoring
        /// </summary>
        public RadarStatsDto GetRadarStats()
        {
            return new RadarStatsDto
            {
                DetailedAvatarCount = _nearbyAvatars.Count,
                CoarseLocationAvatarCount = _coarseLocationAvatars.Count,
                TotalUniqueAvatars = GetUniqueAvatarCount(),
                MaxDetectionRange = MAX_DISTANCE,
                SimAvatarCount = _client.Network.CurrentSim?.AvatarPositions.Count ?? 0
            };
        }

        private int GetUniqueAvatarCount()
        {
            var uniqueIds = new HashSet<UUID>();
            foreach (var id in _nearbyAvatars.Keys)
                uniqueIds.Add(id);
            foreach (var id in _coarseLocationAvatars.Keys)
                uniqueIds.Add(id);
            return uniqueIds.Count;
        }

        /// <summary>
        /// Get a count of nearby avatars with cached display names vs. those still loading
        /// Useful for UI to show loading status
        /// </summary>
        public async Task<(int Total, int Cached, int Loading)> GetDisplayNameLoadingStatusAsync()
        {
            try
            {
                var total = _nearbyAvatars.Count;
                var cached = 0;
                
                var cachedNames = await _displayNameService.GetCachedNamesAsync(Guid.Parse(_accountId));
                var cachedIds = cachedNames.Select(n => n.AvatarId).ToHashSet();
                
                foreach (var avatarId in _nearbyAvatars.Keys)
                {
                    if (cachedIds.Contains(avatarId.ToString()))
                    {
                        cached++;
                    }
                }
                
                return (total, cached, total - cached);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting display name loading status for account {AccountId}", _accountId);
                return (_nearbyAvatars.Count, 0, _nearbyAvatars.Count);
            }
        }

        private void UpdateStatus(string status)
        {
            Status = status;
            AccountInfo.Status = status;
            StatusChanged?.Invoke(this, status);
            _logger.LogInformation("Account {AccountId} status: {Status}", _accountId, status);
        }

        #region Event Handlers

        private void Network_LoginProgress(object? sender, LoginProgressEventArgs e)
        {
            UpdateStatus($"Login: {e.Status}");
            if (e.Status == LoginStatus.Success)
            {
                AccountInfo.CurrentRegion = _client.Network.CurrentSim?.Name;
                UpdateStatus($"Login successful");
                UpdateRegionInfo();
                
                // Register this client with the global display name cache
                _globalDisplayNameCache.RegisterGridClient(Guid.Parse(_accountId), _client);
                _logger.LogDebug("Registered grid client {AccountId} with global display name cache", _accountId);
                
                // Request current groups after successful login
                _client.Groups.RequestCurrentGroups();
                
                // Start periodic display name refresh timer (every 5 minutes)
                _displayNameRefreshTimer?.Dispose();
                _displayNameRefreshTimer = new System.Threading.Timer(PeriodicDisplayNameRefresh, null, 
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
                
                // Start proactive display name loading after successful login
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Wait a bit for initial avatar updates to populate
                        await Task.Delay(2000);
                        
                        // Request our own display name first
                        if (_client.Network.Connected)
                        {
                            await _globalDisplayNameCache.RequestDisplayNamesAsync(
                                new List<string> { _client.Self.AgentID.ToString() }, 
                                Guid.Parse(_accountId));
                        }
                        
                        // Preload any avatars that appeared during login
                        if (_nearbyAvatars.Count > 0)
                        {
                            var avatarIds = _nearbyAvatars.Keys.Select(id => id.ToString()).ToList();
                            await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), avatarIds);
                            _logger.LogInformation("Preloaded display names for {Count} avatars after login", avatarIds.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error preloading display names after login for account {AccountId}", _accountId);
                    }
                });
            }
        }

        private async void PeriodicDisplayNameRefresh(object? state)
        {
            try
            {
                if (!_client.Network.Connected || _nearbyAvatars.Count == 0)
                    return;

                var avatarIds = _nearbyAvatars.Keys.Select(id => id.ToString()).ToList();
                
                // Refresh display names for all nearby avatars
                // This ensures we pick up any display name changes
                await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), avatarIds);
                
                _logger.LogDebug("Periodic refresh of {Count} nearby avatar display names completed", avatarIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in periodic display name refresh for account {AccountId}", _accountId);
            }
        }

        private void Network_Disconnected(object? sender, DisconnectedEventArgs e)
        {
            AccountInfo.IsConnected = false;
            UpdateStatus($"Disconnected: {e.Reason}");
            ConnectionChanged?.Invoke(this, false);
            
            // Unregister from global display name cache
            _globalDisplayNameCache.UnregisterGridClient(Guid.Parse(_accountId));
            _logger.LogDebug("Unregistered grid client {AccountId} from global display name cache", _accountId);
            
            _nearbyAvatars.Clear();
            _chatSessions.Clear();
            _groups.Clear();
        }

        private void Network_LoggedOut(object? sender, LoggedOutEventArgs e)
        {
            AccountInfo.IsConnected = false;
            UpdateStatus("Logged out");
            ConnectionChanged?.Invoke(this, false);
            
            // Unregister from global display name cache
            _globalDisplayNameCache.UnregisterGridClient(Guid.Parse(_accountId));
            _logger.LogDebug("Unregistered grid client {AccountId} from global display name cache (logout)", _accountId);
            
            _nearbyAvatars.Clear();
            _chatSessions.Clear();
            _groups.Clear();
        }

        private void Network_SimChanged(object? sender, SimChangedEventArgs e)
        {
            AccountInfo.CurrentRegion = e.PreviousSimulator?.Name;
            UpdateRegionInfo();
            _nearbyAvatars.Clear(); // Clear avatars from previous sim
            _coarseLocationAvatars.Clear(); // Clear coarse location avatars from previous sim
            _avatarSimHandles.Clear(); // Clear sim handles
            
            // Proactively start loading display names for the new region
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait for avatars to start appearing in the new sim
                    await Task.Delay(3000);
                    
                    // Preload any avatars that have appeared in the new region
                    var totalAvatars = _nearbyAvatars.Count + _coarseLocationAvatars.Count;
                    if (totalAvatars > 0)
                    {
                        var avatarIds = new List<string>();
                        avatarIds.AddRange(_nearbyAvatars.Keys.Select(id => id.ToString()));
                        avatarIds.AddRange(_coarseLocationAvatars.Keys.Select(id => id.ToString()));
                        
                        await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), avatarIds.Distinct().ToList());
                        _logger.LogInformation("Preloaded display names for {Count} avatars after sim change", avatarIds.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error preloading display names after sim change for account {AccountId}", _accountId);
                }
            });
        }

        private void UpdateRegionInfo()
        {
            if (_client.Network.CurrentSim != null)
            {
                // Use the total avatar count from the sim, which includes all avatars
                // regardless of draw distance, similar to how Radegast's radar works
                var totalAvatarCount = _client.Network.CurrentSim.AvatarPositions.Count;
                
                var regionInfo = new RegionInfoDto
                {
                    Name = _client.Network.CurrentSim.Name,
                    AvatarCount = totalAvatarCount, // This includes self and all other avatars
                    AccountId = Guid.Parse(_accountId),
                    RegionX = _client.Network.CurrentSim.Handle >> 32,
                    RegionY = _client.Network.CurrentSim.Handle & 0xFFFFFFFF
                };

                RegionChanged?.Invoke(this, regionInfo);
            }
        }

        private async void Objects_AvatarUpdate(object? sender, AvatarUpdateEventArgs e)
        {
            if (e.Avatar.ID == _client.Self.AgentID) return; // Skip self

            var isNewAvatar = !_nearbyAvatars.ContainsKey(e.Avatar.ID);
            _nearbyAvatars.AddOrUpdate(e.Avatar.ID, e.Avatar, (key, oldValue) => e.Avatar);

            // Mark corresponding coarse location avatar as detailed if it exists
            if (_coarseLocationAvatars.TryGetValue(e.Avatar.ID, out var coarseAvatar))
            {
                coarseAvatar.IsDetailed = true;
                coarseAvatar.Position = e.Avatar.Position; // Update position from detailed data
                coarseAvatar.LastUpdate = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(e.Avatar.Name))
                {
                    coarseAvatar.Name = e.Avatar.Name;
                }
            }

            // Get display name for the avatar with fallback to avatar name
            // Try global cache first, then fall back to account-specific service
            var avatarName = e.Avatar.Name ?? "Unknown";
            var displayName = avatarName;
            
            try
            {
                displayName = await _globalDisplayNameCache.GetDisplayNameAsync(e.Avatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
                if (displayName == "Loading..." || string.IsNullOrEmpty(displayName))
                {
                    // Fall back to account-specific service
                    displayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), e.Avatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
                    
                    // If we still don't have a good name, proactively request it
                    if (displayName == "Loading..." || string.IsNullOrEmpty(displayName) || displayName == avatarName)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Request display name refresh for this avatar
                                await _displayNameService.RefreshDisplayNameAsync(Guid.Parse(_accountId), e.Avatar.ID.ToString());
                                
                                // Also trigger a legacy name request as fallback
                                if (_client.Network.Connected)
                                {
                                    _client.Avatars.RequestAvatarNames(new List<UUID> { e.Avatar.ID });
                                }
                            }
                            catch (Exception refreshEx)
                            {
                                _logger.LogDebug(refreshEx, "Error requesting name refresh for avatar {AvatarId}", e.Avatar.ID);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting display name for avatar {AvatarId}, using fallback", e.Avatar.ID);
                displayName = avatarName;
            }

            var avatarDto = new AvatarDto
            {
                Id = e.Avatar.ID.ToString(),
                Name = avatarName,
                DisplayName = displayName,
                Distance = CalculateHorizontalDistance(_client.Self.SimPosition, e.Avatar.Position),
                Status = "Online",
                AccountId = Guid.Parse(_accountId)
            };

            AvatarAdded?.Invoke(this, avatarDto);
            UpdateRegionInfo();
            
            // Proactive display name loading strategies
            if (isNewAvatar)
            {
                // Strategy 1: Every time a new avatar appears, preload names for a batch of nearby avatars
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Get all current avatar IDs from both detailed and coarse location avatars
                        var avatarIds = new List<string>();
                        avatarIds.AddRange(_nearbyAvatars.Keys.Select(id => id.ToString()));
                        avatarIds.AddRange(_coarseLocationAvatars.Keys.Select(id => id.ToString()));
                        var currentAvatarIds = avatarIds.Distinct().ToList();
                        
                        // Use global cache for preloading - more efficient across accounts
                        await _globalDisplayNameCache.PreloadDisplayNamesAsync(currentAvatarIds);
                        
                        _logger.LogDebug("Preloaded display names for {Count} nearby avatars on new avatar arrival", currentAvatarIds.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error preloading display names on avatar arrival for account {AccountId}", _accountId);
                    }
                });
            }
            
            // Strategy 2: Periodic bulk preloading for larger groups (every 5 avatars)
            var totalAvatarCount = _nearbyAvatars.Count + _coarseLocationAvatars.Count;
            if (totalAvatarCount % 5 == 0 && totalAvatarCount > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var avatarIds = new List<string>();
                        avatarIds.AddRange(_nearbyAvatars.Keys.Select(id => id.ToString()));
                        avatarIds.AddRange(_coarseLocationAvatars.Keys.Select(id => id.ToString()));
                        var allAvatarIds = avatarIds.Distinct().ToList();
                        
                        await _globalDisplayNameCache.PreloadDisplayNamesAsync(allAvatarIds);
                        
                        _logger.LogDebug("Bulk preloaded display names for {Count} nearby avatars (periodic)", allAvatarIds.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in bulk display name preload for account {AccountId}", _accountId);
                    }
                });
            }
        }

        private void Objects_KillObject(object? sender, KillObjectEventArgs e)
        {
            // Find avatar by LocalID and remove by UUID
            var avatarToRemove = _nearbyAvatars.Values.FirstOrDefault(a => a.LocalID == e.ObjectLocalID);
            if (avatarToRemove != null && _nearbyAvatars.TryRemove(avatarToRemove.ID, out var removedAvatar))
            {
                // Also remove from coarse location tracking if it exists
                _coarseLocationAvatars.TryRemove(avatarToRemove.ID, out _);
                _avatarSimHandles.TryRemove(avatarToRemove.ID, out _);
                
                AvatarRemoved?.Invoke(this, removedAvatar.ID.ToString());
                UpdateRegionInfo();
            }
        }

        private void Objects_AvatarSitChanged(object? sender, AvatarSitChangedEventArgs e)
        {
            // Only track sitting changes for this account's avatar
            if (e.Avatar.LocalID != _client.Self.LocalID) return;

            var isSitting = e.SittingOn != 0;
            var objectId = isSitting ? GetObjectUUIDFromLocalID(e.SittingOn) : null;

            var eventArgs = new SitStateChangedEventArgs(isSitting, objectId?.ToString(), e.SittingOn);
            SitStateChanged?.Invoke(this, eventArgs);

            _logger.LogInformation("Avatar sitting state changed for account {AccountId}: {IsSitting} on {ObjectId}", 
                _accountId, isSitting, objectId?.ToString() ?? "ground");
        }

        /// <summary>
        /// Helper method to get object UUID from local ID
        /// </summary>
        private UUID? GetObjectUUIDFromLocalID(uint localId)
        {
            if (!_client.Network.Connected || _client.Network.CurrentSim == null)
                return null;

            var prim = _client.Network.CurrentSim.ObjectsPrimitives.Values.FirstOrDefault(p => p.LocalID == localId);
            return prim?.ID;
        }

        private void Grid_CoarseLocationUpdate(object? sender, CoarseLocationUpdateEventArgs e)
        {
            // This gives us a more complete list of all avatars in the sim, 
            // not just those within draw distance that trigger AvatarUpdate
            if (e.Simulator != _client.Network.CurrentSim)
            {
                // Handle cross-sim avatars if within range
                return;
            }

            try
            {
                // Get our current position for distance calculations
                var agentPosition = e.Simulator.AvatarPositions.TryGetValue(_client.Self.AgentID, out var ourPos)
                    ? ToVector3D(e.Simulator.Handle, ourPos)
                    : _client.Self.GlobalPosition;

                // Handle removed avatars
                var removedAvatars = new List<UUID>();
                foreach (var removedId in e.RemovedEntries)
                {
                    if (_coarseLocationAvatars.TryRemove(removedId, out var removed))
                    {
                        _avatarSimHandles.TryRemove(removedId, out _);
                        removedAvatars.Add(removedId);
                    }
                }

                // Process all avatar positions
                var existingAvatars = new HashSet<UUID>();
                foreach (var avatarPos in e.Simulator.AvatarPositions)
                {
                    existingAvatars.Add(avatarPos.Key);
                    
                    // Skip self
                    if (avatarPos.Key == _client.Self.AgentID)
                        continue;

                    var pos = avatarPos.Value;
                    
                    // Get detailed avatar object if available
                    var detailedAvatar = e.Simulator.ObjectsAvatars.Values.FirstOrDefault(av => av.ID == avatarPos.Key);
                    
                    // Handle altitude issues (SecondLife uses 1020f, OpenSim uses 0f for high altitudes)
                    bool unknownAltitude = _client.Settings.LOGIN_SERVER.Contains("secondlife") ? pos.Z == 1020f : pos.Z == 0f;
                    if (unknownAltitude && detailedAvatar != null)
                    {
                        if (detailedAvatar.ParentID == 0)
                        {
                            pos.Z = detailedAvatar.Position.Z;
                        }
                        else if (e.Simulator.ObjectsPrimitives.TryGetValue(detailedAvatar.ParentID, out var seatObject))
                        {
                            pos.Z = seatObject.Position.Z;
                        }
                    }

                    // Calculate distance from agent
                    var distance = Vector3d.Distance(ToVector3D(e.Simulator.Handle, pos), agentPosition);
                    
                    // Apply maximum distance filter (362m = corner to corner of sim)
                    if (distance > MAX_DISTANCE)
                        continue;

                    // Update or create coarse location avatar
                    var avatarName = detailedAvatar?.Name ?? "Loading...";
                    
                    if (_coarseLocationAvatars.TryGetValue(avatarPos.Key, out var existing))
                    {
                        existing.Position = pos;
                        existing.LastUpdate = DateTime.UtcNow;
                        existing.IsDetailed = detailedAvatar != null;
                        if (!string.IsNullOrEmpty(avatarName) && avatarName != "Loading...")
                        {
                            existing.Name = avatarName;
                        }
                    }
                    else
                    {
                        var coarseAvatar = new CoarseLocationAvatar(avatarPos.Key, pos, e.Simulator.Handle, avatarName)
                        {
                            IsDetailed = detailedAvatar != null
                        };
                        _coarseLocationAvatars.TryAdd(avatarPos.Key, coarseAvatar);
                    }
                    
                    _avatarSimHandles[avatarPos.Key] = e.Simulator.Handle;
                }

                // Remove avatars that are no longer in the position update
                var toRemove = new List<UUID>();
                foreach (var kvp in _coarseLocationAvatars)
                {
                    if (_avatarSimHandles.TryGetValue(kvp.Key, out var simHandle) && 
                        simHandle == e.Simulator.Handle && 
                        !existingAvatars.Contains(kvp.Key))
                    {
                        toRemove.Add(kvp.Key);
                    }
                }

                foreach (var removeId in toRemove)
                {
                    _coarseLocationAvatars.TryRemove(removeId, out _);
                    _avatarSimHandles.TryRemove(removeId, out _);
                    removedAvatars.Add(removeId);
                }

                // Trigger avatar removed events for all removed avatars
                foreach (var removedId in removedAvatars)
                {
                    AvatarRemoved?.Invoke(this, removedId.ToString());
                }

                // Update the region info with the total avatar count
                UpdateRegionInfo();
                
                _logger.LogDebug("Coarse location update processed: {Count} avatars, {Removed} removed", 
                    _coarseLocationAvatars.Count, removedAvatars.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing coarse location update for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Converts sim handle and local position to global Vector3d
        /// </summary>
        private static Vector3d ToVector3D(ulong handle, Vector3 localPosition)
        {
            var regionX = (double)((handle >> 32) & 0xFFFF) * 256.0;
            var regionY = (double)(handle & 0xFFFF) * 256.0;
            return new Vector3d(regionX + localPosition.X, regionY + localPosition.Y, localPosition.Z);
        }

        private async void Self_ChatFromSimulator(object? sender, ChatEventArgs e)
        {
            // Filter out typing indicators and empty messages
            if (e.Type == ChatType.StartTyping || 
                e.Type == ChatType.StopTyping || 
                string.IsNullOrEmpty(e.Message))
            {
                return;
            }

            string senderDisplayName;
            
            // Check if this is object chat or avatar chat
            if (e.SourceType == ChatSourceType.Object)
            {
                // For objects, use the FromName directly - no display name lookup needed
                senderDisplayName = e.FromName;
            }
            else
            {
                // For avatars, get display name with fallback to legacy name
                // Try global cache first, then fall back to account-specific service
                senderDisplayName = await _globalDisplayNameCache.GetDisplayNameAsync(e.SourceID.ToString(), NameDisplayMode.Smart, e.FromName);
                if (senderDisplayName == "Loading..." || string.IsNullOrEmpty(senderDisplayName))
                {
                    senderDisplayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), e.SourceID.ToString(), NameDisplayMode.Smart, e.FromName);
                    
                    // If we still don't have a good name, proactively request it
                    if (senderDisplayName == "Loading..." || string.IsNullOrEmpty(senderDisplayName) || senderDisplayName == e.FromName)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Request display name refresh for this avatar
                                await _displayNameService.RefreshDisplayNameAsync(Guid.Parse(_accountId), e.SourceID.ToString());
                                
                                // Also trigger a legacy name request as fallback
                                if (_client.Network.Connected)
                                {
                                    _client.Avatars.RequestAvatarNames(new List<UUID> { e.SourceID });
                                }
                            }
                            catch (Exception refreshEx)
                            {
                                _logger.LogDebug(refreshEx, "Error requesting name refresh for chat sender {SourceId}", e.SourceID);
                            }
                        });
                    }
                }
            }
            
            // Process any SLURLs in the message
            var processedMessage = await _urlParser.ProcessChatMessageAsync(e.Message, Guid.Parse(_accountId));
            
            var chatMessage = new ChatMessageDto
            {
                AccountId = Guid.Parse(_accountId),
                SenderName = senderDisplayName,
                Message = processedMessage, // Use the processed message with URL replacements
                ChatType = e.Type.ToString(),
                Channel = "0", // Default channel for local chat
                Timestamp = DateTime.UtcNow,
                RegionName = _client.Network.CurrentSim?.Name,
                SenderId = e.SourceID.ToString(),
                SessionId = "local-chat"
            };

            ChatReceived?.Invoke(this, chatMessage);
        }

        private async void Self_IM(object? sender, InstantMessageEventArgs e)
        {
            // First, check if this is a notice that needs special handling
            if (e.IM.Dialog == InstantMessageDialog.GroupNotice ||
                e.IM.Dialog == InstantMessageDialog.GroupNoticeRequested)
            {
                await _noticeService.ProcessIncomingNoticeAsync(Guid.Parse(_accountId), e.IM);
                return; // Don't process as regular IM
            }

            // Handle region notices from Second Life system
            if (e.IM.Dialog == InstantMessageDialog.MessageFromAgent && e.IM.FromAgentName == "Second Life")
            {
                await _noticeService.ProcessIncomingNoticeAsync(Guid.Parse(_accountId), e.IM);
                return; // Don't process as regular IM
            }

            // Filter out typing indicators and other non-chat dialogs
            if (e.IM.Dialog == InstantMessageDialog.StartTyping ||
                e.IM.Dialog == InstantMessageDialog.StopTyping ||
                e.IM.Dialog == InstantMessageDialog.MessageBox ||
                e.IM.Dialog == InstantMessageDialog.RequestTeleport ||
                e.IM.Dialog == InstantMessageDialog.RequestLure ||
                e.IM.Dialog == InstantMessageDialog.GroupInvitation ||
                e.IM.Dialog == InstantMessageDialog.InventoryOffered ||
                e.IM.Dialog == InstantMessageDialog.InventoryAccepted ||
                e.IM.Dialog == InstantMessageDialog.InventoryDeclined)
            {
                // These are system notifications, not chat messages
                // TODO: Could implement proper handling for these in the future
                return;
            }

            string sessionId;
            string sessionName;
            string chatType;
            string targetId;

            // Get display name for the sender using global cache first
            var senderDisplayName = await _globalDisplayNameCache.GetDisplayNameAsync(e.IM.FromAgentID.ToString(), NameDisplayMode.Smart);
            if (senderDisplayName == "Loading..." || string.IsNullOrEmpty(senderDisplayName))
            {
                senderDisplayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), e.IM.FromAgentID.ToString(), NameDisplayMode.Smart, e.IM.FromAgentName);
                
                // If we still don't have a good name, proactively request it
                if (senderDisplayName == "Loading..." || string.IsNullOrEmpty(senderDisplayName) || senderDisplayName == e.IM.FromAgentName)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Request display name refresh for this avatar
                            await _displayNameService.RefreshDisplayNameAsync(Guid.Parse(_accountId), e.IM.FromAgentID.ToString());
                            
                            // Also trigger a legacy name request as fallback
                            if (_client.Network.Connected)
                            {
                                _client.Avatars.RequestAvatarNames(new List<UUID> { e.IM.FromAgentID });
                            }
                        }
                        catch (Exception refreshEx)
                        {
                            _logger.LogDebug(refreshEx, "Error requesting name refresh for IM sender {FromAgentId}", e.IM.FromAgentID);
                        }
                    });
                }
            }

            switch (e.IM.Dialog)
            {
                case InstantMessageDialog.SessionSend:
                    // This could be either group chat or conference IM
                    // First check persistent cache to ensure we catch all groups we're members of
                    var cachedGroupName = await _groupService.GetGroupNameAsync(Guid.Parse(_accountId), 
                        e.IM.IMSessionID.ToString(), string.Empty);
                    
                    if (!string.IsNullOrEmpty(cachedGroupName))
                    {
                        // Found in persistent cache, this is definitely a group message
                        sessionId = $"group-{e.IM.IMSessionID}";
                        sessionName = cachedGroupName;
                        chatType = "Group";
                        targetId = e.IM.IMSessionID.ToString();
                        
                        // Add to in-memory cache for faster future lookups
                        var cachedGroups = await _groupService.GetCachedGroupsAsync(Guid.Parse(_accountId));
                        if (cachedGroups.TryGetValue(e.IM.IMSessionID, out var cachedGroup))
                        {
                            _groups.TryAdd(e.IM.IMSessionID, cachedGroup);
                        }
                        
                        // Try to join the group chat if we're not already in it
                        RequestJoinGroupChatIfNeeded(e.IM.IMSessionID);
                        
                        _logger.LogInformation("Using cached group name {GroupName} for group {GroupId} (loaded from persistent cache)", 
                            cachedGroupName, e.IM.IMSessionID);
                    }
                    // Then check our in-memory groups cache for known groups
                    else if (_groups.ContainsKey(e.IM.IMSessionID))
                    {
                        // Known group chat
                        sessionId = $"group-{e.IM.IMSessionID}";
                        sessionName = _groups[e.IM.IMSessionID].Name;
                        chatType = "Group";
                        targetId = e.IM.IMSessionID.ToString();
                        
                        // Ensure we're joined to the group chat (like Radegast does)
                        RequestJoinGroupChatIfNeeded(e.IM.IMSessionID);
                    }
                    else
                    {
                        // Extract group name from binary bucket (this is the standard approach in Radegast)
                        var binaryBucketContent = System.Text.Encoding.UTF8.GetString(e.IM.BinaryBucket).Trim('\0');
                        
                        if (!string.IsNullOrWhiteSpace(binaryBucketContent))
                        {
                            // Treat as group message if we have a group name in binary bucket
                            // This is how Radegast determines group messages from unknown groups
                            if (e.IM.BinaryBucket.Length >= 2 && binaryBucketContent.Length > 2)
                            {
                                // This looks like a conference (ad-hoc friends chat)
                                sessionId = $"conference-{e.IM.IMSessionID}";
                                sessionName = binaryBucketContent;
                                chatType = "Conference";
                                targetId = e.IM.IMSessionID.ToString();
                                
                                _logger.LogInformation("Received conference message for session {SessionName} ({SessionId})", 
                                    binaryBucketContent, e.IM.IMSessionID);
                            }
                            else
                            {
                                // Single character or short binary bucket suggests group message from unknown group
                                // Request current groups to update our group list
                                _client.Groups.RequestCurrentGroups();
                                
                                // Try to join the group chat session
                                TryJoinUnknownGroupChat(e.IM.IMSessionID);
                                
                                sessionId = $"group-{e.IM.IMSessionID}";
                                sessionName = $"Group Chat ({e.IM.IMSessionID.ToString().Substring(0, 8)})";
                                chatType = "Group";
                                targetId = e.IM.IMSessionID.ToString();
                                
                                _logger.LogInformation("Received group message from unknown group ({GroupId}), requested group list update", 
                                    e.IM.IMSessionID);
                            }
                        }
                        else
                        {
                            // Default to group if we can't determine type
                            // Most SessionSend messages are group messages in SL
                            _client.Groups.RequestCurrentGroups();
                            
                            // Try to join the group chat session
                            TryJoinUnknownGroupChat(e.IM.IMSessionID);
                            
                            sessionId = $"group-{e.IM.IMSessionID}";
                            sessionName = $"Group Chat ({e.IM.IMSessionID.ToString().Substring(0, 8)})";
                            chatType = "Group";
                            targetId = e.IM.IMSessionID.ToString();
                            
                            _logger.LogInformation("Received SessionSend message from unknown session ({SessionId}), treating as group message", 
                                e.IM.IMSessionID);
                        }
                    }
                    break;

                case InstantMessageDialog.MessageFromAgent:
                    // Could be either individual IM or group message
                    // Check if this message is actually from a group by checking IMSessionID against our cached groups
                    var groupNameFromAgent = await _groupService.GetGroupNameAsync(Guid.Parse(_accountId), 
                        e.IM.IMSessionID.ToString(), string.Empty);
                    
                    if (!string.IsNullOrEmpty(groupNameFromAgent))
                    {
                        // This is actually a group message, not an IM
                        sessionId = $"group-{e.IM.IMSessionID}";
                        sessionName = groupNameFromAgent;
                        chatType = "Group";
                        targetId = e.IM.IMSessionID.ToString();
                        
                        // Add to in-memory cache for faster future lookups
                        var cachedGroups = await _groupService.GetCachedGroupsAsync(Guid.Parse(_accountId));
                        if (cachedGroups.TryGetValue(e.IM.IMSessionID, out var cachedGroup))
                        {
                            _groups.TryAdd(e.IM.IMSessionID, cachedGroup);
                        }
                        
                        // Try to join the group chat if we're not already in it
                        RequestJoinGroupChatIfNeeded(e.IM.IMSessionID);
                        
                        _logger.LogInformation("Reclassified MessageFromAgent as group message for group {GroupName} ({GroupId})", 
                            groupNameFromAgent, e.IM.IMSessionID);
                    }
                    else
                    {
                        // Individual IM
                        sessionId = $"im-{e.IM.FromAgentID}";
                        sessionName = senderDisplayName;
                        chatType = "IM";
                        targetId = e.IM.FromAgentID.ToString();
                    }
                    break;

                default:
                    // Skip other dialog types (group invitations, notices, etc.)
                    return;
            }

            // Create or update session
            var session = new ChatSessionDto
            {
                SessionId = sessionId,
                SessionName = sessionName,
                ChatType = chatType,
                TargetId = targetId,
                LastActivity = DateTime.UtcNow,
                AccountId = Guid.Parse(_accountId),
                IsActive = true
            };

            _chatSessions.AddOrUpdate(sessionId, session, (key, existing) =>
            {
                existing.LastActivity = DateTime.UtcNow;
                existing.UnreadCount++;
                existing.SessionName = sessionName; // Update name in case display name changed
                return existing;
            });

            ChatSessionUpdated?.Invoke(this, session);

            // Process any SLURLs in the IM message
            var processedMessage = await _urlParser.ProcessChatMessageAsync(e.IM.Message, Guid.Parse(_accountId));

            // Ensure we don't show "Loading..." as sender name in the UI
            var finalSenderName = senderDisplayName;
            if (finalSenderName == "Loading..." || string.IsNullOrEmpty(finalSenderName))
            {
                finalSenderName = e.IM.FromAgentName;
            }

            var chatMessage = new ChatMessageDto
            {
                AccountId = Guid.Parse(_accountId),
                SenderName = finalSenderName,
                Message = processedMessage, // Use the processed message with URL replacements
                ChatType = chatType,
                Channel = chatType,
                Timestamp = DateTime.UtcNow,
                RegionName = _client.Network.CurrentSim?.Name,
                SenderId = e.IM.FromAgentID.ToString(),
                SessionId = sessionId,
                SessionName = sessionName,
                TargetId = targetId
            };

            ChatReceived?.Invoke(this, chatMessage);
        }

        private void Self_TeleportProgress(object? sender, TeleportEventArgs e)
        {
            if (e.Status == TeleportStatus.Finished)
            {
                AccountInfo.CurrentRegion = _client.Network.CurrentSim?.Name;
                UpdateStatus($"Teleported to {AccountInfo.CurrentRegion}");
                UpdateRegionInfo();
                _nearbyAvatars.Clear(); // Clear avatars from previous location
                
                // Proactively start loading display names for the new location
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Wait for avatars to start appearing at the new location
                        await Task.Delay(3000);
                        
                        // Preload any avatars that have appeared at the new location
                        if (_nearbyAvatars.Count > 0)
                        {
                            var avatarIds = _nearbyAvatars.Keys.Select(id => id.ToString()).ToList();
                            await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), avatarIds);
                            _logger.LogInformation("Preloaded display names for {Count} avatars after teleport", avatarIds.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error preloading display names after teleport for account {AccountId}", _accountId);
                    }
                });
            }
            else if (e.Status == TeleportStatus.Failed)
            {
                UpdateStatus("Teleport failed");
            }
        }

        private async void Self_AlertMessage(object? sender, AlertMessageEventArgs e)
        {
            // Process region notices through the notice service
            await _noticeService.ProcessRegionAlertAsync(Guid.Parse(_accountId), e.Message);
            
            // Also create a system chat message for immediate display
            var chatMessage = new ChatMessageDto
            {
                AccountId = Guid.Parse(_accountId),
                SenderName = "System",
                Message = e.Message,
                ChatType = "System",
                Channel = "System",
                Timestamp = DateTime.UtcNow,
                RegionName = _client.Network.CurrentSim?.Name,
                SessionId = "local-chat"
            };

            ChatReceived?.Invoke(this, chatMessage);
        }

        private async void Groups_CurrentGroups(object? sender, CurrentGroupsEventArgs e)
        {
            var previousGroupCount = _groups.Count;
            
            _groups.Clear();
            foreach (var group in e.Groups.Values)
            {
                _groups.TryAdd(group.ID, group);
                _logger.LogInformation("Loaded group: {GroupName} ({GroupId})", group.Name, group.ID);
            }
            
            // Update groups cache
            try
            {
                await _groupService.UpdateGroupsAsync(Guid.Parse(_accountId), e.Groups);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating groups cache for account {AccountId}", _accountId);
            }
            
            // Don't automatically join all group chats - join them on-demand when needed
            // This prevents "too many chat sessions" errors and matches Radegast behavior
            _logger.LogInformation("Updated {GroupCount} groups (previously had {PreviousGroupCount} cached). Group chats will be joined on-demand.", 
                _groups.Count, previousGroupCount);
            
            // Update any existing conference sessions that might actually be group sessions
            foreach (var session in _chatSessions.Values.Where(s => s.ChatType == "Conference"))
            {
                if (UUID.TryParse(session.TargetId, out UUID groupId) && _groups.ContainsKey(groupId))
                {
                    // Convert conference session to group session
                    var updatedSession = new ChatSessionDto
                    {
                        SessionId = $"group-{groupId}",
                        SessionName = _groups[groupId].Name,
                        ChatType = "Group",
                        TargetId = session.TargetId,
                        LastActivity = session.LastActivity,
                        AccountId = session.AccountId,
                        IsActive = session.IsActive,
                        UnreadCount = session.UnreadCount
                    };
                    
                    // Remove old conference session and add new group session
                    _chatSessions.TryRemove(session.SessionId, out _);
                    _chatSessions.TryAdd(updatedSession.SessionId, updatedSession);
                    
                    ChatSessionUpdated?.Invoke(this, updatedSession);
                    
                    _logger.LogInformation("Converted conference session {OldSessionId} to group session {NewSessionId} for group {GroupName}", 
                        session.SessionId, updatedSession.SessionId, _groups[groupId].Name);
                }
            }
            
            // Update any existing group sessions that have placeholder names
            foreach (var session in _chatSessions.Values.Where(s => s.ChatType == "Group"))
            {
                if (UUID.TryParse(session.TargetId, out UUID groupId) && _groups.ContainsKey(groupId))
                {
                    var groupName = _groups[groupId].Name;
                    if (session.SessionName != groupName && session.SessionName.StartsWith("Group Chat ("))
                    {
                        // Update the session with the real group name
                        session.SessionName = groupName;
                        ChatSessionUpdated?.Invoke(this, session);
                        
                        _logger.LogInformation("Updated group session {SessionId} name from placeholder to {GroupName}", 
                            session.SessionId, groupName);
                    }
                }
            }
        }

        private void Self_GroupChatJoined(object? sender, GroupChatJoinedEventArgs e)
        {
            if (e.Success)
            {
                _logger.LogInformation("Successfully joined group chat for session {SessionId}", e.SessionID);
            }
            else
            {
                _logger.LogWarning("Failed to join group chat for session {SessionId}", e.SessionID);
            }
        }

        private async void Avatars_UUIDNameReply(object? sender, UUIDNameReplyEventArgs e)
        {
            try
            {
                await _displayNameService.UpdateLegacyNamesAsync(Guid.Parse(_accountId), e.Names);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating legacy names for account {AccountId}", _accountId);
            }
        }

        private async void Avatars_DisplayNameUpdate(object? sender, DisplayNameUpdateEventArgs e)
        {
            try
            {
                var displayNames = new Dictionary<UUID, AgentDisplayName>
                {
                    { e.DisplayName.ID, e.DisplayName }
                };
                
                await _displayNameService.UpdateDisplayNamesAsync(Guid.Parse(_accountId), displayNames);
                
                // Check if this is our own display name being updated
                if (e.DisplayName.ID == _client.Self.AgentID)
                {
                    var newDisplayName = e.DisplayName.IsDefaultDisplayName
                        ? e.DisplayName.DisplayName
                        : $"{e.DisplayName.DisplayName} ({e.DisplayName.UserName})";
                        
                    var legacyName = $"{AccountInfo.FirstName} {AccountInfo.LastName}";
                    
                    if (!string.IsNullOrEmpty(newDisplayName) && newDisplayName != legacyName)
                    {
                        AccountInfo.DisplayName = newDisplayName;
                        _logger.LogInformation("Updated own display name to: {DisplayName}", newDisplayName);
                        
                        // Trigger status update to broadcast the change
                        UpdateStatus(AccountInfo.Status);
                    }
                }
                
                _logger.LogInformation("Display name updated for {AvatarId}: {DisplayName}", 
                    e.DisplayName.ID, e.DisplayName.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating display name for account {AccountId}", _accountId);
            }
        }
        
        private void DisplayNameService_DisplayNameChanged(object? sender, DisplayNameChangedEventArgs e)
        {
            try
            {
                // Check if this avatar is in our nearby avatars list
                if (UUID.TryParse(e.AvatarId, out var avatarUuid) && _nearbyAvatars.TryGetValue(avatarUuid, out var avatar))
                {
                    // Create updated avatar DTO with new display name
                    var avatarDto = new AvatarDto
                    {
                        Id = e.AvatarId,
                        Name = e.DisplayName.LegacyFullName,
                        DisplayName = e.DisplayName.DisplayNameValue,
                        Distance = CalculateHorizontalDistance(_client.Self.SimPosition, avatar.Position),
                        Status = "Online",
                        AccountId = e.DisplayName.AccountId
                    };
                    
                    // Fire the avatar updated event
                    AvatarUpdated?.Invoke(this, avatarDto);
                    
                    _logger.LogDebug("Updated nearby avatar display name for {AvatarId} to '{NewName}'", 
                        e.AvatarId, e.DisplayName.DisplayNameValue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling display name change for avatar {AvatarId}", e.AvatarId);
            }
        }
        
        private async void Directory_DirPeopleReply(object? sender, DirPeopleReplyEventArgs e)
        {
            try
            {
                // Extract names from people directory search results
                var legacyNames = new Dictionary<UUID, string>();
                
                foreach (var person in e.MatchedPeople)
                {
                    var fullName = $"{person.FirstName} {person.LastName}";
                    legacyNames[person.AgentID] = fullName;
                }
                
                if (legacyNames.Count > 0)
                {
                    await _displayNameService.UpdateLegacyNamesAsync(Guid.Parse(_accountId), legacyNames);
                    _logger.LogDebug("Updated {Count} names from directory people search", legacyNames.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing directory people reply for account {AccountId}", _accountId);
            }
        }
        
        private async void Avatars_AvatarPickerReply(object? sender, AvatarPickerReplyEventArgs e)
        {
            try
            {
                // Extract names from avatar picker search results
                var legacyNames = new Dictionary<UUID, string>();
                
                foreach (var kvp in e.Avatars)
                {
                    legacyNames[kvp.Key] = kvp.Value;
                }
                
                if (legacyNames.Count > 0)
                {
                    await _displayNameService.UpdateLegacyNamesAsync(Guid.Parse(_accountId), legacyNames);
                    _logger.LogDebug("Updated {Count} names from avatar picker search", legacyNames.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing avatar picker reply for account {AccountId}", _accountId);
            }
        }
        
        private void Groups_GroupNamesReply(object? sender, GroupNamesEventArgs e)
        {
            try
            {
                // This event doesn't contain avatar names, but group names
                // We'll process it for completeness but it doesn't affect avatar display names
                _logger.LogDebug("Received group names reply with {Count} groups", e.GroupNames.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing group names reply for account {AccountId}", _accountId);
            }
        }
        
        private async void RefreshNearbyDisplayNames(object? state)
        {
            if (!_client.Network.Connected || _disposed) return;
            
            try
            {
                var nearbyAvatarIds = _nearbyAvatars.Keys.Select(id => id.ToString()).ToList();
                if (nearbyAvatarIds.Count > 0)
                {
                    // Batch request display names for all nearby avatars
                    await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), nearbyAvatarIds);
                    
                    _logger.LogDebug("Refreshed display names for {Count} nearby avatars", nearbyAvatarIds.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during periodic display name refresh for account {AccountId}", _accountId);
            }
        }
        
        private void NoticeService_NoticeReceived(object? sender, NoticeReceivedEventArgs e)
        {
            try
            {
                // Only handle events for this account
                if (e.Notice.AccountId != Guid.Parse(_accountId))
                    return;

                // Create a chat message with the formatted notice and special styling
                var chatMessage = new ChatMessageDto
                {
                    AccountId = e.Notice.AccountId,
                    SenderName = e.Notice.FromName,
                    Message = e.DisplayMessage,
                    ChatType = "Notice", // Special chat type for notices
                    Channel = e.Notice.Type == NoticeType.Group ? "Group" : "System",
                    Timestamp = e.Notice.Timestamp,
                    RegionName = _client.Network.CurrentSim?.Name,
                    SenderId = e.Notice.FromId,
                    SessionId = e.SessionId,
                    // Add some metadata for the UI to style differently
                    SessionName = e.Notice.Type == NoticeType.Group ? e.Notice.GroupName : "Local Chat"
                };

                // Fire the chat received event for display in the appropriate chat tab
                ChatReceived?.Invoke(this, chatMessage);

                // Also fire the specific notice event for UI components that need it
                NoticeReceived?.Invoke(this, e);

                _logger.LogInformation("Forwarded {NoticeType} notice to chat for account {AccountId}", 
                    e.Notice.Type, e.Notice.AccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling notice received event for account {AccountId}", _accountId);
            }
        }

        #endregion

        #region Sit/Stand Methods
        
        /// <summary>
        /// Gets whether the avatar is currently sitting
        /// </summary>
        public bool IsSitting => _client.Self.SittingOn != 0 || _client.Self.Movement.SitOnGround;
        
        /// <summary>
        /// Gets the local ID of the object the avatar is sitting on, or 0 if not sitting on an object
        /// </summary>
        public uint SittingOnLocalId => _client.Self.SittingOn;
        
        /// <summary>
        /// Event fired when sitting state changes
        /// </summary>
        public event EventHandler<SitStateChangedEventArgs>? SitStateChanged;
        
        /// <summary>
        /// Attempts to sit on the specified object or ground
        /// </summary>
        /// <param name="target">UUID of object to sit on, or UUID.Zero to sit on ground</param>
        /// <returns>True if sit request was sent successfully</returns>
        public bool SetSitting(bool sit, UUID target = default)
        {
            try
            {
                if (!_client.Network.Connected)
                {
                    _logger.LogWarning("Cannot change sitting state - not connected");
                    return false;
                }

                if (sit)
                {
                    if (target == UUID.Zero)
                    {
                        // Sit on ground
                        _client.Self.SitOnGround();
                        _logger.LogInformation("Requested to sit on ground for account {AccountId}", _accountId);
                    }
                    else
                    {
                        // Check if object exists in current simulator
                        if (!IsObjectInRegion(target))
                        {
                            _logger.LogWarning("Cannot sit on object {ObjectId} - object not found in current region", target);
                            return false;
                        }

                        // Sit on object
                        _client.Self.RequestSit(target, Vector3.Zero);
                        _client.Self.Sit();
                        _logger.LogInformation("Requested to sit on object {ObjectId} for account {AccountId}", target, _accountId);
                    }
                }
                else
                {
                    // Stand up
                    _client.Self.Stand();
                    _logger.LogInformation("Requested to stand up for account {AccountId}", _accountId);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing sitting state for account {AccountId}", _accountId);
                return false;
            }
        }
        
        /// <summary>
        /// Checks if an object exists in the current region
        /// </summary>
        /// <param name="objectId">UUID of the object to check</param>
        /// <returns>True if object exists in current region</returns>
        public bool IsObjectInRegion(UUID objectId)
        {
            if (!_client.Network.Connected || _client.Network.CurrentSim == null)
                return false;

            // Search through all objects in the simulator to find one with the matching UUID
            return _client.Network.CurrentSim.ObjectsPrimitives.Values.Any(prim => prim.ID == objectId);
        }
        
        /// <summary>
        /// Gets information about a specific object in the region
        /// </summary>
        /// <param name="objectId">UUID of the object</param>
        /// <returns>Object information or null if not found</returns>
        public ObjectInfo? GetObjectInfo(UUID objectId)
        {
            if (!_client.Network.Connected || _client.Network.CurrentSim == null)
                return null;

            var prim = _client.Network.CurrentSim.ObjectsPrimitives.Values.FirstOrDefault(p => p.ID == objectId);
            if (prim != null)
            {
                return new ObjectInfo
                {
                    Id = prim.ID.ToString(),
                    LocalId = prim.LocalID,
                    Name = prim.Properties?.Name ?? "Unknown",
                    Description = prim.Properties?.Description ?? "",
                    Position = prim.Position,
                    OwnerId = prim.OwnerID.ToString(),
                    CanSit = (prim.Flags & PrimFlags.Touch) != 0 // Objects with touch flag can usually be sat on
                };
            }

            return null;
        }

        #endregion

        #region Distance Calculation

        /// <summary>
        /// Calculate horizontal distance between two positions, ignoring height difference.
        /// This matches how Radegast calculates nearby people distance for practical usage.
        /// </summary>
        /// <param name="pos1">First position</param>
        /// <param name="pos2">Second position</param>
        /// <returns>Horizontal distance in meters</returns>
        private static float CalculateHorizontalDistance(Vector3 pos1, Vector3 pos2)
        {
            var deltaX = pos2.X - pos1.X;
            var deltaY = pos2.Y - pos1.Y;
            return (float)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                UnregisterClientEvents();
                
                // Stop the display name refresh timer
                _displayNameRefreshTimer?.Dispose();
                _displayNameRefreshTimer = null;
                
                // Clean up display name service resources
                _displayNameService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up notice service resources
                _noticeService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up group service resources
                _groupService.CleanupAccount(Guid.Parse(_accountId));
                
                // Unregister from name resolution service
                _nameResolutionService.UnregisterInstance(Guid.Parse(_accountId));
                
                if (_client.Network.Connected)
                {
                    _ = Task.Run(async () => await DisconnectAsync());
                }
                // GridClient doesn't implement IDisposable in this version
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing WebRadegastInstance");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}