using Microsoft.EntityFrameworkCore;
using OpenMetaverse;
using RadegastWeb.Data;
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
        private readonly IMasterDisplayNameService _masterDisplayNameService;
        private readonly IStatsService _statsService;
        private readonly ICorradeService _corradeService;
        private readonly IAiChatService _aiChatService;
        private readonly IChatHistoryService _chatHistoryService;
        private readonly IScriptDialogService _scriptDialogService;
        private readonly ITeleportRequestService _teleportRequestService;
        private readonly IConnectionTrackingService _connectionTrackingService;
        private readonly IChatProcessingService _chatProcessingService;
        private readonly ISLTimeService _slTimeService;
        private readonly IPresenceService _presenceService;
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        private readonly IFriendshipRequestService _friendshipRequestService;
        private readonly IGroupInvitationService _groupInvitationService;
        private readonly IRegionMapCacheService _regionMapCacheService;
        private readonly IAutoSitService _autoSitService;
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
        
        // Track recent name requests to prevent excessive duplicate requests
        private readonly ConcurrentDictionary<UUID, DateTime> _recentNameRequests = new();
        private readonly TimeSpan _nameRequestCooldown = TimeSpan.FromMinutes(5); // Don't request same name more than once per 5 minutes
        
        // Track which avatars we've already sent proximity alerts for to prevent spam
        private readonly ConcurrentDictionary<UUID, DateTime> _proximityAlertedAvatars = new();
        
        // Track previous avatar positions to detect movement into proximity range
        private readonly ConcurrentDictionary<UUID, Vector3> _previousAvatarPositions = new();
        
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
        public event EventHandler<string>? OwnDisplayNameChanged; // New event for our own display name changes
        public event EventHandler<ChatSessionDto>? ChatSessionUpdated;
        public event EventHandler<NoticeReceivedEventArgs>? NoticeReceived;
        public event EventHandler<Models.ScriptDialogEventArgs>? ScriptDialogReceived;
        public event EventHandler<Models.ScriptPermissionEventArgs>? ScriptPermissionReceived;
        public event EventHandler<TeleportRequestEventArgs>? TeleportRequestReceived;

        public WebRadegastInstance(Account account, ILogger<WebRadegastInstance> logger, IDisplayNameService displayNameService, INoticeService noticeService, ISlUrlParser urlParser, INameResolutionService nameResolutionService, IGroupService groupService, IGlobalDisplayNameCache globalDisplayNameCache, IMasterDisplayNameService masterDisplayNameService, IStatsService statsService, ICorradeService corradeService, IAiChatService aiChatService, IChatHistoryService chatHistoryService, IScriptDialogService scriptDialogService, ITeleportRequestService teleportRequestService, IConnectionTrackingService connectionTrackingService, IChatProcessingService chatProcessingService, ISLTimeService slTimeService, IPresenceService presenceService, IDbContextFactory<RadegastDbContext> dbContextFactory, IFriendshipRequestService friendshipRequestService, IGroupInvitationService groupInvitationService, IRegionMapCacheService regionMapCacheService, IAutoSitService autoSitService)
        {
            _logger = logger;
            _displayNameService = displayNameService;
            _noticeService = noticeService;
            _urlParser = urlParser;
            _nameResolutionService = nameResolutionService;
            _groupService = groupService;
            _globalDisplayNameCache = globalDisplayNameCache;
            _masterDisplayNameService = masterDisplayNameService;
            _statsService = statsService;
            _corradeService = corradeService;
            _aiChatService = aiChatService;
            _chatHistoryService = chatHistoryService;
            _scriptDialogService = scriptDialogService;
            _teleportRequestService = teleportRequestService;
            _connectionTrackingService = connectionTrackingService;
            _chatProcessingService = chatProcessingService;
            _slTimeService = slTimeService;
            _presenceService = presenceService;
            _dbContextFactory = dbContextFactory;
            _friendshipRequestService = friendshipRequestService;
            _groupInvitationService = groupInvitationService;
            _regionMapCacheService = regionMapCacheService;
            _autoSitService = autoSitService;
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
            
            // Start periodic timer for display name refresh + self-presence recording (similar to Radegast's approach)
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
            
            // Register Friends events for friendship management
            _client.Friends.FriendshipResponse += Friends_FriendshipResponse;
            _client.Friends.FriendOnline += Friends_FriendOnline;
            _client.Friends.FriendOffline += Friends_FriendOffline;
            
            // Register display name events for automatic cache updates
            _client.Avatars.UUIDNameReply += Avatars_UUIDNameReply;
            _client.Avatars.DisplayNameUpdate += Avatars_DisplayNameUpdate;
            
            // Register additional events that can provide name information
            _client.Directory.DirPeopleReply += Directory_DirPeopleReply;
            _client.Avatars.AvatarPickerReply += Avatars_AvatarPickerReply;
            _client.Groups.GroupNamesReply += Groups_GroupNamesReply;
            
            // Subscribe to display name changes from our service
            _displayNameService.DisplayNameChanged += DisplayNameService_DisplayNameChanged;
            
            // Subscribe to global display name cache changes
            _globalDisplayNameCache.DisplayNameChanged += GlobalDisplayNameCache_DisplayNameChanged;
            
            // Subscribe to master display name service changes
            _masterDisplayNameService.DisplayNameChanged += MasterDisplayNameService_DisplayNameChanged;
            
            // Subscribe to notice events
            _noticeService.NoticeReceived += NoticeService_NoticeReceived;
            
            // Subscribe to script dialog service events
            _scriptDialogService.DialogReceived += ScriptDialogService_DialogReceived;
            _scriptDialogService.PermissionReceived += ScriptDialogService_PermissionReceived;
            
            // Subscribe to teleport request service events
            _teleportRequestService.TeleportRequestReceived += TeleportRequestService_TeleportRequestReceived;
            
            // Register script dialog events
            _client.Self.ScriptDialog += Self_ScriptDialog;
            _client.Self.ScriptQuestion += Self_ScriptQuestion;
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
            
            // Unregister Friends events
            _client.Friends.FriendshipResponse -= Friends_FriendshipResponse;
            _client.Friends.FriendOnline -= Friends_FriendOnline;
            _client.Friends.FriendOffline -= Friends_FriendOffline;
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
            
            // Unsubscribe from global display name cache changes
            _globalDisplayNameCache.DisplayNameChanged -= GlobalDisplayNameCache_DisplayNameChanged;
            
            // Unsubscribe from master display name service changes
            _masterDisplayNameService.DisplayNameChanged -= MasterDisplayNameService_DisplayNameChanged;
            
            // Unsubscribe from notice events
            _noticeService.NoticeReceived -= NoticeService_NoticeReceived;
            
            // Unsubscribe from script dialog service events
            _scriptDialogService.DialogReceived -= ScriptDialogService_DialogReceived;
            _scriptDialogService.PermissionReceived -= ScriptDialogService_PermissionReceived;
            
            // Unsubscribe from teleport request service events
            _teleportRequestService.TeleportRequestReceived -= TeleportRequestService_TeleportRequestReceived;
            
            // Unregister script dialog events
            _client.Self.ScriptDialog -= Self_ScriptDialog;
            _client.Self.ScriptQuestion -= Self_ScriptQuestion;
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
                            if (!string.IsNullOrEmpty(ownDisplayName) && ownDisplayName != legacyName && ownDisplayName != "Loading..." && ownDisplayName != AccountInfo.DisplayName)
                            {
                                var oldDisplayName = AccountInfo.DisplayName;
                                AccountInfo.DisplayName = ownDisplayName;
                                _logger.LogInformation("Updated account display name from '{OldDisplayName}' to: '{NewDisplayName}'", oldDisplayName, ownDisplayName);
                                
                                // Fire event to notify listeners (like AccountService) that our display name changed
                                OwnDisplayNameChanged?.Invoke(this, ownDisplayName);
                                
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
                    
                    // Schedule auto-sit if enabled
                    _ = Task.Run(async () => 
                    {
                        try
                        {
                            await _autoSitService.ScheduleAutoSitAsync(Guid.Parse(_accountId), this);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error scheduling auto-sit for account {AccountId}", _accountId);
                        }
                    });
                    
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
                
                // Check if sitting and stand up before logout
                if (IsSitting)
                {
                    _logger.LogInformation("Avatar is sitting, standing up before logout for account {AccountId}", _accountId);
                    SetSitting(false); // Stand up and stop animations
                    
                    // Small delay to allow the stand-up command to be processed
                    await Task.Delay(500);
                }
                
                await Task.Run(() => _client.Network.Logout());
                
                AccountInfo.IsConnected = false;
                AccountInfo.CurrentRegion = null; // Clear region info
                
                // Reset status to offline and clear any presence status
                UpdateStatus("Offline");
                
                // Reset presence status to clear any Away/Busy status
                ResetPresenceStatusOnDisconnect();
                
                ConnectionChanged?.Invoke(this, false);
                
                // Trigger region change event with empty region to clear it in the database
                var regionInfo = new RegionInfoDto
                {
                    Name = string.Empty,
                    AvatarCount = 0,
                    AccountId = Guid.Parse(_accountId),
                    RegionX = 0,
                    RegionY = 0
                };
                RegionChanged?.Invoke(this, regionInfo);
                
                // Clean up all cached data
                _nearbyAvatars.Clear();
                _proximityAlertedAvatars.Clear(); // Clear proximity tracking on disconnect
                _previousAvatarPositions.Clear(); // Clear position tracking on disconnect
                _chatSessions.Clear();
                _groups.Clear();
                _coarseLocationAvatars.Clear(); // Clear coarse location avatars
                _avatarSimHandles.Clear(); // Clear avatar sim handles
                _recentNameRequests.Clear(); // Clear name request tracking
                
                // Stop any periodic timers
                _displayNameRefreshTimer?.Dispose();
                _displayNameRefreshTimer = null;
                
                // Clean up display name service resources
                _displayNameService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up notice service resources
                _noticeService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up group service resources
                _groupService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up region map cache for this account
                await _regionMapCacheService.CleanupAccountMapsAsync(Guid.Parse(_accountId));
                
                _logger.LogInformation("Completed manual disconnect cleanup for account {AccountId}", _accountId);
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
                var senderName = !string.IsNullOrEmpty(AccountInfo.DisplayName) && AccountInfo.DisplayName != $"{AccountInfo.FirstName} {AccountInfo.LastName}"
                    ? AccountInfo.DisplayName 
                    : $"{AccountInfo.FirstName} {AccountInfo.LastName}";
                    
                var chatMessage = CreateChatMessage(
                    senderName: senderName,
                    message: message,
                    chatType: chatType.ToString(),
                    channel: channel.ToString(),
                    senderId: _client.Self.AgentID.ToString(),
                    sessionId: "local-chat"
                );
                
                // Process outgoing chat through chat pipeline (handles database save and SignalR broadcast)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _chatProcessingService.ProcessChatMessageAsync(chatMessage, Guid.Parse(_accountId));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing outgoing chat through chat pipeline");
                    }
                });
                
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

                        // If the display name is still invalid, proactively request a fresh one
                        if (string.IsNullOrWhiteSpace(targetDisplayName) || targetDisplayName == "Loading..." || targetDisplayName == "???")
                        {
                            // Use a more informative fallback that includes the UUID prefix
                            targetDisplayName = $"Resolving... ({targetId.Substring(0, 8)})";

                            // Proactively request a proper display name for future use
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    // Request display name refresh for this avatar
                                    await _displayNameService.RefreshDisplayNameAsync(Guid.Parse(_accountId), targetId);

                                    // Also trigger a legacy name request as fallback
                                    if (_client.Network.Connected && UUID.TryParse(targetId, out UUID avatarUUID))
                                    {
                                        _client.Avatars.RequestAvatarNames(new List<UUID> { avatarUUID });
                                    }
                                }
                                catch (Exception refreshEx)
                                {
                                    _logger.LogDebug(refreshEx, "Error requesting name refresh for IM target {TargetId}", targetId);
                                }
                            });
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

                    // Process outgoing IM through chat pipeline (handles database save and SignalR broadcast)
                    try
                    {
                        await _chatProcessingService.ProcessChatMessageAsync(chatMessage, Guid.Parse(_accountId));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing outgoing IM through chat pipeline");
                        
                        // Fallback: save to database directly if pipeline fails
                        try
                        {
                            await _chatHistoryService.SaveChatMessageAsync(chatMessage);
                        }
                        catch (Exception saveEx)
                        {
                            _logger.LogError(saveEx, "Error saving outgoing IM to database as fallback");
                        }
                    }

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
                    // Check if we're already in the session
                    if (!_client.Self.GroupChatSessions.ContainsKey(groupUUID))
                    {
                        // Need to join - use a wait handle for the event
                        using (var joinEvent = new System.Threading.ManualResetEventSlim(false))
                        {
                            bool joinSucceeded = false;
                            
                            // Set up event handler for join completion
                            EventHandler<GroupChatJoinedEventArgs> joinHandler = (s, e) =>
                            {
                                if (e.SessionID == groupUUID)
                                {
                                    joinSucceeded = e.Success;
                                    joinEvent.Set();
                                }
                            };
                            
                            try
                            {
                                _client.Self.GroupChatJoined += joinHandler;
                                
                                // Request to join
                                if (RequestJoinGroupChatIfNeeded(groupUUID))
                                {
                                    // Wait up to 10 seconds for join to complete
                                    if (!joinEvent.Wait(TimeSpan.FromSeconds(10)))
                                    {
                                        _logger.LogWarning("Timeout waiting for group chat join response for {GroupId}", groupId);
                                        return;
                                    }
                                    
                                    if (!joinSucceeded)
                                    {
                                        _logger.LogWarning("Failed to join group chat for {GroupId}", groupId);
                                        return;
                                    }
                                }
                                else
                                {
                                    // Already joined or can't join
                                    if (!_client.Self.GroupChatSessions.ContainsKey(groupUUID))
                                    {
                                        _logger.LogWarning("Cannot join group chat for {GroupId} - not a member or already failed", groupId);
                                        return;
                                    }
                                }
                            }
                            finally
                            {
                                _client.Self.GroupChatJoined -= joinHandler;
                            }
                        }
                    }

                    // Now send the message
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

        /// <summary>
        /// Send a group invitation to an avatar
        /// </summary>
        /// <param name="groupId">The group UUID to invite to</param>
        /// <param name="agentId">The avatar UUID to invite</param>
        /// <returns>True if the invitation was sent successfully</returns>
        public bool SendGroupInvite(string groupId, string agentId)
        {
            if (!_client.Network.Connected)
            {
                _logger.LogWarning("Attempted to send group invite while not connected");
                return false;
            }

            try
            {
                if (!UUID.TryParse(groupId, out UUID groupUUID))
                {
                    _logger.LogError("Invalid group UUID format: {GroupId}", groupId);
                    return false;
                }

                if (!UUID.TryParse(agentId, out UUID agentUUID))
                {
                    _logger.LogError("Invalid agent UUID format: {AgentId}", agentId);
                    return false;
                }

                // Check if we're actually a member of this group
                if (!_groups.ContainsKey(groupUUID))
                {
                    _logger.LogWarning("Cannot invite to group {GroupId} - not a member", groupId);
                    return false;
                }

                // Send group invitation using LibreMetaverse
                // Use UUID.Zero for the role ID to assign the default "Everyone" role
                var roleIds = new List<UUID> { UUID.Zero };
                _client.Groups.Invite(groupUUID, roleIds, agentUUID);

                _logger.LogInformation("Sent group invitation to {AgentId} for group {GroupId}", agentId, groupId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending group invitation to {AgentId} for group {GroupId}", agentId, groupId);
                return false;
            }
        }

        /// <summary>
        /// Gets the raw avatar data (IDs and basic info) for nearby avatars without triggering display name lookups.
        /// Used by PeriodicDisplayNameService to avoid circular dependencies.
        /// </summary>
        public Task<IEnumerable<(string Id, string Name, Vector3 Position)>> GetNearbyAvatarDataAsync()
        {
            var avatarData = new List<(string Id, string Name, Vector3 Position)>();
            
            // Add detailed avatars
            foreach (var avatar in _nearbyAvatars.Values)
            {
                var actualPosition = GetAvatarActualPosition(avatar);
                avatarData.Add((avatar.ID.ToString(), GetBestAvatarName(avatar.ID, avatar.Name), actualPosition));
            }
            
            // Add coarse location avatars that aren't already detailed
            var detailedIds = new HashSet<string>(_nearbyAvatars.Keys.Select(id => id.ToString()));
            foreach (var coarseAvatar in _coarseLocationAvatars.Values)
            {
                if (!detailedIds.Contains(coarseAvatar.ID.ToString()))
                {
                    avatarData.Add((coarseAvatar.ID.ToString(), GetBestAvatarName(coarseAvatar.ID, coarseAvatar.Name), coarseAvatar.Position));
                }
            }
            
            return Task.FromResult(avatarData.AsEnumerable());
        }

        public async Task<IEnumerable<AvatarDto>> GetNearbyAvatarsAsync()
        {
            // First, proactively ensure all nearby avatar display names are loading using the global cache
            var allAvatarIds = new List<string>();
            
            // Collect IDs from both detailed and coarse location avatars
            allAvatarIds.AddRange(_nearbyAvatars.Keys.Select(id => id.ToString()));
            allAvatarIds.AddRange(_coarseLocationAvatars.Keys.Select(id => id.ToString()));
            
            if (allAvatarIds.Count > 0)
            {
                // Fire and forget - start loading names in background through global cache
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _globalDisplayNameCache.PreloadDisplayNamesAsync(allAvatarIds.Distinct().ToList());
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
            var avatarName = GetBestAvatarName(avatar.ID, avatar.Name);
            var displayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), avatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
            var legacyName = await _displayNameService.GetLegacyNameAsync(Guid.Parse(_accountId), avatar.ID.ToString(), avatarName);
            
            // Fallback to avatar name if display name is invalid
            if (string.IsNullOrWhiteSpace(displayName) || displayName == "Loading..." || displayName == "???")
            {
                displayName = avatarName;
            }
            
            // Fallback to avatar name if legacy name is invalid
            if (string.IsNullOrWhiteSpace(legacyName) || legacyName == "Loading..." || legacyName == "???")
            {
                legacyName = avatarName;
            }

            // Calculate actual avatar position accounting for seating
            var actualPosition = GetAvatarActualPosition(avatar);
            
            return new AvatarDto
            {
                Id = avatar.ID.ToString(),
                Name = legacyName,
                DisplayName = displayName,
                Distance = Calculate3DDistance(GetOurActualPosition(), actualPosition),
                Status = "Online", // TODO: Get actual status if available
                AccountId = Guid.Parse(_accountId),
                Position = new PositionDto
                {
                    X = actualPosition.X,
                    Y = actualPosition.Y,
                    Z = actualPosition.Z
                }
            };
        }

        private AvatarDto CreateAvatarDto(Avatar avatar)
        {
            var avatarName = GetBestAvatarName(avatar.ID, avatar.Name);
            string displayName = avatarName;
            
            // Try to get display name from global cache synchronously
            try
            {
                // Check if we have the display name in global cache
                var cachedName = _globalDisplayNameCache.GetCachedDisplayName(avatar.ID.ToString());
                if (!string.IsNullOrWhiteSpace(cachedName) && 
                    cachedName != "Loading..." && 
                    cachedName != "???" &&
                    cachedName != avatarName) // Don't replace with same value
                {
                    displayName = cachedName;
                }
                // If not in cache, use the avatar name and trigger async loading for next time
                else
                {
                    // IMMEDIATELY request the display name (like Radegast does)
                    // Use deduplicated request to prevent cache bloat
                    RequestAvatarNamesWithDeduplication(avatar.ID);
                    
                    // Also trigger background preload
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

            // Calculate actual avatar position accounting for seating
            var actualPosition = GetAvatarActualPosition(avatar);
            
            return new AvatarDto
            {
                Id = avatar.ID.ToString(),
                Name = avatarName,
                DisplayName = displayName,
                Distance = Calculate3DDistance(GetOurActualPosition(), actualPosition),
                Status = "Online", // TODO: Get actual status if available
                AccountId = Guid.Parse(_accountId),
                Position = new PositionDto
                {
                    X = actualPosition.X,
                    Y = actualPosition.Y,
                    Z = actualPosition.Z
                }
            };
        }

        private async Task<AvatarDto> CreateAvatarDtoFromCoarseAsync(CoarseLocationAvatar coarseAvatar)
        {
            var avatarName = coarseAvatar.Name;
            if (string.IsNullOrEmpty(avatarName) || avatarName == "Loading...")
            {
                avatarName = GetBestAvatarName(coarseAvatar.ID, coarseAvatar.Name);
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
                Distance = Calculate3DDistance(GetOurActualPosition(), coarseAvatar.Position),
                Status = "Online", // Coarse location avatars are assumed online
                AccountId = Guid.Parse(_accountId),
                Position = new PositionDto
                {
                    X = coarseAvatar.Position.X,
                    Y = coarseAvatar.Position.Y,
                    Z = coarseAvatar.Position.Z
                }
            };
        }

        private AvatarDto CreateAvatarDtoFromCoarse(CoarseLocationAvatar coarseAvatar)
        {
            var avatarName = coarseAvatar.Name;
            if (string.IsNullOrEmpty(avatarName) || avatarName == "Loading...")
            {
                avatarName = GetBestAvatarName(coarseAvatar.ID, coarseAvatar.Name);
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
                else
                {
                    // IMMEDIATELY request the display name for coarse avatars too
                    // Use deduplicated request to prevent cache bloat
                    RequestAvatarNamesWithDeduplication(coarseAvatar.ID);
                    
                    // Also trigger background load for fallback
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), coarseAvatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error loading name for coarse avatar {AvatarId}", coarseAvatar.ID);
                        }
                    });
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
                Distance = Calculate3DDistance(GetOurActualPosition(), coarseAvatar.Position),
                Status = "Online", // Coarse location avatars are assumed online
                AccountId = Guid.Parse(_accountId),
                Position = new PositionDto
                {
                    X = coarseAvatar.Position.X,
                    Y = coarseAvatar.Position.Y,
                    Z = coarseAvatar.Position.Z
                }
            };
        }

        public async Task<IEnumerable<ChatSessionDto>> GetChatSessionsAsync()
        {
            try
            {
                // MEMORY FIX: Query database for recent sessions instead of keeping everything in memory
                var recentSessions = await _chatHistoryService.GetRecentSessionsAsync(Guid.Parse(_accountId));
                
                // Merge with any active in-memory sessions (for things like ongoing IMs)
                var activeSessions = _chatSessions.Values.ToList();
                
                // Create a combined list, preferring database data but including any newer active sessions
                var combinedSessions = new Dictionary<string, ChatSessionDto>();
                
                // Add database sessions first
                foreach (var session in recentSessions)
                {
                    combinedSessions[session.SessionId] = session;
                }
                
                // Update with any newer active sessions from memory
                foreach (var activeSession in activeSessions)
                {
                    if (!combinedSessions.ContainsKey(activeSession.SessionId) || 
                        combinedSessions[activeSession.SessionId].LastActivity < activeSession.LastActivity)
                    {
                        combinedSessions[activeSession.SessionId] = activeSession;
                    }
                }
                
                return combinedSessions.Values.OrderByDescending(s => s.LastActivity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat sessions for account {AccountId}", _accountId);
                // Fallback to in-memory sessions only
                return _chatSessions.Values.OrderByDescending(s => s.LastActivity);
            }
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

        /// <summary>
        /// Set the ignore status for a group
        /// </summary>
        public async Task SetGroupIgnoreStatusAsync(string groupId, bool isIgnored)
        {
            try
            {
                await _groupService.SetGroupIgnoreStatusAsync(Guid.Parse(_accountId), groupId, isIgnored);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting ignore status for group {GroupId} on account {AccountId}", groupId, _accountId);
                throw;
            }
        }

        /// <summary>
        /// Check if a group is ignored
        /// </summary>
        public async Task<bool> IsGroupIgnoredAsync(string groupId)
        {
            try
            {
                return await _groupService.IsGroupIgnoredAsync(Guid.Parse(_accountId), groupId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking ignore status for group {GroupId} on account {AccountId}", groupId, _accountId);
                return false;
            }
        }

        /// <summary>
        /// Requests current groups from Second Life server, triggering a cache refresh
        /// </summary>
        public void RefreshGroupsCache()
        {
            if (_client?.Groups != null && _client.Network.Connected)
            {
                _logger.LogDebug("Requesting current groups refresh for account {AccountId}", _accountId);
                _client.Groups.RequestCurrentGroups();
            }
        }

        public void TriggerStatusUpdate()
        {
            UpdateStatus(Status);
        }

        /// <summary>
        /// Updates the presence status (Away, Busy, Online) for this instance
        /// </summary>
        public void UpdatePresenceStatus(string presenceStatus)
        {
            UpdateStatus(presenceStatus);
        }

        /// <summary>
        /// Performs periodic cleanup of LibOpenMetaverse internal collections to prevent memory leaks
        /// This should be called periodically (e.g., every 30 minutes) to clean up accumulated data
        /// </summary>
        public void PerformLibOpenMetaverseCleanup()
        {
            try
            {
                if (!_client.Network.Connected || _client.Network.CurrentSim == null)
                    return;

                var memoryBefore = GC.GetTotalMemory(false);
                int totalCleaned = 0;

                // Clean up simulator object caches
                var currentSim = _client.Network.CurrentSim;
                if (currentSim != null)
                {
                    // Clean up old avatar objects beyond reasonable range (keep those within 512m)
                    // ObjectsAvatars uses LocalID as key, not UUID
                    var ourPosition = GetOurActualPosition();
                    var avatarsToRemove = currentSim.ObjectsAvatars.Values
                        .Where(a => a.ID != _client.Self.AgentID)
                        .Where(a => Calculate3DDistance(ourPosition, GetAvatarActualPosition(a)) > 512.0f) // Beyond sim range
                        .Select(a => a.LocalID) // Use LocalID for removal
                        .Take(50) // Limit to prevent excessive removal
                        .ToList();

                    foreach (var avatarLocalId in avatarsToRemove)
                    {
                        if (currentSim.ObjectsAvatars.TryRemove(avatarLocalId, out _))
                            totalCleaned++;
                    }

                    // Clean up distant primitive objects (keep only those within reasonable range)
                    // ObjectsPrimitives uses LocalID as key
                    var primsToRemove = currentSim.ObjectsPrimitives.Values
                        .Where(p => Calculate3DDistance(ourPosition, p.Position) > 256.0f) // Beyond typical draw distance
                        .Select(p => p.LocalID)
                        .Take(100) // Limit to prevent excessive removal
                        .ToList();

                    foreach (var primLocalId in primsToRemove)
                    {
                        if (currentSim.ObjectsPrimitives.TryRemove(primLocalId, out _))
                            totalCleaned++;
                    }

                    // Clean up AvatarPositions (coarse location data) for distant avatars
                    // This dictionary can grow large in busy sims
                    if (currentSim.AvatarPositions?.Count > 100)
                    {
                        var positionsToRemove = currentSim.AvatarPositions
                            .Where(kvp => kvp.Key != _client.Self.AgentID)
                            .Where(kvp => Calculate3DDistance(ourPosition, kvp.Value) > 1024.0f) // Very distant
                            .Select(kvp => kvp.Key)
                            .Take(25)
                            .ToList();

                        foreach (var avatarId in positionsToRemove)
                        {
                            if (currentSim.AvatarPositions.TryRemove(avatarId, out _))
                                totalCleaned++;
                        }
                    }
                }

                // Clean up asset cache if it's getting large
                if (_client.Assets?.Cache != null)
                {
                    // Check cache size and prune if necessary
                    try
                    {
                        // Force a cache cleanup by temporarily enabling auto-prune
                        var oldAutoPrune = _client.Settings.ASSET_CACHE_DIR;
                        _client.Assets.Cache.AutoPruneEnabled = true;
                        
                        // Trigger cache maintenance
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                _client.Assets.Cache.Clear();
                                _client.Assets.Cache.AutoPruneEnabled = false;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Error during asset cache cleanup for account {AccountId}", _accountId);
                            }
                        });
                        
                        totalCleaned += 50; // Estimate for asset cache cleanup
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error accessing asset cache for cleanup on account {AccountId}", _accountId);
                    }
                }

                // MEMORY FIX: Clean up old in-memory chat sessions to prevent unbounded growth
                var chatSessionsCleared = CleanupOldChatSessions();
                totalCleaned += chatSessionsCleared;

                // Clean up network message queues (if accessible)
                try
                {
                    // Force garbage collection after cleanup
                    if (totalCleaned > 100)
                    {
                        GC.Collect(1, GCCollectionMode.Optimized);
                    }

                    var memoryAfter = GC.GetTotalMemory(false);
                    var memorySaved = (memoryBefore - memoryAfter) / (1024.0 * 1024.0);

                    if (totalCleaned > 0 || memorySaved > 1.0)
                    {
                        _logger.LogInformation(
                            "LibOpenMetaverse cleanup for account {AccountId}: cleaned {CleanedItems} items, saved {MemoryMB:F1}MB memory",
                            _accountId, totalCleaned, memorySaved);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during memory measurement in LibOpenMetaverse cleanup for account {AccountId}", _accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during LibOpenMetaverse periodic cleanup for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Clean up old chat sessions from memory to prevent unbounded growth
        /// Keeps only sessions that have been active in the last 2 hours
        /// </summary>
        private int CleanupOldChatSessions()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddHours(-2); // Keep sessions active within last 2 hours
                var sessionsToRemove = _chatSessions
                    .Where(kvp => kvp.Value.LastActivity < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                var removedCount = 0;
                foreach (var sessionId in sessionsToRemove)
                {
                    if (_chatSessions.TryRemove(sessionId, out _))
                    {
                        removedCount++;
                    }
                }

                // MEMORY FIX: Also cleanup old avatar tracking data periodically
                var avatarCutoffTime = DateTime.UtcNow.AddMinutes(-15); // Clean avatars not seen for 15 minutes
                var staleAvatarsToRemove = _nearbyAvatars
                    .Where(kvp => !_coarseLocationAvatars.ContainsKey(kvp.Key)) // Don't remove if still in coarse tracking
                    .Take(50) // Limit cleanup to prevent performance impact
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var avatarId in staleAvatarsToRemove)
                {
                    _nearbyAvatars.TryRemove(avatarId, out _);
                    _proximityAlertedAvatars.TryRemove(avatarId, out _);
                    _previousAvatarPositions.TryRemove(avatarId, out _);
                    removedCount++;
                }

                // MEMORY FIX: Cleanup coarse location avatars that are very old
                var coarseAvatarsToRemove = _coarseLocationAvatars
                    .Where(kvp => DateTime.UtcNow - kvp.Value.LastUpdate > TimeSpan.FromMinutes(30))
                    .Take(50) // Limit cleanup
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var avatarId in coarseAvatarsToRemove)
                {
                    _coarseLocationAvatars.TryRemove(avatarId, out _);
                    _avatarSimHandles.TryRemove(avatarId, out _);
                    _proximityAlertedAvatars.TryRemove(avatarId, out _);
                    _previousAvatarPositions.TryRemove(avatarId, out _);
                    removedCount++;
                }

                // MEMORY FIX: Cleanup old name request tracking
                CleanupOldNameRequests();

                if (removedCount > 0)
                {
                    _logger.LogDebug("Cleaned up {RemovedCount} old chat sessions and stale avatar data from memory for account {AccountId}", 
                        removedCount, _accountId);
                }

                return removedCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during cleanup for account {AccountId}", _accountId);
                return 0;
            }
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

        /// <summary>
        /// Request avatar names with deduplication to prevent cache bloat
        /// Only requests names that haven't been requested recently
        /// </summary>
        private void RequestAvatarNamesWithDeduplication(UUID avatarId)
        {
            if (!_client.Network.Connected)
                return;

            // Check if we've already requested this name recently
            if (_recentNameRequests.TryGetValue(avatarId, out var lastRequest))
            {
                if (DateTime.UtcNow - lastRequest < _nameRequestCooldown)
                {
                    // Skip request - too recent
                    return;
                }
            }

            // Update request time
            _recentNameRequests.AddOrUpdate(avatarId, DateTime.UtcNow, (key, old) => DateTime.UtcNow);

            // Perform the actual request
            _client.Avatars.RequestAvatarNames(new List<UUID> { avatarId });
            
            // Request display names with callback to update global cache
            _client.Avatars.GetDisplayNames(new List<UUID> { avatarId }, (success, names, badIDs) =>
            {
                if (success && names != null && names.Any())
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var nameDict = new Dictionary<UUID, AgentDisplayName>();
                            foreach (var name in names)
                            {
                                nameDict[name.ID] = name;
                            }
                            await _globalDisplayNameCache.UpdateDisplayNamesAsync(nameDict);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error updating display names from deduplicated request");
                        }
                    });
                }
            });
        }

        /// <summary>
        /// Cleanup old name request tracking to prevent memory leaks
        /// </summary>
        private void CleanupOldNameRequests()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.Subtract(_nameRequestCooldown).AddMinutes(-5); // Extra buffer
                var expiredKeys = _recentNameRequests
                    .Where(kvp => kvp.Value < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _recentNameRequests.TryRemove(key, out _);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} old name request tracking entries for account {AccountId}", 
                        expiredKeys.Count, _accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up old name requests for account {AccountId}", _accountId);
            }
        }

        private void UpdateStatus(string status)
        {
            Status = status;
            AccountInfo.Status = status;
            StatusChanged?.Invoke(this, status);
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
                
                // Also register with the master display name service
                _masterDisplayNameService.RegisterGridClient(Guid.Parse(_accountId), _client);
                _logger.LogDebug("Registered grid client {AccountId} with master display name service", _accountId);
                
                // Request current groups after successful login
                _client.Groups.RequestCurrentGroups();
                
                // Start periodic maintenance timer (display names refresh + self-presence recording every 5 minutes)
                _displayNameRefreshTimer?.Dispose();
                _displayNameRefreshTimer = new System.Threading.Timer(PeriodicMaintenanceRefresh, null, 
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
                        
                        // FIXED: Record our own avatar as a visitor to the region after login
                        await RecordOwnAvatarAsync();
                        
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

        private async void PeriodicMaintenanceRefresh(object? state)
        {
            try
            {
                if (!_client.Network.Connected)
                    return;

                // FIXED: Always record our own avatar periodically to maintain presence stats
                await RecordOwnAvatarAsync();

                if (_nearbyAvatars.Count > 0)
                {
                    var avatarIds = _nearbyAvatars.Keys.Select(id => id.ToString()).ToList();
                    
                    // Refresh display names for all nearby avatars
                    // This ensures we pick up any display name changes
                    await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), avatarIds);
                    
                    _logger.LogDebug("Periodic maintenance refresh completed: display names for {Count} avatars + self-presence recording", avatarIds.Count);
                }
                else
                {
                    _logger.LogDebug("Periodic maintenance refresh completed: self-presence recording only");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in periodic maintenance refresh for account {AccountId}", _accountId);
            }
        }

        private void Network_Disconnected(object? sender, DisconnectedEventArgs e)
        {
            _logger.LogInformation("Network disconnected for account {AccountId}: {Reason}", _accountId, e.Reason);
            
            AccountInfo.IsConnected = false;
            AccountInfo.CurrentRegion = null; // Clear region info on disconnect
            
            // Reset status to offline and clear any presence status  
            var disconnectReason = DetermineDisconnectReason(e.Reason);
            UpdateStatus($"Offline ({disconnectReason})");
            
            // Reset presence status to clear any Away/Busy status
            ResetPresenceStatusOnDisconnect();
            
            ConnectionChanged?.Invoke(this, false);
            
            // Trigger region change event with empty region to clear it in the database
            var regionInfo = new RegionInfoDto
            {
                Name = string.Empty,
                AvatarCount = 0,
                AccountId = Guid.Parse(_accountId),
                RegionX = 0,
                RegionY = 0
            };
            RegionChanged?.Invoke(this, regionInfo);
            
            // Unregister from global display name cache
            _globalDisplayNameCache.UnregisterGridClient(Guid.Parse(_accountId));
            _logger.LogDebug("Unregistered grid client {AccountId} from global display name cache", _accountId);
            
            // Also unregister from master display name service
            _masterDisplayNameService.UnregisterGridClient(Guid.Parse(_accountId));
            _logger.LogDebug("Unregistered grid client {AccountId} from master display name service", _accountId);
            
            // Clean up all cached data
            _nearbyAvatars.Clear();
            _proximityAlertedAvatars.Clear(); // Clear proximity tracking on client cleanup
            _previousAvatarPositions.Clear(); // Clear position tracking on client cleanup
            _chatSessions.Clear();
            _groups.Clear();
            _coarseLocationAvatars.Clear(); // Clear coarse location avatars
            _avatarSimHandles.Clear(); // Clear avatar sim handles
            _recentNameRequests.Clear(); // Clear name request tracking
            
            // Remove account from stats service region tracking
            _statsService.RemoveAccountRegion(Guid.Parse(_accountId));
            
            // Stop any periodic timers
            _displayNameRefreshTimer?.Dispose();
            _displayNameRefreshTimer = null;
            
            // CRITICAL: Call the same service cleanup as manual disconnect
            // This prevents memory leaks on simulator restarts and network timeouts
            try
            {
                // Clean up display name service resources
                _displayNameService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up notice service resources
                _noticeService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up group service resources
                _groupService.CleanupAccount(Guid.Parse(_accountId));
                
                // Clean up region map cache for this account (async but don't await in event handler)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _regionMapCacheService.CleanupAccountMapsAsync(Guid.Parse(_accountId));
                        _logger.LogDebug("Cleaned up region map cache for disconnected account {AccountId}", _accountId);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, "Error cleaning up region map cache for account {AccountId}", _accountId);
                    }
                });
                
                _logger.LogInformation("Completed full disconnect cleanup for account {AccountId} (including service cleanup)", _accountId);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Error during service cleanup for disconnected account {AccountId}", _accountId);
            }
        }

        private void Network_LoggedOut(object? sender, LoggedOutEventArgs e)
        {
            _logger.LogInformation("Network logged out for account {AccountId}", _accountId);
            
            AccountInfo.IsConnected = false;
            AccountInfo.CurrentRegion = null; // Clear region info on logout
            
            // Reset status to offline and clear any presence status
            UpdateStatus("Offline");
            
            // Reset presence status to clear any Away/Busy status
            ResetPresenceStatusOnDisconnect();
            
            ConnectionChanged?.Invoke(this, false);
            
            // Trigger region change event with empty region to clear it in the database
            var regionInfo = new RegionInfoDto
            {
                Name = string.Empty,
                AvatarCount = 0,
                AccountId = Guid.Parse(_accountId),
                RegionX = 0,
                RegionY = 0
            };
            RegionChanged?.Invoke(this, regionInfo);
            
            // Unregister from global display name cache
            _globalDisplayNameCache.UnregisterGridClient(Guid.Parse(_accountId));
            _logger.LogDebug("Unregistered grid client {AccountId} from global display name cache (logout)", _accountId);
            
            // Also unregister from master display name service
            _masterDisplayNameService.UnregisterGridClient(Guid.Parse(_accountId));
            _logger.LogDebug("Unregistered grid client {AccountId} from master display name service (logout)", _accountId);
            
            // Clean up all cached data
            _nearbyAvatars.Clear();
            _proximityAlertedAvatars.Clear(); // Clear proximity tracking on logout
            _previousAvatarPositions.Clear(); // Clear position tracking on logout
            _chatSessions.Clear();
            _groups.Clear();
            _coarseLocationAvatars.Clear(); // Clear coarse location avatars
            _avatarSimHandles.Clear(); // Clear avatar sim handles
            _recentNameRequests.Clear(); // Clear name request tracking
            
            // Remove account from stats service region tracking
            _statsService.RemoveAccountRegion(Guid.Parse(_accountId));
            
            // Stop any periodic timers
            _displayNameRefreshTimer?.Dispose();
            _displayNameRefreshTimer = null;
            
            _logger.LogInformation("Completed logout cleanup for account {AccountId}", _accountId);
        }

        private void Network_SimChanged(object? sender, SimChangedEventArgs e)
        {
            AccountInfo.CurrentRegion = _client.Network.CurrentSim?.Name;
            UpdateRegionInfo();
            _nearbyAvatars.Clear(); // Clear avatars from previous sim
            _proximityAlertedAvatars.Clear(); // Clear proximity tracking for new sim
            _previousAvatarPositions.Clear(); // Clear position tracking for new sim
            _coarseLocationAvatars.Clear(); // Clear coarse location avatars from previous sim
            _avatarSimHandles.Clear(); // Clear sim handles
            _recentNameRequests.Clear(); // Clear name request tracking for new sim
            
            // Proactively start loading display names for the new region
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait for avatars to start appearing in the new sim
                    await Task.Delay(3000);
                    
                    // FIXED: Record our own avatar as a visitor to the new region after teleport/sim change
                    await RecordOwnAvatarAsync();
                    
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
                // Update the AccountInfo.CurrentRegion immediately for in-memory access
                AccountInfo.CurrentRegion = _client.Network.CurrentSim.Name;
                
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
            else
            {
                // Clear region info when not connected to any sim
                AccountInfo.CurrentRegion = null;
            }
        }

        private async void Objects_AvatarUpdate(object? sender, AvatarUpdateEventArgs e)
        {
            if (_disposed || !_client.Network.Connected)
            {
                _logger.LogDebug("Skipping avatar update - disposed: {Disposed}, connected: {Connected}", _disposed, _client.Network.Connected);
                return;
            }

            if (e.Avatar.ID == _client.Self.AgentID) return; // Skip self

            var isNewAvatar = !_nearbyAvatars.ContainsKey(e.Avatar.ID);
            
            _logger.LogDebug("Avatar update received for {AccountId}: {AvatarId} ({AvatarName}) - New: {IsNew}", 
                _accountId, e.Avatar.ID, e.Avatar.Name ?? "Unknown", isNewAvatar);
            _nearbyAvatars.AddOrUpdate(e.Avatar.ID, e.Avatar, (key, oldValue) => e.Avatar);

            // Mark corresponding coarse location avatar as detailed if it exists
            if (_coarseLocationAvatars.TryGetValue(e.Avatar.ID, out var coarseAvatar))
            {
                coarseAvatar.IsDetailed = true;
                coarseAvatar.Position = GetAvatarActualPosition(e.Avatar); // Update position with proper calculation
                coarseAvatar.LastUpdate = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(e.Avatar.Name))
                {
                    coarseAvatar.Name = e.Avatar.Name;
                }
            }

            // Get display name for the avatar through global cache
            var avatarName = GetBestAvatarName(e.Avatar.ID, e.Avatar.Name);
            var displayName = avatarName;
            
            try
            {
                // IMMEDIATELY request name if not in cache (like Radegast does)
                var cachedName = _globalDisplayNameCache.GetCachedDisplayName(e.Avatar.ID.ToString(), NameDisplayMode.Smart);
                if (string.IsNullOrEmpty(cachedName) || cachedName == "Loading..." || 
                    cachedName == "???" || cachedName == avatarName)
                {
                    // Use deduplicated request to prevent cache bloat
                    RequestAvatarNamesWithDeduplication(e.Avatar.ID);
                    
                    displayName = avatarName; // Use fallback for now
                }
                else
                {
                    displayName = cachedName;
                }
                
                // Also try async request for future updates
                displayName = await _globalDisplayNameCache.GetDisplayNameAsync(e.Avatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting display name for avatar {AvatarId}, using fallback", e.Avatar.ID);
                displayName = avatarName;
            }

            // Calculate actual avatar position accounting for seating
            var actualPosition = GetAvatarActualPosition(e.Avatar);
            var distance = Calculate3DDistance(GetOurActualPosition(), actualPosition);

            var avatarDto = new AvatarDto
            {
                Id = e.Avatar.ID.ToString(),
                Name = avatarName,
                DisplayName = displayName,
                Distance = distance,
                Status = "Online",
                AccountId = Guid.Parse(_accountId),
                Position = new PositionDto
                {
                    X = actualPosition.X,
                    Y = actualPosition.Y,
                    Z = actualPosition.Z
                }
            };

            // Check for proximity-based IM relay (0.25m threshold)
            // Now checks both new avatars AND existing avatars that move closer
            var previousDistance = float.MaxValue;
            if (_previousAvatarPositions.TryGetValue(e.Avatar.ID, out var previousPosition))
            {
                var ourPosition = GetOurActualPosition();
                previousDistance = Calculate3DDistance(ourPosition, previousPosition);
            }
            
            // Store current position for next time
            _previousAvatarPositions.AddOrUpdate(e.Avatar.ID, actualPosition, (key, old) => actualPosition);
            
            // Trigger proximity alert if:
            // 1. New avatar within 1.5m, OR
            // 2. Existing avatar that moved from >1.5m to <=1.5m
            if (distance <= 1.5f && (isNewAvatar || previousDistance > 1.5f))
            {
                _ = Task.Run(async () => await HandleProximityWarning(e.Avatar.ID.ToString()));
            }
            else if (distance > 4.0f)
            {
                // If avatar has moved more than 4m away, clear proximity alert so we can alert again if they return
                _proximityAlertedAvatars.TryRemove(e.Avatar.ID, out _);
            }

            _logger.LogDebug("Invoking AvatarAdded event for {AccountId}: {AvatarId} ({AvatarName}) - Distance: {Distance}m, Subscribers: {SubscriberCount}", 
                _accountId, e.Avatar.ID, avatarDto.Name, distance, AvatarAdded?.GetInvocationList().Length ?? 0);
            
            AvatarAdded?.Invoke(this, avatarDto);
            UpdateRegionInfo();
            
            // Record visitor statistics with delay for name resolution (fire and forget to avoid blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait a few seconds to allow name resolution to complete
                    await Task.Delay(3000);
                    
                    // Get the most current display name after delay
                    var resolvedDisplayName = await _globalDisplayNameCache.GetDisplayNameAsync(e.Avatar.ID.ToString(), NameDisplayMode.Smart, avatarName);
                    
                    // Use resolved name if available, otherwise fall back to original
                    var finalDisplayName = (!string.IsNullOrEmpty(resolvedDisplayName) && resolvedDisplayName != "Loading...") 
                        ? resolvedDisplayName 
                        : displayName;
                    
                    await RecordVisitorStatAsync(e.Avatar.ID.ToString(), avatarName, finalDisplayName);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error recording visitor stat for avatar {AvatarId}", e.Avatar.ID);
                }
            });
            
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
                
                // Clear proximity alert tracking so we can alert again if they return
                _proximityAlertedAvatars.TryRemove(avatarToRemove.ID, out _);
                
                // Clear previous position tracking
                _previousAvatarPositions.TryRemove(avatarToRemove.ID, out _);
                
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
                // Get our current position for distance calculations (accounting for sitting)
                var ourPosition = GetOurActualPosition();
                var agentPosition = e.Simulator.AvatarPositions.TryGetValue(_client.Self.AgentID, out var ourPos)
                    ? ToVector3D(e.Simulator.Handle, ourPosition) // Use our corrected position
                    : _client.Self.GlobalPosition;

                // Handle removed avatars
                var removedAvatars = new List<UUID>();
                foreach (var removedId in e.RemovedEntries)
                {
                    if (_coarseLocationAvatars.TryRemove(removedId, out var removed))
                    {
                        _avatarSimHandles.TryRemove(removedId, out _);
                        _proximityAlertedAvatars.TryRemove(removedId, out _);
                        _previousAvatarPositions.TryRemove(removedId, out _);
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
                    
                    // If we have detailed avatar info, use the properly calculated position
                    if (detailedAvatar != null)
                    {
                        var detailedPosition = GetAvatarActualPosition(detailedAvatar);
                        pos = detailedPosition;
                    }
                    else
                    {
                        // Handle altitude issues for coarse-only avatars (SecondLife uses 1020f, OpenSim uses 0f for high altitudes)
                        bool unknownAltitude = _client.Settings.LOGIN_SERVER.Contains("secondlife") ? pos.Z == 1020f : pos.Z == 0f;
                        if (unknownAltitude)
                        {
                            // For coarse-only avatars with unknown altitude, we can't do much more
                            // The position from CoarseLocationUpdate is the best we have
                        }
                    }

                    // Calculate distance from agent
                    var distance = Vector3d.Distance(ToVector3D(e.Simulator.Handle, pos), agentPosition);
                    
                    // Apply maximum distance filter (362m = corner to corner of sim)
                    // if (distance > MAX_DISTANCE)
                    //    continue;

                    // Update or create coarse location avatar
                    var avatarName = GetBestAvatarName(avatarPos.Key, detailedAvatar?.Name);
                    var isNewCoarseAvatar = !_coarseLocationAvatars.ContainsKey(avatarPos.Key);
                    
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
                        
                        // IMMEDIATELY request name for new coarse avatar (like Radegast does)
                        if (avatarName.StartsWith("Resolving...") || avatarName == "Loading..." || avatarName == "???")
                        {
                            // Use deduplicated request to prevent cache bloat
                            RequestAvatarNamesWithDeduplication(avatarPos.Key);
                        }
                    }

                    // Check for proximity-based IM relay (0.25m threshold)
                    // Only check if this avatar is not already detailed (to avoid duplicate alerts)
                    if (!_nearbyAvatars.ContainsKey(avatarPos.Key))
                    {
                        var previousCoarseDistance = float.MaxValue;
                        if (_previousAvatarPositions.TryGetValue(avatarPos.Key, out var previousCoarsePosition))
                        {
                            var currentOurPosition = GetOurActualPosition();
                            previousCoarseDistance = Calculate3DDistance(currentOurPosition, previousCoarsePosition);
                        }
                        
                        // Store current position for next time
                        _previousAvatarPositions.AddOrUpdate(avatarPos.Key, pos, (key, old) => pos);
                        
                        // Trigger proximity alert for coarse avatars that move into range
                        if (distance <= 1.5 && (isNewCoarseAvatar || previousCoarseDistance > 1.5))
                        {
                            _ = Task.Run(async () => await HandleProximityWarning(avatarPos.Key.ToString()));
                        }
                        else if (distance > 4.0)
                        {
                            // If avatar has moved more than 1m away, clear proximity alert so we can alert again if they return
                            _proximityAlertedAvatars.TryRemove(avatarPos.Key, out _);
                            _logger.LogDebug("Coarse avatar {AvatarName} ({AvatarId}) moved away (distance={Distance:F2}m), cleared proximity alert", 
                                avatarName, avatarPos.Key, distance);
                        }
                    }
                        
                    // Record visitor statistics for new coarse location avatars (fire and forget)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RecordVisitorStatAsync(avatarPos.Key.ToString(), avatarName, null);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error recording visitor stat for coarse avatar {AvatarId}", avatarPos.Key);
                        }
                    });
                    
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
                    _proximityAlertedAvatars.TryRemove(removeId, out _);
                    _previousAvatarPositions.TryRemove(removeId, out _);
                    removedAvatars.Add(removeId);
                }

                // Trigger avatar removed events for all removed avatars
                foreach (var removedId in removedAvatars)
                {
                    // Clear proximity alert tracking so we can alert again if they return
                    _proximityAlertedAvatars.TryRemove(removedId, out _);
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

            // Skip processing our own messages to avoid double logging
            // Our outgoing messages are already processed in SendChat()
            if (e.SourceID == _client.Self.AgentID)
            {
                _logger.LogDebug("Skipping own chat message echo from server to avoid double logging");
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
            

            
            var chatMessage = new ChatMessageDto
            {
                AccountId = Guid.Parse(_accountId),
                SenderName = senderDisplayName,
                Message = e.Message, // Use the raw message - URL processing will be handled by the pipeline
                ChatType = e.Type.ToString(),
                Channel = "0", // Default channel for local chat
                Timestamp = DateTime.UtcNow,
                RegionName = _client.Network.CurrentSim?.Name,
                SenderId = e.SourceID.ToString(),
                SessionId = "local-chat"
            };

            // Use unified chat processing service instead of separate processing
            _ = Task.Run(async () =>
            {
                try
                {
                    await _chatProcessingService.ProcessChatMessageAsync(chatMessage, Guid.Parse(_accountId));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in unified chat processing for message from {SenderName}", senderDisplayName);
                }
            });

            // Still fire the event for compatibility with existing event handlers
            ChatReceived?.Invoke(this, chatMessage);
        }

        private async void Self_IM(object? sender, InstantMessageEventArgs e)
        {
            // DEBUG: Log all incoming IM details to troubleshoot IM relay
            _logger.LogDebug("INCOMING IM DEBUG - Dialog: {Dialog}, FromAgent: {FromAgent} ({FromAgentID}), IMSessionID: {IMSessionID}, Message: {Message}", 
                e.IM.Dialog, e.IM.FromAgentName, e.IM.FromAgentID, e.IM.IMSessionID, e.IM.Message);
            
            // First, check if this is a notice that needs special handling
            if (e.IM.Dialog == InstantMessageDialog.GroupNotice ||
                e.IM.Dialog == InstantMessageDialog.GroupNoticeRequested)
            {
                var processedNotice = await _noticeService.ProcessIncomingNoticeAsync(Guid.Parse(_accountId), e.IM);
                
                // Auto-acknowledge notices that require acknowledgment to Second Life
                if (processedNotice != null && processedNotice.RequiresAcknowledgment)
                {
                    try
                    {
                        // Send acknowledgment back to Second Life
                        // Based on Radegast implementation, we need to send an IM response
                        var hasAttachment = processedNotice.HasAttachment;
                        var dialog = InstantMessageDialog.MessageFromAgent; // Default dialog
                        
                        // Prepare the binary bucket for attachment acceptance (if needed)
                        byte[] binaryBucket = new byte[0];
                        if (hasAttachment && !string.IsNullOrEmpty(processedNotice.AttachmentType))
                        {
                            // Try to find appropriate inventory folder for the attachment type
                            if (Enum.TryParse<AssetType>(processedNotice.AttachmentType, out var assetType))
                            {
                                var destinationFolderID = _client.Inventory.FindFolderForType(assetType);
                                binaryBucket = destinationFolderID.GetBytes();
                                dialog = InstantMessageDialog.GroupNoticeInventoryAccepted;
                            }
                        }
                        
                        // Send the acknowledgment IM to Second Life
                        _client.Self.InstantMessage(
                            _client.Self.Name,
                            e.IM.FromAgentID,
                            string.Empty,
                            e.IM.IMSessionID,
                            dialog,
                            InstantMessageOnline.Offline,
                            _client.Self.SimPosition,
                            _client.Network.CurrentSim?.RegionID ?? UUID.Zero,
                            binaryBucket
                        );
                        
                        _logger.LogInformation("Auto-acknowledged group notice ({Dialog}) from {FromAgent} for account {AccountId}, hasAttachment: {HasAttachment}", 
                            e.IM.Dialog, e.IM.FromAgentName, _accountId, hasAttachment);
                            
                        // Mark as acknowledged in our database
                        await _noticeService.AcknowledgeNoticeAsync(Guid.Parse(_accountId), processedNotice.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error auto-acknowledging group notice for account {AccountId}", _accountId);
                    }
                }
                
                return; // Don't process as regular IM
            }

            // Handle friendship offers
            if (e.IM.Dialog == InstantMessageDialog.FriendshipOffered)
            {
                await HandleFriendshipOfferAsync(e.IM);
                return; // Don't process as regular IM
            }

            // Handle group invitations
            if (e.IM.Dialog == InstantMessageDialog.GroupInvitation)
            {
                await HandleGroupInvitationAsync(e.IM);
                return; // Don't process as regular IM
            }

            // Handle region notices from Second Life system
            // But filter out status-related messages
            if (e.IM.Dialog == InstantMessageDialog.MessageFromAgent && e.IM.FromAgentName == "Second Life")
            {
                // Check if this is actually a status message that shouldn't be processed as a notice
                if (!IsStatusOrFriendshipMessage(e.IM))
                {
                    await _noticeService.ProcessIncomingNoticeAsync(Guid.Parse(_accountId), e.IM);
                    return; // Don't process as regular IM
                }
                else
                {
                    // This is a status/friendship message, let it fall through to normal IM processing
                    // or just ignore it entirely if it's not meant for chat
                    _logger.LogDebug("Received status/friendship message from Second Life, not processing as notice: {Message}", e.IM.Message);
                    return; // Don't process these at all
                }
            }

            // Handle friendship acceptance/decline notifications
            if (e.IM.Dialog == InstantMessageDialog.FriendshipAccepted)
            {
                _logger.LogInformation("Friendship accepted from {FromAgentName} for account {AccountId}", e.IM.FromAgentName, _accountId);
                // The Friends_FriendshipResponse event will handle the notification
                return;
            }
            
            if (e.IM.Dialog == InstantMessageDialog.FriendshipDeclined)
            {
                _logger.LogInformation("Friendship declined by {FromAgentName} for account {AccountId}", e.IM.FromAgentName, _accountId);
                // The Friends_FriendshipResponse event will handle the notification
                return;
            }

            // Filter out typing indicators and other non-chat dialogs (except teleport requests which we handle)
            if (e.IM.Dialog == InstantMessageDialog.StartTyping ||
                e.IM.Dialog == InstantMessageDialog.StopTyping ||
                e.IM.Dialog == InstantMessageDialog.MessageBox ||
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

            // Handle teleport requests (offers)
            if (e.IM.Dialog == InstantMessageDialog.RequestTeleport)
            {
                // Check if there are any active web connections for this account
                var hasActiveConnections = _connectionTrackingService.HasActiveConnections(Guid.Parse(_accountId));
                
                if (!hasActiveConnections)
                {
                    // No web clients connected - auto-decline the teleport request immediately
                    _logger.LogInformation("Auto-declining teleport request for account {AccountId} - no active web connections. Request from {FromAgentName} ({FromAgentId})", 
                        _accountId, e.IM.FromAgentName, e.IM.FromAgentID);
                    
                    // Send decline response directly to Second Life
                    _client.Self.TeleportLureRespond(e.IM.FromAgentID, e.IM.IMSessionID, false);
                    return;
                }

                // Active web connections exist - process the teleport request normally
                await _teleportRequestService.HandleTeleportRequestAsync(
                    Guid.Parse(_accountId), 
                    e.IM.FromAgentID, 
                    e.IM.FromAgentName, 
                    e.IM.Message, 
                    e.IM.IMSessionID);
                return;
            }

            string sessionId;
            string sessionName;
            string chatType;
            string targetId;

            // Get display name for the sender using global cache first
            var senderDisplayName = await _globalDisplayNameCache.GetDisplayNameAsync(e.IM.FromAgentID.ToString(), NameDisplayMode.Smart, e.IM.FromAgentName);
            
            // If the display name is invalid, use the fallback name from the message
            if (string.IsNullOrWhiteSpace(senderDisplayName) || senderDisplayName == "Loading..." || senderDisplayName == "???")
            {
                senderDisplayName = !string.IsNullOrWhiteSpace(e.IM.FromAgentName) ? e.IM.FromAgentName : GetBestAvatarName(e.IM.FromAgentID, e.IM.FromAgentName);
                
                // Special handling for relay messages - check if this is from a known relay avatar
                // and use cached name if available to prevent "Unknown" from appearing
                if (senderDisplayName.StartsWith("Resolving...") || senderDisplayName == "Unknown User")
                {
                    try
                    {
                        var cachedRelayName = await _displayNameService.GetDisplayNameAsync(Guid.Parse(_accountId), e.IM.FromAgentID.ToString());
                        if (!string.IsNullOrWhiteSpace(cachedRelayName) && 
                            cachedRelayName != "Loading..." && 
                            cachedRelayName != "???" &&
                            !cachedRelayName.StartsWith("Resolving..."))
                        {
                            senderDisplayName = cachedRelayName;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error getting cached relay name for IM sender {FromAgentId}", e.IM.FromAgentID);
                    }
                }
                
                // Proactively request a proper display name for future use
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

            // Check for Corrade commands in IMs (for external chat interfaces)
            if (_corradeService.IsEnabled &&
                _corradeService.IsWhisperCorradeCommand(e.IM.Message))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Check if this account should process Corrade commands
                        var shouldProcess = await _corradeService.ShouldProcessWhispersForAccountAsync(Guid.Parse(_accountId));
                        if (!shouldProcess)
                        {
                            _logger.LogDebug("Corrade command in IM ignored - account {AccountId} not configured for Corrade processing", _accountId);
                            return;
                        }

                        _logger.LogInformation("Processing Corrade command from IM: {SenderName} -> {Message}", senderDisplayName, e.IM.Message);

                        var result = await _corradeService.ProcessWhisperCommandAsync(
                            Guid.Parse(_accountId), 
                            e.IM.FromAgentID.ToString(), 
                            senderDisplayName, 
                            e.IM.Message);
                        
                        _logger.LogInformation("Processed Corrade IM command from {SenderName}: Success={Success}, Message={Message}", 
                            senderDisplayName, result.Success, result.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing Corrade IM command from {SenderName}", senderDisplayName);
                    }
                });
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
                        
                        _logger.LogDebug("Using cached group name {GroupName} for group {GroupId} (loaded from persistent cache)", cachedGroupName, e.IM.IMSessionID);
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
                    _logger.LogDebug("MessageFromAgent - Checking if IMSessionID {IMSessionID} is a group...", e.IM.IMSessionID);
                    
                    var groupNameFromAgent = await _groupService.GetGroupNameAsync(Guid.Parse(_accountId), 
                        e.IM.IMSessionID.ToString(), string.Empty);
                    
                    _logger.LogDebug("Group name lookup result: '{GroupName}' for IMSessionID {IMSessionID}", groupNameFromAgent ?? "null", e.IM.IMSessionID);
                    
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
                    // Check if this might be an individual IM with a different dialog type
                    // Individual IMs can come through as various dialog types depending on the sender and context
                    
                    // Skip certain dialog types that are definitely not individual IMs
                    if (e.IM.Dialog == InstantMessageDialog.StartTyping ||
                        e.IM.Dialog == InstantMessageDialog.StopTyping ||
                        e.IM.Dialog == InstantMessageDialog.GroupNotice ||
                        e.IM.Dialog == InstantMessageDialog.GroupNoticeRequested ||
                        e.IM.Dialog == InstantMessageDialog.RequestTeleport ||
                        e.IM.Dialog == InstantMessageDialog.MessageBox ||
                        e.IM.Dialog == InstantMessageDialog.GroupInvitation ||
                        e.IM.Dialog == InstantMessageDialog.InventoryOffered ||
                        e.IM.Dialog == InstantMessageDialog.InventoryAccepted ||
                        e.IM.Dialog == InstantMessageDialog.InventoryDeclined ||
                        e.IM.Dialog == InstantMessageDialog.FriendshipOffered ||
                        e.IM.Dialog == InstantMessageDialog.FriendshipAccepted ||
                        e.IM.Dialog == InstantMessageDialog.FriendshipDeclined)
                    {
                        _logger.LogDebug("Skipping system IM dialog type: {Dialog} from {SenderName} ({SenderId})", 
                            e.IM.Dialog, senderDisplayName, e.IM.FromAgentID);
                        return;
                    }
                    
                    // For other dialog types, treat as potential individual IMs
                    _logger.LogDebug("TREATING AS INDIVIDUAL IM - Dialog: {Dialog} from {SenderName} ({SenderId}) - Message: {Message}", 
                        e.IM.Dialog, senderDisplayName, e.IM.FromAgentID, e.IM.Message);
                    
                    // Individual IM (with non-standard dialog type)
                    sessionId = $"im-{e.IM.FromAgentID}";
                    sessionName = senderDisplayName;
                    chatType = "IM";
                    targetId = e.IM.FromAgentID.ToString();
                    break;
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
                Message = e.IM.Message, // Use the raw message - URL processing will be handled by the pipeline
                ChatType = chatType,
                Channel = chatType,
                Timestamp = DateTime.UtcNow,
                RegionName = _client.Network.CurrentSim?.Name,
                SenderId = e.IM.FromAgentID.ToString(),
                SessionId = sessionId,
                SessionName = sessionName,
                TargetId = targetId
            };

            // Process IM through unified chat processing service as well
            _ = Task.Run(async () =>
            {
                try
                {
                    await _chatProcessingService.ProcessChatMessageAsync(chatMessage, Guid.Parse(_accountId));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in unified chat processing for IM from {SenderName}", finalSenderName);
                }
            });

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
                _proximityAlertedAvatars.Clear(); // Clear proximity alert tracking for new location
                _previousAvatarPositions.Clear(); // Clear position tracking for new location
                
                // Reset presence status on teleport completion to prevent state desync
                ResetPresenceStatus();
                
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
            // Filter out relay message warnings for offline users
            if (ShouldIgnoreAlertMessage(e.Message))
            {
                _logger.LogDebug("Ignoring filtered alert message for account {AccountId}: {Message}", _accountId, e.Message);
                return;
            }
            
            // Check for region restart messages and handle them specially
            await HandleRegionRestartIfDetected(e.Message);
            
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

        /// <summary>
        /// Detects region restart messages and handles them by notifying relay avatar and initiating logout
        /// </summary>
        /// <param name="message">The alert message to check for restart notifications</param>
        private async Task HandleRegionRestartIfDetected(string message)
        {
            try
            {
                // Check for region restart message pattern: 
                // "Region "{region_name}" will restart in 5 minutes. If you stay in this region, you will be logged out"
                var restartPattern = @"Region\s+""(.+?)""\s+will\s+restart\s+in\s+\d+\s+minutes?\.\s+If\s+you\s+stay\s+in\s+this\s+region,?\s+you\s+will\s+be\s+logged\s+out";
                var match = System.Text.RegularExpressions.Regex.Match(message, restartPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                if (match.Success)
                {
                    var regionName = match.Groups[1].Value;
                    _logger.LogWarning("Region restart detected for region '{RegionName}' - Account {AccountId}", regionName, _accountId);
                    
                    // Check if relay avatar is configured
                    if (!string.IsNullOrEmpty(AccountInfo.AvatarRelayUuid) && 
                        AccountInfo.AvatarRelayUuid != "00000000-0000-0000-0000-000000000000")
                    {
                        await SendRelayRestartNotification(regionName, message);
                    }
                    
                    // Initiate logout and cleanup after a short delay to ensure relay notification is sent
                    _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(async _ =>
                    {
                        try
                        {
                            _logger.LogInformation("Initiating logout for account {AccountId} due to region restart in {RegionName}", 
                                _accountId, regionName);
                            
                            // Check if sitting and stand up before logout
                            if (IsSitting)
                            {
                                _logger.LogInformation("Avatar is sitting, standing up before region restart logout for account {AccountId}", _accountId);
                                SetSitting(false); // Stand up and stop animations
                                
                                // Small delay to allow the stand-up command to be processed
                                await Task.Delay(500);
                            }
                            
                            await DisconnectAsync();
                        }
                        catch (Exception logoutEx)
                        {
                            _logger.LogError(logoutEx, "Error during automatic logout for region restart - Account {AccountId}", _accountId);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling region restart detection for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Sends an IM notification to the relay avatar about the impending region restart
        /// </summary>
        /// <param name="regionName">Name of the region that's restarting</param>
        /// <param name="originalMessage">The original restart message from the system</param>
        private async Task SendRelayRestartNotification(string regionName, string originalMessage)
        {
            try
            {
                if (!UUID.TryParse(AccountInfo.AvatarRelayUuid, out UUID relayUuid))
                {
                    _logger.LogWarning("Invalid relay UUID '{RelayUuid}' for account {AccountId}", 
                        AccountInfo.AvatarRelayUuid, _accountId);
                    return;
                }
                
                var notificationMessage = $"[REGION RESTART] Avatar {AccountInfo.FirstName} {AccountInfo.LastName} in region '{regionName}' - {originalMessage}. Auto-logging out to avoid disconnection.";
                
                _logger.LogInformation("Sending region restart notification to relay avatar {RelayUuid} for account {AccountId}", 
                    relayUuid, _accountId);
                
                // Send the IM to the relay avatar
                await Task.Run(() => _client.Self.InstantMessage(relayUuid, notificationMessage));
                
                _logger.LogInformation("Successfully sent region restart notification to relay avatar for account {AccountId}", _accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending region restart notification to relay avatar for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Determines if an alert message should be ignored to prevent spam from common system warnings
        /// </summary>
        /// <param name="message">The alert message to check</param>
        /// <returns>True if the message should be ignored, false if it should be processed</returns>
        private bool ShouldIgnoreAlertMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            var msg = message.Trim();

            // Filter out offline user warnings that appear when sending IMs to offline users
            // Common patterns include:
            // - "User not online - message will be stored and delivered later."
            // - "User not online - your message will be stored and delivered when they return"
            if (msg.Contains("User not online", StringComparison.OrdinalIgnoreCase) &&
                (msg.Contains("message will be stored", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("delivered later", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("delivered when", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Filter out similar offline message patterns
            if (msg.Contains("not online", StringComparison.OrdinalIgnoreCase) &&
                msg.Contains("stored", StringComparison.OrdinalIgnoreCase) &&
                msg.Contains("delivered", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Filter out "Message queued for delivery" type messages
            if (msg.Contains("message", StringComparison.OrdinalIgnoreCase) &&
                msg.Contains("queued", StringComparison.OrdinalIgnoreCase) &&
                msg.Contains("delivery", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
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

        /// <summary>
        /// Handle friendship response events (when someone accepts/declines our friendship offer or we accept/decline theirs)
        /// </summary>
        private async void Friends_FriendshipResponse(object? sender, FriendshipResponseEventArgs e)
        {
            try
            {
                var agentName = await _nameResolutionService.ResolveAgentNameAsync(Guid.Parse(_accountId), e.AgentID);
                
                if (e.Accepted)
                {
                    _logger.LogInformation("Friendship accepted with {AgentName} ({AgentId}) for account {AccountId}", 
                        agentName, e.AgentID, _accountId);
                    
                    // Send a notification to the web UI
                    var chatMessage = new ChatMessageDto
                    {
                        SenderName = "System",
                        Message = $"You are now friends with {agentName}.",
                        Timestamp = DateTime.UtcNow,
                        ChatType = "System",
                        SessionId = "local-chat",
                        SenderId = UUID.Zero.ToString(),
                        AccountId = Guid.Parse(_accountId),
                        SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss")
                    };
                    
                    ChatReceived?.Invoke(this, chatMessage);
                }
                else
                {
                    _logger.LogInformation("Friendship declined with {AgentName} ({AgentId}) for account {AccountId}", 
                        agentName, e.AgentID, _accountId);
                    
                    // Send a notification to the web UI
                    var chatMessage = new ChatMessageDto
                    {
                        SenderName = "System",
                        Message = $"{agentName} declined your friendship offer.",
                        Timestamp = DateTime.UtcNow,
                        ChatType = "System",
                        SessionId = "local-chat",
                        SenderId = UUID.Zero.ToString(),
                        AccountId = Guid.Parse(_accountId),
                        SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss")
                    };
                    
                    ChatReceived?.Invoke(this, chatMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling friendship response for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Handle friend coming online
        /// </summary>
        private async void Friends_FriendOnline(object? sender, FriendInfoEventArgs e)
        {
            try
            {
                var friendName = await _nameResolutionService.ResolveAgentNameAsync(Guid.Parse(_accountId), e.Friend.UUID);
                _logger.LogDebug("Friend {FriendName} came online for account {AccountId}", friendName, _accountId);
                
                // Optionally send a notification to the web UI
                var chatMessage = new ChatMessageDto
                {
                    SenderName = "System",
                    Message = $"{friendName} is now online.",
                    Timestamp = DateTime.UtcNow,
                    ChatType = "System",
                    SessionId = "local-chat",
                    SenderId = UUID.Zero.ToString(),
                    AccountId = Guid.Parse(_accountId),
                    SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss")
                };
                
                ChatReceived?.Invoke(this, chatMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling friend online event for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Handle friend going offline
        /// </summary>
        private async void Friends_FriendOffline(object? sender, FriendInfoEventArgs e)
        {
            try
            {
                var friendName = await _nameResolutionService.ResolveAgentNameAsync(Guid.Parse(_accountId), e.Friend.UUID);
                _logger.LogDebug("Friend {FriendName} went offline for account {AccountId}", friendName, _accountId);
                
                // Optionally send a notification to the web UI
                var chatMessage = new ChatMessageDto
                {
                    SenderName = "System",
                    Message = $"{friendName} is now offline.",
                    Timestamp = DateTime.UtcNow,
                    ChatType = "System",
                    SessionId = "local-chat",
                    SenderId = UUID.Zero.ToString(),
                    AccountId = Guid.Parse(_accountId),
                    SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss")
                };
                
                ChatReceived?.Invoke(this, chatMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling friend offline event for account {AccountId}", _accountId);
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
                    
                    if (!string.IsNullOrEmpty(newDisplayName) && newDisplayName != legacyName && newDisplayName != AccountInfo.DisplayName)
                    {
                        var oldDisplayName = AccountInfo.DisplayName;
                        AccountInfo.DisplayName = newDisplayName;
                        _logger.LogInformation("Updated own display name from '{OldDisplayName}' to: '{NewDisplayName}'", oldDisplayName, newDisplayName);
                        
                        // Fire event to notify listeners (like AccountService) that our display name changed
                        OwnDisplayNameChanged?.Invoke(this, newDisplayName);
                        
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
        
        private void GlobalDisplayNameCache_DisplayNameChanged(object? sender, DisplayNameChangedEventArgs e)
        {
            try
            {
                // Check if this avatar is in our nearby avatars list
                if (UUID.TryParse(e.AvatarId, out var avatarUuid) && _nearbyAvatars.TryGetValue(avatarUuid, out var avatar))
                {
                    // Create updated avatar DTO with new display name
                    var actualPosition = GetAvatarActualPosition(avatar);
                    var avatarDto = new AvatarDto
                    {
                        Id = e.AvatarId,
                        Name = e.DisplayName.LegacyFullName,
                        DisplayName = e.DisplayName.DisplayNameValue,
                        Distance = Calculate3DDistance(GetOurActualPosition(), actualPosition),
                        Status = "Online",
                        AccountId = Guid.Parse(_accountId),
                        Position = new PositionDto
                        {
                            X = actualPosition.X,
                            Y = actualPosition.Y,
                            Z = actualPosition.Z
                        }
                    };
                    
                    // Fire the avatar updated event
                    AvatarUpdated?.Invoke(this, avatarDto);
                    
                    _logger.LogDebug("Updated nearby avatar display name (global cache) for {AvatarId} to '{NewName}'", 
                        e.AvatarId, e.DisplayName.DisplayNameValue);
                }
                
                // Check if this avatar has an active IM session and update the session name
                var imSessionId = $"im-{e.AvatarId}";
                if (_chatSessions.TryGetValue(imSessionId, out var imSession))
                {
                    var oldSessionName = imSession.SessionName;
                    imSession.SessionName = e.DisplayName.DisplayNameValue;
                    ChatSessionUpdated?.Invoke(this, imSession);
                    
                    _logger.LogDebug("Updated IM session display name (global cache) for {AvatarId} from '{OldName}' to '{NewName}'", 
                        e.AvatarId, oldSessionName, e.DisplayName.DisplayNameValue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling global display name change for avatar {AvatarId}", e.AvatarId);
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
                    var actualPosition = GetAvatarActualPosition(avatar);
                    var avatarDto = new AvatarDto
                    {
                        Id = e.AvatarId,
                        Name = e.DisplayName.LegacyFullName,
                        DisplayName = e.DisplayName.DisplayNameValue,
                        Distance = Calculate3DDistance(GetOurActualPosition(), actualPosition),
                        Status = "Online",
                        AccountId = Guid.Parse(_accountId),
                        Position = new PositionDto
                        {
                            X = actualPosition.X,
                            Y = actualPosition.Y,
                            Z = actualPosition.Z
                        }
                    };
                    
                    // Fire the avatar updated event
                    AvatarUpdated?.Invoke(this, avatarDto);
                    
                    _logger.LogDebug("Updated nearby avatar display name for {AvatarId} to '{NewName}'", 
                        e.AvatarId, e.DisplayName.DisplayNameValue);
                }
                
                // Check if this avatar has an active IM session and update the session name
                var imSessionId = $"im-{e.AvatarId}";
                if (_chatSessions.TryGetValue(imSessionId, out var imSession))
                {
                    var oldSessionName = imSession.SessionName;
                    imSession.SessionName = e.DisplayName.DisplayNameValue;
                    ChatSessionUpdated?.Invoke(this, imSession);
                    
                    _logger.LogDebug("Updated IM session display name for {AvatarId} from '{OldName}' to '{NewName}'", 
                        e.AvatarId, oldSessionName, e.DisplayName.DisplayNameValue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling display name change for avatar {AvatarId}", e.AvatarId);
            }
        }
        
        private void MasterDisplayNameService_DisplayNameChanged(object? sender, DisplayNameChangedEventArgs e)
        {
            try
            {
                // Check if this avatar is in our nearby avatars list
                if (UUID.TryParse(e.AvatarId, out var avatarUuid) && _nearbyAvatars.TryGetValue(avatarUuid, out var avatar))
                {
                    // Create updated avatar DTO with new display name
                    var actualPosition = GetAvatarActualPosition(avatar);
                    var avatarDto = new AvatarDto
                    {
                        Id = e.AvatarId,
                        Name = e.DisplayName.LegacyFullName,
                        DisplayName = e.DisplayName.DisplayNameValue,
                        Distance = Calculate3DDistance(GetOurActualPosition(), actualPosition),
                        Status = "Online",
                        AccountId = Guid.Parse(_accountId),
                        Position = new PositionDto
                        {
                            X = actualPosition.X,
                            Y = actualPosition.Y,
                            Z = actualPosition.Z
                        }
                    };
                    
                    // Fire the avatar updated event
                    AvatarUpdated?.Invoke(this, avatarDto);
                    
                    _logger.LogDebug("Updated nearby avatar display name (master service) for {AvatarId} to '{NewName}'", 
                        e.AvatarId, e.DisplayName.DisplayNameValue);
                }
                
                // Also check coarse location avatars and update them
                if (UUID.TryParse(e.AvatarId, out var coarseAvatarUuid) && _coarseLocationAvatars.TryGetValue(coarseAvatarUuid, out var coarseAvatar))
                {
                    coarseAvatar.Name = e.DisplayName.DisplayNameValue;
                    _logger.LogDebug("Updated coarse location avatar display name (master service) for {AvatarId} to '{NewName}'", 
                        e.AvatarId, e.DisplayName.DisplayNameValue);
                }
                
                // Check if this avatar has an active IM session and update the session name
                var imSessionId = $"im-{e.AvatarId}";
                if (_chatSessions.TryGetValue(imSessionId, out var imSession))
                {
                    var oldSessionName = imSession.SessionName;
                    imSession.SessionName = e.DisplayName.DisplayNameValue;
                    ChatSessionUpdated?.Invoke(this, imSession);
                    
                    _logger.LogDebug("Updated IM session display name (master service) for {AvatarId} from '{OldName}' to '{NewName}'", 
                        e.AvatarId, oldSessionName, e.DisplayName.DisplayNameValue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling master display name change for avatar {AvatarId}", e.AvatarId);
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
                // FIXED: Record our own avatar presence every 30 seconds to maintain visitor stats
                await RecordOwnAvatarAsync();

                var nearbyAvatarIds = _nearbyAvatars.Keys.Select(id => id.ToString()).ToList();
                if (nearbyAvatarIds.Count > 0)
                {
                    // Batch request display names for all nearby avatars
                    await _displayNameService.PreloadDisplayNamesAsync(Guid.Parse(_accountId), nearbyAvatarIds);
                    
                    _logger.LogDebug("Refreshed display names for {Count} nearby avatars + recorded self-presence", nearbyAvatarIds.Count);
                }
                else
                {
                    _logger.LogDebug("Recorded self-presence (no nearby avatars)");
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
        /// Gets the UUID of the object the avatar is currently sitting on, or null if not sitting on an object
        /// </summary>
        public UUID? CurrentSittingObjectUuid
        {
            get
            {
                var localId = SittingOnLocalId;
                if (localId == 0) return null;
                
                return GetObjectUUIDFromLocalID(localId);
            }
        }

        /// <summary>
        /// Gets whether the avatar is currently in away mode
        /// </summary>
        public bool IsAway 
        { 
            get 
            { 
                var isAway = _client.Self.Movement.Away;
                _logger.LogDebug("Account {AccountId} IsAway check: Movement.Away = {IsAway}", _accountId, isAway);
                return isAway;
            } 
        }

        /// <summary>
        /// Gets the current presence status based on avatar state
        /// </summary>
        public PresenceStatus GetCurrentPresenceStatus()
        {
            if (!_client.Network.Connected)
                return PresenceStatus.Online;

            // Check for away status first (Movement.Away is reliable)
            if (_client.Self.Movement.Away)
                return PresenceStatus.Away;

            // For busy status, we need to check if the busy animation is currently playing
            // This is more complex since we need to track it through our animation system
            // For now, this will be handled by the PresenceService tracking
            return PresenceStatus.Online;
        }
        
        /// <summary>
        /// Event fired when sitting state changes
        /// </summary>
        public event EventHandler<SitStateChangedEventArgs>? SitStateChanged;
        
        /// <summary>
        /// Attempts to sit on the specified object or ground
        /// </summary>
        /// <param name="sit">True to sit down, false to stand up</param>
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
                        
                        // Track the sit target for auto-sit functionality with presence status (only if auto-sit is enabled)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _autoSitService.UpdateLastSitTargetWithPresenceIfEnabledAsync(Guid.Parse(_accountId), target.ToString(), _presenceService);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error updating last sit target with presence for account {AccountId}", _accountId);
                            }
                        });
                    }
                }
                else
                {
                    // Stand up
                    _client.Self.Stand();
                    
                    // Stop all animations that aren't basic avatar animations
                    // This fixes the issue where object animations continue after standing up
                    StopAllAnimations();
                    
                    _logger.LogInformation("Requested to stand up for account {AccountId} and stopped all animations", _accountId);
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
        /// Stop all animations except for known system animations
        /// This is used when standing up to stop object animations that would otherwise continue
        /// Based on Radegast's implementation to handle animation persistence issues
        /// Also resets away/busy status to prevent internal state desync
        /// </summary>
        public void StopAllAnimations()
        {
            try
            {
                if (!_client.Network.Connected)
                {
                    _logger.LogWarning("Cannot stop animations - not connected");
                    return;
                }

                var stop = new Dictionary<UUID, bool>();

                // Get all currently signaled animations
                _client.Self.SignaledAnimations.ForEach(anim =>
                {
                    // Only stop animations that are not known system animations
                    if (!IsKnownSystemAnimation(anim))
                    {
                        stop.Add(anim, false); // false = stop animation
                    }
                });

                if (stop.Count > 0)
                {
                    _client.Self.Animate(stop, true);
                    _logger.LogInformation("Stopped {Count} non-system animations for account {AccountId}", stop.Count, _accountId);
                }
                else
                {
                    _logger.LogDebug("No non-system animations to stop for account {AccountId}", _accountId);
                }
                
                // Reset away/busy status to prevent internal state desync
                // This ensures that stopping animations also clears the internal tracking
                ResetPresenceStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping animations for account {AccountId}", _accountId);
            }
        }
        
        /// <summary>
        /// Resets the presence status to Online, clearing any away/busy status
        /// Used when animations are stopped (stand up, teleport) to keep internal state in sync
        /// </summary>
        private void ResetPresenceStatus()
        {
            try
            {
                if (Guid.TryParse(_accountId, out var accountGuid))
                {
                    // Reset both away and busy status to false/online
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _presenceService.SetAwayAsync(accountGuid, false);
                            await _presenceService.SetBusyAsync(accountGuid, false);
                            _logger.LogInformation("Reset presence status to Online for account {AccountId} after stopping animations", _accountId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error resetting presence status for account {AccountId}", _accountId);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing account ID for presence reset: {AccountId}", _accountId);
            }
        }
        
        /// <summary>
        /// Reset presence status specifically for disconnect scenarios where we need immediate cleanup
        /// </summary>
        private void ResetPresenceStatusOnDisconnect()
        {
            try
            {
                if (Guid.TryParse(_accountId, out var accountGuid))
                {
                    // Fire and forget - reset presence status immediately for disconnect
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _presenceService.SetAwayAsync(accountGuid, false);
                            await _presenceService.SetBusyAsync(accountGuid, false);
                            _logger.LogInformation("Reset presence status to Offline for account {AccountId} on disconnect", _accountId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error resetting presence status on disconnect for account {AccountId}", _accountId);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing account ID for presence reset on disconnect: {AccountId}", _accountId);
            }
        }
        
        /// <summary>
        /// Determines a user-friendly disconnect reason based on the network disconnect reason
        /// </summary>
        private string DetermineDisconnectReason(NetworkManager.DisconnectType reason)
        {
            return reason switch
            {
                NetworkManager.DisconnectType.ServerInitiated => "Server Restart/Maintenance",
                NetworkManager.DisconnectType.ClientInitiated => "Manual Logout",
                NetworkManager.DisconnectType.NetworkTimeout => "Network Timeout",
                NetworkManager.DisconnectType.SimShutdown => "Region Shutdown",
                _ => "Connection Lost"
            };
        }
        
        /// <summary>
        /// Touch an object in Second Life by UUID
        /// This simulates clicking on an object and is useful for re-triggering dialogs from seated objects
        /// Based on Radegast's touch implementation using Grab/DeGrab sequence
        /// </summary>
        /// <param name="objectId">UUID of the object to touch</param>
        /// <returns>True if touch request was sent successfully</returns>
        public bool TouchObject(UUID objectId)
        {
            try
            {
                if (!_client.Network.Connected)
                {
                    _logger.LogWarning("Cannot touch object - not connected");
                    return false;
                }

                // Check if object exists in current simulator
                if (!IsObjectInRegion(objectId))
                {
                    _logger.LogWarning("Cannot touch object {ObjectId} - object not found in current region", objectId);
                    return false;
                }

                // Get the primitive/object to find its LocalID
                var prim = _client.Network.CurrentSim?.ObjectsPrimitives.Values.FirstOrDefault(p => p.ID == objectId);
                if (prim == null)
                {
                    _logger.LogWarning("Cannot touch object {ObjectId} - primitive not found", objectId);
                    return false;
                }

                // Check if the object is touchable
                if ((prim.Flags & PrimFlags.Touch) == 0)
                {
                    _logger.LogInformation("Object {ObjectId} ({ObjectName}) may not be touchable - no touch flag set, but attempting anyway", 
                        objectId, prim.Properties?.Name ?? "Unknown");
                }

                // Use the Radegast approach: Grab + DeGrab sequence to trigger touch events
                // This is more reliable than using Touch() alone, especially for scripted objects
                _client.Self.Grab(prim.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                
                // Small delay to ensure the grab is registered
                System.Threading.Thread.Sleep(100);
                
                _client.Self.DeGrab(prim.LocalID, Vector3.Zero, Vector3.Zero, 0, Vector3.Zero, Vector3.Zero, Vector3.Zero);

                _logger.LogInformation("Touched object {ObjectId} ({ObjectName}) for account {AccountId}", 
                    objectId, prim.Properties?.Name ?? "Unknown", _accountId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error touching object {ObjectId} for account {AccountId}", objectId, _accountId);
                return false;
            }
        }
        
        /// <summary>
        /// Checks if an animation UUID is a known system animation that should not be stopped
        /// Based on OpenMetaverse's Animations class constants
        /// </summary>
        /// <param name="animationId">Animation UUID to check</param>
        /// <returns>True if this is a known system animation</returns>
        private bool IsKnownSystemAnimation(UUID animationId)
        {
            // Known system animations from OpenMetaverse.Animations
            // These are basic avatar animations that should not be stopped
            var knownAnimations = new HashSet<UUID>
            {
                new UUID("16384f8e-291f-4020-a393-2aa672699b6d"), // STAND
                new UUID("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f"), // STAND_1
                new UUID("15468e00-3400-466d-a13b-5efa71fa55d4"), // STAND_2
                new UUID("370f3a20-6ca6-9971-848c-9a01bc42ae3c"), // STAND_3
                new UUID("42b46214-4b44-79ae-deb8-0df61424ff4b"), // STAND_4
                new UUID("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0"), // WALK
                new UUID("056e8d4e-4a8b-cfbb-d8b8-0498afe9d97f"), // RUN
                new UUID("2d25f9fc-0a91-b464-9b03-cdbb69b60c26"), // FLY
                new UUID("aec4610c-757f-bc4e-c092-c6e9caf18daf"), // HOVER
                new UUID("4ae8016b-31b9-03bb-c401-b1ea941db41d"), // HOVER_UP
                new UUID("20f063ea-8306-2562-0b07-5c853b37b31e"), // HOVER_DOWN
                new UUID("62c5de58-cb33-5743-3d07-9e4cd4352864"), // LAND
                new UUID("1a5fe8ac-a804-8a5d-7cbd-56bd83184568"), // FALLDOWN
                new UUID("7a17b059-12b2-41b1-570a-186368b6aa6f"), // SIT
                new UUID("1c7600d6-661f-b87b-efe2-d7421eb93c86"), // SIT_GROUND
                new UUID("245f3c54-f1c0-bf2e-811f-46d8eeb386e7"), // SIT_GROUND_CONSTRAINED
                new UUID("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e"), // SIT_GENERIC
                new UUID("1c5c77d1-1201-b215-88d8-0519b8cc0304"), // SIT_TO_STAND
                new UUID("a8dee56f-2eae-9e7a-05a2-6fb92b97e21e"), // STAND_UP
                new UUID("6883a61a-b27b-5914-a61e-dda118a9ee2c"), // TURNLEFT
                new UUID("d2f2ee58-8ad1-06c9-d8d3-3827ba31567a"), // TURNRIGHT
                new UUID("6bd01860-4ebd-127a-bb3d-d1427e8e0c42"), // AWAY
                new UUID("efcf670c-2d18-8128-973a-034ebc806b67"), // BUSY
                new UUID("c0d38bd4-0711-a87f-2a49-8c2a4a0b9fc3"), // TYPE
                new UUID("92624d3e-1068-f1aa-a5ec-8244585193ed"), // CROUCH
                new UUID("201f3fdf-cb1f-dbec-201f-7333e328ae7c"), // CROUCHWALK
                new UUID("47f5f6fb-22e5-ae44-f871-73aaaf4a6022"), // JUMP
                new UUID("2305bd75-1ca9-b03b-1faa-b176b8a8c49e"), // PREJUMP
                new UUID("7a4e87fe-de39-6fcb-6223-024b00893244"), // SOFT_LAND
            };

            return knownAnimations.Contains(animationId);
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

        #region Visitor Statistics

        /// <summary>
        /// Records visitor statistics for an avatar sighting
        /// </summary>
        private async Task RecordVisitorStatAsync(string avatarId, string? avatarName, string? displayName)
        {
            if (_client.Network.CurrentSim == null || string.IsNullOrEmpty(_client.Network.CurrentSim.Name))
                return;

            var currentAccountId = Guid.Parse(_accountId);
            var regionName = _client.Network.CurrentSim.Name;
            var simHandle = _client.Network.CurrentSim.Handle;
            
            // Update the account's current region for tracking purposes
            _statsService.SetAccountRegion(currentAccountId, regionName, simHandle);
            
            // FIXED: Always record visitor stats regardless of other accounts in the same region
            // The StatsService already handles deduplication through its cooldown mechanism
            // and database-level uniqueness constraints. Multiple accounts in the same region
            // should all contribute to visitor tracking to ensure comprehensive coverage.
            await _statsService.RecordVisitorAsync(avatarId, regionName, simHandle, avatarName, displayName);
        }

        /// <summary>
        /// Records our own avatar as a visitor to the current region
        /// This ensures the reporting account is also included in visitor statistics
        /// </summary>
        private async Task RecordOwnAvatarAsync()
        {
            if (_client.Network.CurrentSim == null || string.IsNullOrEmpty(_client.Network.CurrentSim.Name) || !_client.Network.Connected)
                return;

            try
            {
                var ownAvatarId = _client.Self.AgentID.ToString();
                var legacyName = $"{AccountInfo.FirstName} {AccountInfo.LastName}";
                
                // Get our own display name - use the account's stored display name if available
                var displayName = !string.IsNullOrEmpty(AccountInfo.DisplayName) && AccountInfo.DisplayName != legacyName
                    ? AccountInfo.DisplayName
                    : await _globalDisplayNameCache.GetDisplayNameAsync(ownAvatarId, NameDisplayMode.Smart, legacyName);

                // If still no good display name, fall back to legacy name
                if (string.IsNullOrEmpty(displayName) || displayName == "Loading..." || displayName == "???")
                {
                    displayName = legacyName;
                }

                await RecordVisitorStatAsync(ownAvatarId, legacyName, displayName);
                _logger.LogDebug("Recorded own avatar {AvatarId} ({DisplayName}) in region {RegionName}", 
                    ownAvatarId, displayName, _client.Network.CurrentSim.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording own avatar as visitor for account {AccountId}", _accountId);
            }
        }

        /// <summary>
        /// Records all currently present avatars for visitor statistics
        /// This is useful for day boundary transitions to ensure existing avatars are counted
        /// OPTIMIZED: Now uses batching and memory-efficient processing to prevent midnight memory spikes
        /// </summary>
        public async Task RecordAllPresentAvatarsAsync()
        {
            if (_client.Network.CurrentSim == null || string.IsNullOrEmpty(_client.Network.CurrentSim.Name))
                return;

            var currentAccountId = Guid.Parse(_accountId);
            var regionName = _client.Network.CurrentSim.Name;
            var simHandle = _client.Network.CurrentSim.Handle;
            
            var recordedCount = 0;
            const int batchSize = 10; // Process in small batches to prevent memory spikes
            const int delayBetweenBatches = 50; // Small delay between batches to reduce pressure

            try
            {
                // First, record our own avatar as present
                await RecordOwnAvatarAsync();
                recordedCount++;
                _logger.LogDebug("Recorded own avatar in bulk recording for region {Region}", regionName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error recording own avatar in bulk recording");
            }

            // Collect all avatars to process (detailed + coarse, avoiding duplicates)
            var avatarsToProcess = new List<(UUID Id, string Name, bool IsDetailed)>();
            
            // Add detailed avatars
            foreach (var kvp in _nearbyAvatars)
            {
                avatarsToProcess.Add((kvp.Key, kvp.Value.Name, true));
            }

            // Add coarse location avatars that aren't already in detailed list
            foreach (var kvp in _coarseLocationAvatars)
            {
                if (!_nearbyAvatars.ContainsKey(kvp.Key))
                {
                    var fallbackName = $"Avatar {kvp.Key.ToString().Substring(0, 8)}...";
                    avatarsToProcess.Add((kvp.Key, fallbackName, false));
                }
            }

            // Pre-fetch display names in batches to reduce individual cache hits
            var avatarIds = avatarsToProcess.Select(a => a.Id.ToString()).ToList();
            var displayNameCache = new Dictionary<string, string>();
            
            for (int i = 0; i < avatarIds.Count; i += batchSize)
            {
                var batch = avatarIds.Skip(i).Take(batchSize).ToList();
                
                // Fetch display names concurrently for this batch
                var displayNameTasks = batch.Select(async avatarId =>
                {
                    try
                    {
                        var displayName = await _globalDisplayNameCache.GetCachedDisplayNameAsync(avatarId);
                        return new KeyValuePair<string, string>(avatarId, displayName?.DisplayNameValue ?? "");
                    }
                    catch
                    {
                        return new KeyValuePair<string, string>(avatarId, "");
                    }
                });
                
                var results = await Task.WhenAll(displayNameTasks);
                foreach (var result in results)
                {
                    if (!string.IsNullOrEmpty(result.Value))
                        displayNameCache[result.Key] = result.Value;
                }
                
                // Small delay to prevent overwhelming the cache
                if (i + batchSize < avatarIds.Count)
                    await Task.Delay(delayBetweenBatches);
            }

            // Process visitor recording in batches using batch API for better performance
            for (int i = 0; i < avatarsToProcess.Count; i += batchSize)
            {
                var batch = avatarsToProcess.Skip(i).Take(batchSize).ToList();
                
                // Prepare batch data
                var batchData = batch.Select(avatar =>
                {
                    var avatarId = avatar.Id.ToString();
                    var avatarName = avatar.Name;
                    
                    // Use pre-fetched display name or fallback to avatar name
                    var finalDisplayName = displayNameCache.TryGetValue(avatarId, out var cachedName) && !string.IsNullOrEmpty(cachedName)
                        ? cachedName
                        : avatarName;

                    return (AvatarId: avatarId, RegionName: regionName, SimHandle: simHandle, AvatarName: (string?)avatarName, DisplayName: (string?)finalDisplayName);
                }).ToList();
                
                try
                {
                    // Use batch recording for better database performance
                    await _statsService.RecordVisitorBatchAsync(batchData);
                    recordedCount += batchData.Count;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error in batch recording {Count} avatars, falling back to individual recording", batchData.Count);
                    
                    // Fallback to individual recording if batch fails
                    foreach (var item in batchData)
                    {
                        try
                        {
                            await _statsService.RecordVisitorAsync(item.AvatarId, item.RegionName, item.SimHandle, item.AvatarName, item.DisplayName);
                            recordedCount++;
                        }
                        catch (Exception individualEx)
                        {
                            _logger.LogTrace(individualEx, "Error recording present avatar {AvatarId} during fallback recording", item.AvatarId);
                        }
                    }
                }
                
                // Small delay between batches to reduce memory pressure
                if (i + batchSize < avatarsToProcess.Count)
                    await Task.Delay(delayBetweenBatches);
            }

            // Clear temporary collections to help GC
            avatarsToProcess.Clear();
            displayNameCache.Clear();

            if (recordedCount > 0)
            {
                _logger.LogDebug("Recorded {Count} present avatars for visitor statistics in {Region} using batched processing", recordedCount, regionName);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a ChatMessageDto with properly formatted SLT timestamps
        /// </summary>
        private ChatMessageDto CreateChatMessage(
            string senderName,
            string message,
            string chatType = "Normal",
            string? channel = null,
            string? regionName = null,
            string? senderId = null,
            string? targetId = null,
            string? sessionId = null,
            string? sessionName = null)
        {
            var timestamp = DateTime.UtcNow;
            
            return new ChatMessageDto
            {
                AccountId = Guid.Parse(_accountId),
                SenderName = senderName,
                Message = message,
                ChatType = chatType,
                Channel = channel,
                Timestamp = timestamp,
                RegionName = regionName ?? _client.Network.CurrentSim?.Name,
                SenderId = senderId,
                TargetId = targetId,
                SessionId = sessionId,
                SessionName = sessionName,
                SLTTime = _slTimeService.FormatSLT(timestamp, "HH:mm:ss"),
                SLTDateTime = _slTimeService.FormatSLTWithDate(timestamp, "MMM dd, HH:mm:ss")
            };
        }

        /// <summary>
        /// Checks if an instant message is a status or friendship related message that shouldn't be processed as a notice
        /// </summary>
        private bool IsStatusOrFriendshipMessage(InstantMessage im)
        {
            if (string.IsNullOrEmpty(im.Message))
                return false;

            var message = im.Message.ToLowerInvariant();
            
            // Check for friendship-related messages
            if (im.Dialog == InstantMessageDialog.FriendshipOffered ||
                im.Dialog == InstantMessageDialog.FriendshipAccepted ||
                im.Dialog == InstantMessageDialog.FriendshipDeclined)
            {
                return true;
            }

            // Check for status-related messages by content
            if (message.Contains("is now online") || 
                message.Contains("is now offline") ||
                message.Contains("away") ||
                message.Contains("busy") ||
                (message.Contains("status") && message.Length < 100)) // Short status messages
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handle incoming friendship offers by creating interactive notices
        /// </summary>
        private async Task HandleFriendshipOfferAsync(InstantMessage im)
        {
            try
            {
                // Create a friendship request DTO
                var requestId = Guid.NewGuid().ToString();
                var timestamp = DateTime.UtcNow;
                
                var friendshipRequest = new FriendshipRequestDto
                {
                    RequestId = requestId,
                    AccountId = Guid.Parse(_accountId),
                    FromAgentId = im.FromAgentID.ToString(),
                    FromAgentName = im.FromAgentName,
                    Message = im.Message,
                    SessionId = im.IMSessionID.ToString(),
                    ReceivedAt = timestamp,
                    IsResponded = false,
                    ExpiresAt = timestamp.AddHours(24), // Give 24 hours to respond
                    SLTReceivedAt = _slTimeService.FormatSLTWithDate(timestamp, "MMM dd, HH:mm:ss"),
                    SLTExpiresAt = _slTimeService.FormatSLTWithDate(timestamp.AddHours(24), "MMM dd, HH:mm:ss")
                };

                // Store the friendship request
                await _friendshipRequestService.StoreFriendshipRequestAsync(friendshipRequest);

                // Create an interactive notice
                var notice = new Notice
                {
                    AccountId = Guid.Parse(_accountId),
                    Title = "Friendship Offer",
                    Message = $"{im.FromAgentName} has offered you friendship.\n\nMessage: {im.Message}",
                    FromName = im.FromAgentName,
                    FromId = im.FromAgentID.ToString(),
                    Type = "FriendshipOffer",
                    Timestamp = timestamp,
                    IsInteractive = true,
                    HasResponse = false,
                    ExternalRequestId = requestId,
                    SessionId = im.IMSessionID.ToString(),
                    ExpiresAt = timestamp.AddHours(24),
                    RequiresAcknowledgment = false, // User needs to respond, but no SL auto-ack needed
                    IsAcknowledged = false,
                    IsRead = false
                };

                // Save the notice to database
                await SaveNoticeDirectlyAsync(notice);

                _logger.LogInformation("Created interactive friendship offer notice from {FromAgentName} ({FromAgentId}) for account {AccountId}",
                    im.FromAgentName, im.FromAgentID, _accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling friendship offer from {FromAgentName} for account {AccountId}",
                    im.FromAgentName, _accountId);
            }
        }

        /// <summary>
        /// Handle incoming group invitations by creating interactive notices
        /// </summary>
        private async Task HandleGroupInvitationAsync(InstantMessage im)
        {
            try
            {
                // Create a group invitation DTO
                var invitationId = Guid.NewGuid().ToString();
                var timestamp = DateTime.UtcNow;
                
                // Try to extract group information from the message
                // Group invitations typically have the group name in the message
                var groupName = "Unknown Group";
                var groupId = "00000000-0000-0000-0000-000000000000";

                // Try to extract group ID from the binary bucket if available
                if (im.BinaryBucket.Length >= 16)
                {
                    try
                    {
                        var extractedGroupId = new UUID(im.BinaryBucket, 0);
                        groupId = extractedGroupId.ToString();

                        // Try to get group name from our cached groups
                        if (_groups.TryGetValue(extractedGroupId, out var group))
                        {
                            groupName = group.Name;
                        }
                        else
                        {
                            // Try to parse group name from message
                            var lines = im.Message.Split('\n');
                            if (lines.Length > 0)
                            {
                                var firstLine = lines[0].Trim();
                                if (!string.IsNullOrEmpty(firstLine) && !firstLine.Contains("has invited you"))
                                {
                                    groupName = firstLine;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error parsing group ID from binary bucket for invitation from {FromAgentName}", im.FromAgentName);
                    }
                }

                var groupInvitation = new GroupInvitationDto
                {
                    InvitationId = invitationId,
                    AccountId = Guid.Parse(_accountId),
                    FromAgentId = im.FromAgentID.ToString(),
                    FromAgentName = im.FromAgentName,
                    GroupId = groupId,
                    GroupName = groupName,
                    Message = im.Message,
                    SessionId = im.IMSessionID.ToString(),
                    ReceivedAt = timestamp,
                    IsResponded = false,
                    ExpiresAt = timestamp.AddHours(24), // Give 24 hours to respond
                    SLTReceivedAt = _slTimeService.FormatSLTWithDate(timestamp, "MMM dd, HH:mm:ss"),
                    SLTExpiresAt = _slTimeService.FormatSLTWithDate(timestamp.AddHours(24), "MMM dd, HH:mm:ss")
                };

                // Store the group invitation
                await _groupInvitationService.StoreGroupInvitationAsync(groupInvitation);

                // Create an interactive notice
                var notice = new Notice
                {
                    AccountId = Guid.Parse(_accountId),
                    Title = "Group Invitation",
                    Message = $"{im.FromAgentName} has invited you to join the group '{groupName}'.\n\nMessage: {im.Message}",
                    FromName = im.FromAgentName,
                    FromId = im.FromAgentID.ToString(),
                    GroupId = groupId,
                    GroupName = groupName,
                    Type = "GroupInvitation",
                    Timestamp = timestamp,
                    IsInteractive = true,
                    HasResponse = false,
                    ExternalRequestId = invitationId,
                    SessionId = im.IMSessionID.ToString(),
                    ExpiresAt = timestamp.AddHours(24),
                    RequiresAcknowledgment = false, // User needs to respond, but no SL auto-ack needed
                    IsAcknowledged = false,
                    IsRead = false
                };

                // Save the notice to database
                await SaveNoticeDirectlyAsync(notice);

                _logger.LogInformation("Created interactive group invitation notice to '{GroupName}' from {FromAgentName} ({FromAgentId}) for account {AccountId}",
                    groupName, im.FromAgentName, im.FromAgentID, _accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling group invitation from {FromAgentName} for account {AccountId}",
                    im.FromAgentName, _accountId);
            }
        }

        /// <summary>
        /// Helper method to save notices directly to the database
        /// </summary>
        private async Task SaveNoticeDirectlyAsync(Notice notice)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                context.Notices.Add(notice);
                await context.SaveChangesAsync();
                _logger.LogDebug("Saved interactive notice {NoticeId} to database for account {AccountId}", 
                    notice.Id, notice.AccountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving interactive notice to database for account {AccountId}", 
                    notice.AccountId);
                throw;
            }
        }

        #endregion

        #region Distance Calculation

        /// <summary>
        /// Calculate 3D distance between two positions (including Z-axis/altitude)
        /// </summary>
        /// <param name="pos1">First position</param>
        /// <param name="pos2">Second position</param>
        /// <returns>3D distance in meters</returns>
        private static float Calculate3DDistance(Vector3 pos1, Vector3 pos2)
        {
            var deltaX = pos2.X - pos1.X;
            var deltaY = pos2.Y - pos1.Y;
            var deltaZ = pos2.Z - pos1.Z;
            return (float)Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
        }

        /// <summary>
        /// Gets the actual position of our avatar, accounting for sitting on objects
        /// </summary>
        /// <returns>The corrected position of our avatar</returns>
        private Vector3 GetOurActualPosition()
        {
            var basePosition = _client.Self.SimPosition;
            
            // If we're sitting on an object, we need to account for the object's position
            if (_client.Self.SittingOn != 0)
            {
                // Try to find the object we're sitting on
                if (_client.Network.CurrentSim?.ObjectsPrimitives.TryGetValue(_client.Self.SittingOn, out var seatObject) == true)
                {
                    // Use the seat object's position as our position
                    return seatObject.Position;
                }
            }
            
            return basePosition;
        }

        /// <summary>
        /// Gets the actual world position of an avatar, accounting for seating on objects.
        /// This matches Radegast's avatar position calculation logic.
        /// </summary>
        /// <param name="avatar">The avatar to get position for</param>
        /// <returns>Actual world position of the avatar</returns>
        private Vector3 GetAvatarActualPosition(Avatar avatar)
        {
            // If avatar is not seated (ParentID == 0), return their direct position
            if (avatar.ParentID == 0)
            {
                return avatar.Position;
            }
            
            // Avatar is seated on an object - need to calculate actual position
            if (_client.Network.CurrentSim?.ObjectsPrimitives.TryGetValue(avatar.ParentID, out var seatObject) == true)
            {
                // The avatar's position is relative to the seat object
                // Following Radegast's logic: seat position + avatar offset * seat rotation
                return seatObject.Position + avatar.Position * seatObject.Rotation;
            }
            
            // Fallback if we can't find the seat object
            return avatar.Position;
        }

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

        #region IM Relay Functions

        // Second Life NULL_KEY constant
        private const string NULL_KEY = "00000000-0000-0000-0000-000000000000";

        /*
         * IM relay function if AvatarRelayUuid is configured:
         * 
         * HandleProximityWarning - Sends warning IM when avatar gets within 0.25m (triggered from radar)
         * 
         * Note: Individual IM relaying is now handled by the ChatProcessingService pipeline
         */

        /// <summary>
        /// Sends proximity warning IM when an avatar comes within 0.25m
        /// Includes spam prevention - only sends one alert per avatar until they move away and come back
        /// This is triggered from the radar when avatars get close
        /// </summary>
        /// <param name="avatarId">The UUID of the nearby avatar</param>
        private async Task HandleProximityWarning(string avatarId)
        {
            try
            {
                _logger.LogDebug("HandleProximityWarning called for avatar {AvatarId} on account {AccountId}. AvatarRelayUuid: {AvatarRelayUuid}", 
                    avatarId, _accountId, AccountInfo.AvatarRelayUuid ?? "null");
                
                // Check if AvatarRelayUuid is valid (not null, empty, or NULL_KEY) using the AccountInfo property
                if (string.IsNullOrEmpty(AccountInfo.AvatarRelayUuid) || AccountInfo.AvatarRelayUuid == NULL_KEY)
                {
                    _logger.LogDebug("Account {AccountId} has no valid relay avatar configured for proximity warnings", _accountId);
                    return;
                }

                // Prevent sending IM to oneself
                if (AccountInfo.AvatarRelayUuid.Equals(_client.Self.AgentID.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Account {AccountId} has AvatarRelayUuid set to own avatar ID {OwnId} - cannot send IM to self for proximity warnings", 
                        _accountId, _client.Self.AgentID);
                    return;
                }

                // Parse avatar UUID for tracking
                if (!UUID.TryParse(avatarId, out var avatarUuid))
                {
                    _logger.LogWarning("Invalid avatar UUID for proximity detection: {AvatarId}", avatarId);
                    return;
                }

                // Check if we've already alerted about this avatar being close
                if (_proximityAlertedAvatars.ContainsKey(avatarUuid))
                {
                    _logger.LogDebug("Skipping proximity alert for avatar {AvatarId} - already alerted (preventing spam)", avatarId);
                    return;
                }

                // Record that we've alerted about this avatar
                _proximityAlertedAvatars.TryAdd(avatarUuid, DateTime.UtcNow);

                // Get avatar name for better alert message
                var avatarName = "Unknown";
                if (_nearbyAvatars.TryGetValue(avatarUuid, out var detailedAvatar))
                {
                    avatarName = detailedAvatar.Name;
                }
                else if (_coarseLocationAvatars.TryGetValue(avatarUuid, out var coarseAvatar))
                {
                    avatarName = coarseAvatar.Name;
                }

                // Try to get display name for more informative alert
                var displayName = await _globalDisplayNameCache.GetDisplayNameAsync(avatarId, NameDisplayMode.Smart, avatarName);
                var finalName = !string.IsNullOrEmpty(displayName) && displayName != "Loading..." ? displayName : avatarName;

                // Format the proximity message with more detail
                var proximityMessage = $"[PROXIMITY ALERT] [secondlife:///app/agent/{avatarId}/about {finalName}] is close to me in {_client.Network.CurrentSim?.Name ?? "Unknown Region"}!";

                // Send the IM to the relay avatar if we're connected
                if (_client.Network.Connected)
                {
                    _logger.LogInformation("Sending proximity warning IM to relay avatar {RelayUuid} about {AvatarName} ({AvatarId}) on account {AccountId}", 
                        AccountInfo.AvatarRelayUuid, finalName, avatarId, _accountId);
                    
                    SendIM(AccountInfo.AvatarRelayUuid!, proximityMessage);
                    
                    _logger.LogInformation("✓ Successfully sent proximity warning IM to relay avatar {RelayUuid} for avatar {AvatarName} ({AvatarId}) on account {AccountId}", 
                        AccountInfo.AvatarRelayUuid, finalName, avatarId, _accountId);
                }
                else
                {
                    _logger.LogWarning("Cannot send proximity warning - account {AccountId} is not connected", _accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling proximity warning for avatar {AvatarId} on account {AccountId}", 
                    avatarId, _accountId);
            }
        }

        #endregion

        #region Script Dialog Service Event Handlers

        private void ScriptDialogService_DialogReceived(object? sender, Models.ScriptDialogEventArgs e)
        {
            try
            {
                ScriptDialogReceived?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling script dialog received event for account {AccountId}", _accountId);
            }
        }

        private void ScriptDialogService_PermissionReceived(object? sender, Models.ScriptPermissionEventArgs e)
        {
            try
            {
                ScriptPermissionReceived?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling script permission received event for account {AccountId}", _accountId);
            }
        }

        #endregion

        #region Teleport Request Service Event Handlers

        private void TeleportRequestService_TeleportRequestReceived(object? sender, TeleportRequestEventArgs e)
        {
            try
            {
                TeleportRequestReceived?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling teleport request received event for account {AccountId}", _accountId);
            }
        }

        #endregion

        #region Script Dialog Event Handlers

        private void Self_ScriptDialog(object? sender, OpenMetaverse.ScriptDialogEventArgs e)
        {
            try
            {
                _logger.LogInformation("Script dialog received from {ObjectName} ({ObjectId}) for account {AccountId}", 
                    e.ObjectName, e.ObjectID, _accountId);

                // Handle the script dialog through the service
                _ = Task.Run(async () => 
                {
                    try
                    {
                        await _scriptDialogService.HandleScriptDialogAsync(
                            Guid.Parse(_accountId),
                            e.Message,
                            e.ObjectName,
                            e.ImageID,
                            e.ObjectID,
                            e.FirstName,
                            e.LastName,
                            e.Channel,
                            e.ButtonLabels
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error handling script dialog for account {AccountId}", _accountId);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Self_ScriptDialog event handler for account {AccountId}", _accountId);
            }
        }

        private void Self_ScriptQuestion(object? sender, OpenMetaverse.ScriptQuestionEventArgs e)
        {
            try
            {
                _logger.LogInformation("Script permission request received from {ObjectName} ({TaskId}) for account {AccountId}: {Questions}", 
                    e.ObjectName, e.TaskID, _accountId, e.Questions);

                // Handle the script permission request through the service
                _ = Task.Run(async () => 
                {
                    try
                    {
                        await _scriptDialogService.HandleScriptPermissionAsync(
                            Guid.Parse(_accountId),
                            e.TaskID,
                            e.ItemID,
                            e.ObjectName,
                            e.ObjectOwnerName,
                            e.Questions
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error handling script permission request for account {AccountId}", _accountId);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Self_ScriptQuestion event handler for account {AccountId}", _accountId);
            }
        }

        #endregion

        #region Avatar Name Resolution Helpers

        /// <summary>
        /// Gets the best available name for an avatar, with proper fallbacks
        /// </summary>
        /// <param name="avatarId">UUID of the avatar</param>
        /// <param name="detailedAvatarName">Name from detailed avatar object, if available</param>
        /// <returns>Best available name for the avatar</returns>
        private string GetBestAvatarName(UUID avatarId, string? detailedAvatarName)
        {
            // Try detailed avatar name first
            if (!string.IsNullOrWhiteSpace(detailedAvatarName) && 
                detailedAvatarName != "Loading..." && 
                detailedAvatarName != "???")
            {
                return detailedAvatarName;
            }

            // Try cached display name from global cache
            try
            {
                var cachedDisplayName = _globalDisplayNameCache.GetCachedDisplayName(avatarId.ToString(), NameDisplayMode.Smart);
                if (!string.IsNullOrWhiteSpace(cachedDisplayName) && 
                    cachedDisplayName != "Loading..." && 
                    cachedDisplayName != "???")
                {
                    return cachedDisplayName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error getting cached display name for avatar {AvatarId}", avatarId);
            }

            // Try name resolution service cache
            try
            {
                var cachedName = _nameResolutionService.GetCachedName(Guid.Parse(_accountId), avatarId, ResolveType.AgentDefaultName);
                if (!string.IsNullOrWhiteSpace(cachedName) && 
                    cachedName != "Loading..." && 
                    cachedName != "???" &&
                    cachedName != avatarId.ToString())
                {
                    return cachedName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error getting cached name from name resolution service for avatar {AvatarId}", avatarId);
            }

            // Request name resolution if we don't have a good name
            // This will populate the cache for future lookups
            if (_client.Network.Connected)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _client.Avatars.RequestAvatarNames(new List<UUID> { avatarId });
                        await _globalDisplayNameCache.RequestDisplayNamesAsync(
                            new List<string> { avatarId.ToString() }, 
                            Guid.Parse(_accountId));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error requesting names for avatar {AvatarId}", avatarId);
                    }
                });
            }

            // Return a more informative placeholder that includes the avatar ID
            // This makes it easier to track which avatars need name resolution
            return $"Resolving... ({avatarId.ToString().Substring(0, 8)})";
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
                
                // Clean up region map cache for this account
                _ = Task.Run(async () => await _regionMapCacheService.CleanupAccountMapsAsync(Guid.Parse(_accountId)));
                
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