using RadegastWeb.Models;
using RadegastWeb.Core;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using OpenMetaverse;

namespace RadegastWeb.Services
{
    /// <summary>
    /// Service for handling Corrade plugin functionality
    /// </summary>
    public class CorradeService : ICorradeService
    {
        private readonly ILogger<CorradeService> _logger;
        private readonly IAccountService _accountService;
        private readonly IGroupService _groupService;
        private readonly string _configPath;
        private CorradeConfig? _cachedConfig;
        private readonly object _configLock = new();
        private DateTime _configLastLoaded = DateTime.MinValue;
        private static readonly TimeSpan ConfigCacheTimeout = TimeSpan.FromMinutes(5);
        private bool _isEnabled = false;

        public CorradeService(
            ILogger<CorradeService> logger,
            IAccountService accountService,
            IGroupService groupService)
        {
            _logger = logger;
            _accountService = accountService;
            _groupService = groupService;
            _configPath = Path.Combine("data", "corrade.json");
            
            // Check if plugin should be enabled
            _logger.LogInformation("Initializing CorradeService...");
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("Loading Corrade configuration...");
                    var config = await LoadConfigurationAsync();
                    _logger.LogDebug("Corrade configuration loaded: {GroupCount} groups", config.Groups.Count);
                    _isEnabled = config.Groups.Count > 0;
                    if (_isEnabled)
                    {
                        _logger.LogInformation("Corrade plugin enabled with {GroupCount} groups configured", config.Groups.Count);
                        foreach (var group in config.Groups)
                        {
                            _logger.LogInformation("Corrade group configured: {GroupName} ({GroupUuid})", group.GroupName ?? "Unknown", group.GroupUuid);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Corrade plugin disabled - no groups configured");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize Corrade plugin");
                    _isEnabled = false;
                }
            });
        }

        public bool IsEnabled => _isEnabled;

        /// <summary>
        /// Check if this account should process Corrade commands
        /// </summary>
        /// <param name="accountId">The account ID to check</param>
        /// <returns>True if this account should process Corrade commands</returns>
        public async Task<bool> ShouldProcessWhispersForAccountAsync(Guid accountId)
        {
            if (!_isEnabled)
                return false;

            try
            {
                var config = await LoadConfigurationAsync();
                
                // If no specific account is configured, allow all accounts (legacy behavior)
                if (string.IsNullOrWhiteSpace(config.LinkedAccountId))
                {
                    return true;
                }
                
                // Only process for the specifically linked account
                return config.LinkedAccountId.Equals(accountId.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking if account {AccountId} should process Corrade commands", accountId);
                return false;
            }
        }

        /// <summary>
        /// Check if object commands are allowed based on configuration
        /// </summary>
        /// <returns>True if object commands are allowed</returns>
        public async Task<bool> AreObjectCommandsAllowedAsync()
        {
            try
            {
                var config = await LoadConfigurationAsync();
                return config.AllowObjectCommands;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking if object commands are allowed");
                return false;
            }
        }

        public bool IsWhisperCorradeCommand(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            // Check if message starts with Corrade command pattern
            return message.TrimStart().StartsWith("command=", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<CorradeCommandResult> ProcessWhisperCommandAsync(Guid accountId, string senderId, string senderName, string message)
        {
            try
            {
                // Check if plugin is enabled
                if (!_isEnabled)
                {
                    _logger.LogDebug("Corrade plugin is disabled, ignoring whisper command from {SenderName}", senderName);
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Corrade plugin is disabled",
                        ErrorCode = "PLUGIN_DISABLED"
                    };
                }

                _logger.LogInformation("Processing Corrade command from {SenderName} ({SenderId}) for account {AccountId}: {Message}", 
                    senderName, senderId, accountId, message);

                // Parse the command first to get the group UUID for security check
                var command = ParseCorradeCommand(message);
                if (!command.IsValid)
                {
                    _logger.LogWarning("Invalid Corrade command from {SenderName}: {Error}", senderName, command.ValidationError);
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = command.ValidationError,
                        ProcessedCommand = command
                    };
                }

                // Security Check: Verify the sender is a member of the authorizing group
                if (!await IsSenderGroupMemberAsync(accountId, senderId, command.GroupUuid!, senderName))
                {
                    _logger.LogWarning("Security: Corrade command rejected - {SenderName} ({SenderId}) is not a member of authorizing group {GroupId}", 
                        senderName, senderId, command.GroupUuid);
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Access denied - not a group member",
                        ErrorCode = "NOT_AUTHORIZED",
                        ProcessedCommand = command
                    };
                }

                // Validate permissions
                if (!await ValidateEntityPermissionAsync(accountId, command.Entity!, command.TargetUuid, command.GroupUuid!, command.Password!))
                {
                    _logger.LogWarning("Permission denied for Corrade command from {SenderName} to {Entity}", senderName, command.Entity);
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Permission denied",
                        ErrorCode = "PERMISSION_DENIED",
                        ProcessedCommand = command
                    };
                }

                // Get the account instance
                var accountInstance = _accountService.GetInstance(accountId);
                if (accountInstance == null || !accountInstance.IsConnected)
                {
                    _logger.LogWarning("Account {AccountId} not connected, cannot process Corrade command", accountId);
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Account not connected",
                        ErrorCode = "ACCOUNT_OFFLINE",
                        ProcessedCommand = command
                    };
                }

                // Execute the command based on entity type
                var result = await ExecuteCommandAsync(accountInstance, command);
                
                _logger.LogInformation("Corrade command processed: Success={Success}, Entity={Entity}, Message={Message}", 
                    result.Success, command.Entity, command.Message);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Corrade command from {SenderName}", senderName);
                return new CorradeCommandResult
                {
                    Success = false,
                    Message = "Internal error processing command",
                    ErrorCode = "INTERNAL_ERROR"
                };
            }
        }

        private CorradeCommand ParseCorradeCommand(string message)
        {
            var command = new CorradeCommand();
            
            try
            {
                // Parse URL-encoded parameters
                var parameters = ParseUrlEncodedParameters(message);
                command.Parameters = parameters;

                // Extract required parameters
                if (parameters.TryGetValue("command", out var commandValue))
                    command.Command = commandValue;

                if (parameters.TryGetValue("group", out var groupValue))
                    command.GroupUuid = groupValue;

                if (parameters.TryGetValue("password", out var passwordValue))
                    command.Password = passwordValue;

                if (parameters.TryGetValue("entity", out var entityValue))
                    command.Entity = entityValue;

                if (parameters.TryGetValue("message", out var messageValue))
                    command.Message = messageValue;

                if (parameters.TryGetValue("target", out var targetValue))
                    command.TargetUuid = targetValue;

                if (parameters.TryGetValue("agent", out var agentValue))
                    command.Agent = agentValue;

                // Validate required fields for "tell" command
                if (command.Command.Equals("tell", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(command.GroupUuid))
                    {
                        command.ValidationError = "Group UUID is required";
                        return command;
                    }

                    if (string.IsNullOrWhiteSpace(command.Password))
                    {
                        command.ValidationError = "Password is required";
                        return command;
                    }

                    if (string.IsNullOrWhiteSpace(command.Entity))
                    {
                        command.ValidationError = "Entity is required";
                        return command;
                    }

                    if (string.IsNullOrWhiteSpace(command.Message))
                    {
                        command.ValidationError = "Message is required";
                        return command;
                    }

                    // Validate entity type
                    var validEntities = new[] { "local", "group", "avatar" };
                    if (!validEntities.Contains(command.Entity.ToLowerInvariant()))
                    {
                        command.ValidationError = $"Invalid entity type. Must be one of: {string.Join(", ", validEntities)}";
                        return command;
                    }

                    // Validate target UUID for group and avatar entities
                    if (command.Entity.Equals("group", StringComparison.OrdinalIgnoreCase))
                    {
                        // For group entities, if target is missing/empty, use the authorizing group UUID
                        if (string.IsNullOrWhiteSpace(command.TargetUuid))
                        {
                            command.TargetUuid = command.GroupUuid;
                            _logger.LogDebug("Using authorizing group UUID as target for group command: {GroupUuid}", command.GroupUuid);
                        }
                    }
                    else if (command.Entity.Equals("avatar", StringComparison.OrdinalIgnoreCase) && 
                             string.IsNullOrWhiteSpace(command.TargetUuid))
                    {
                        command.ValidationError = "Target UUID is required for avatar entity type";
                        return command;
                    }

                    // Validate UUIDs
                    if (!string.IsNullOrWhiteSpace(command.GroupUuid) && !UUID.TryParse(command.GroupUuid, out _))
                    {
                        command.ValidationError = "Invalid group UUID format";
                        return command;
                    }

                    if (!string.IsNullOrWhiteSpace(command.TargetUuid) && !UUID.TryParse(command.TargetUuid, out _))
                    {
                        command.ValidationError = "Invalid target UUID format";
                        return command;
                    }

                    command.IsValid = true;
                }
                // Validate required fields for "invite" command
                else if (command.Command.Equals("invite", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(command.GroupUuid))
                    {
                        command.ValidationError = "Group UUID is required";
                        return command;
                    }

                    if (string.IsNullOrWhiteSpace(command.Password))
                    {
                        command.ValidationError = "Password is required";
                        return command;
                    }

                    if (string.IsNullOrWhiteSpace(command.Agent))
                    {
                        command.ValidationError = "Agent UUID is required for invite command";
                        return command;
                    }

                    // Validate UUIDs
                    if (!UUID.TryParse(command.GroupUuid, out _))
                    {
                        command.ValidationError = "Invalid group UUID format";
                        return command;
                    }

                    if (!UUID.TryParse(command.Agent, out _))
                    {
                        command.ValidationError = "Invalid agent UUID format";
                        return command;
                    }

                    command.IsValid = true;
                }
                else
                {
                    command.ValidationError = $"Unsupported command: {command.Command}";
                }
            }
            catch (Exception ex)
            {
                command.ValidationError = $"Error parsing command: {ex.Message}";
                _logger.LogError(ex, "Error parsing Corrade command: {Message}", message);
            }

            return command;
        }

        private Dictionary<string, string> ParseUrlEncodedParameters(string message)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            try
            {
                // Split by & to get parameter pairs
                var parts = message.Split('&');
                
                foreach (var part in parts)
                {
                    var keyValue = part.Split('=', 2);
                    if (keyValue.Length == 2)
                    {
                        var key = HttpUtility.UrlDecode(keyValue[0].Trim());
                        var value = HttpUtility.UrlDecode(keyValue[1].Trim());
                        parameters[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing URL-encoded parameters: {Message}", message);
            }

            return parameters;
        }

        private async Task<CorradeCommandResult> ExecuteCommandAsync(WebRadegastInstance accountInstance, CorradeCommand command)
        {
            try
            {
                // Handle invite command separately (doesn't use entity)
                if (command.Command.Equals("invite", StringComparison.OrdinalIgnoreCase))
                {
                    return await ExecuteGroupInviteCommand(accountInstance, command);
                }

                switch (command.Entity!.ToLowerInvariant())
                {
                    case "local":
                        return await ExecuteLocalChatCommand(accountInstance, command);
                    
                    case "group":
                        return await ExecuteGroupChatCommand(accountInstance, command);
                    
                    case "avatar":
                        return await ExecuteAvatarIMCommand(accountInstance, command);
                    
                    default:
                        return new CorradeCommandResult
                        {
                            Success = false,
                            Message = $"Unsupported entity type: {command.Entity}",
                            ErrorCode = "INVALID_ENTITY",
                            ProcessedCommand = command
                        };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing Corrade command");
                return new CorradeCommandResult
                {
                    Success = false,
                    Message = "Error executing command",
                    ErrorCode = "EXECUTION_ERROR",
                    ProcessedCommand = command
                };
            }
        }

        private Task<CorradeCommandResult> ExecuteLocalChatCommand(WebRadegastInstance accountInstance, CorradeCommand command)
        {
            try
            {
                // Send message to local chat
                accountInstance.SendChat(command.Message!);

                _logger.LogInformation("Sent local chat message via Corrade for account {AccountId}: {Message}", 
                    accountInstance.AccountId, command.Message);

                return Task.FromResult(new CorradeCommandResult
                {
                    Success = true,
                    Message = "Message sent to local chat",
                    ProcessedCommand = command
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending local chat message via Corrade");
                return Task.FromResult(new CorradeCommandResult
                {
                    Success = false,
                    Message = "Failed to send local chat message",
                    ErrorCode = "CHAT_FAILED",
                    ProcessedCommand = command
                });
            }
        }

        private async Task<CorradeCommandResult> ExecuteGroupChatCommand(WebRadegastInstance accountInstance, CorradeCommand command)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(command.TargetUuid))
                {
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Target group UUID is required",
                        ErrorCode = "MISSING_TARGET",
                        ProcessedCommand = command
                    };
                }

                // Verify the account is a member of the target group
                var accountId = Guid.Parse(accountInstance.AccountId);
                var isGroupMember = await IsAccountGroupMemberAsync(accountId, command.TargetUuid);
                
                if (!isGroupMember)
                {
                    _logger.LogWarning("Account {AccountId} is not a member of group {GroupId}, cannot send group message", 
                        accountId, command.TargetUuid);
                    
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Not a member of the target group",
                        ErrorCode = "NOT_GROUP_MEMBER",
                        ProcessedCommand = command
                    };
                }

                // Send message to group
                accountInstance.SendGroupIM(command.TargetUuid, command.Message!);

                _logger.LogInformation("Sent group IM via Corrade for account {AccountId} to group {GroupId}: {Message}", 
                    accountInstance.AccountId, command.TargetUuid, command.Message);

                return new CorradeCommandResult
                {
                    Success = true,
                    Message = "Message sent to group",
                    ProcessedCommand = command
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending group IM via Corrade");
                return new CorradeCommandResult
                {
                    Success = false,
                    Message = "Failed to send group message",
                    ErrorCode = "GROUP_SEND_FAILED",
                    ProcessedCommand = command
                };
            }
        }

        private Task<CorradeCommandResult> ExecuteAvatarIMCommand(WebRadegastInstance accountInstance, CorradeCommand command)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(command.TargetUuid))
                {
                    return Task.FromResult(new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Target avatar UUID is required",
                        ErrorCode = "MISSING_TARGET",
                        ProcessedCommand = command
                    });
                }

                // Send IM to avatar
                accountInstance.SendIM(command.TargetUuid, command.Message!);

                _logger.LogInformation("Sent avatar IM via Corrade for account {AccountId} to avatar {AvatarId}: {Message}", 
                    accountInstance.AccountId, command.TargetUuid, command.Message);

                return Task.FromResult(new CorradeCommandResult
                {
                    Success = true,
                    Message = "Message sent to avatar",
                    ProcessedCommand = command
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending avatar IM via Corrade");
                return Task.FromResult(new CorradeCommandResult
                {
                    Success = false,
                    Message = "Failed to send avatar message",
                    ErrorCode = "AVATAR_SEND_FAILED",
                    ProcessedCommand = command
                });
            }
        }

        private async Task<CorradeCommandResult> ExecuteGroupInviteCommand(WebRadegastInstance accountInstance, CorradeCommand command)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(command.GroupUuid))
                {
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Group UUID is required",
                        ErrorCode = "MISSING_GROUP",
                        ProcessedCommand = command
                    };
                }

                if (string.IsNullOrWhiteSpace(command.Agent))
                {
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Agent UUID is required",
                        ErrorCode = "MISSING_AGENT",
                        ProcessedCommand = command
                    };
                }

                // Verify the account is a member of the group
                var accountId = Guid.Parse(accountInstance.AccountId);
                var isGroupMember = await IsAccountGroupMemberAsync(accountId, command.GroupUuid);
                
                if (!isGroupMember)
                {
                    _logger.LogWarning("Account {AccountId} is not a member of group {GroupId}, cannot send invite", 
                        accountId, command.GroupUuid);
                    
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Not a member of the specified group",
                        ErrorCode = "NOT_GROUP_MEMBER",
                        ProcessedCommand = command
                    };
                }

                // Send group invite
                var success = accountInstance.SendGroupInvite(command.GroupUuid, command.Agent);

                if (success)
                {
                    _logger.LogInformation("Sent group invite via Corrade for account {AccountId} to group {GroupId} for agent {AgentId}", 
                        accountInstance.AccountId, command.GroupUuid, command.Agent);

                    return new CorradeCommandResult
                    {
                        Success = true,
                        Message = "Group invitation sent",
                        ProcessedCommand = command
                    };
                }
                else
                {
                    return new CorradeCommandResult
                    {
                        Success = false,
                        Message = "Failed to send group invitation",
                        ErrorCode = "INVITE_FAILED",
                        ProcessedCommand = command
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending group invite via Corrade");
                return new CorradeCommandResult
                {
                    Success = false,
                    Message = "Failed to send group invitation",
                    ErrorCode = "INVITE_FAILED",
                    ProcessedCommand = command
                };
            }
        }

        public async Task<CorradeConfig> LoadConfigurationAsync()
        {
            lock (_configLock)
            {
                // Return cached config if it's still valid
                if (_cachedConfig != null && DateTime.UtcNow - _configLastLoaded < ConfigCacheTimeout)
                {
                    return _cachedConfig;
                }
            }

            try
            {
                if (!File.Exists(_configPath))
                {
                    _logger.LogInformation("Corrade config file not found at {ConfigPath}, creating default configuration", _configPath);
                    var defaultConfig = new CorradeConfig();
                    await SaveConfigurationAsync(defaultConfig);
                    return defaultConfig;
                }

                var json = await File.ReadAllTextAsync(_configPath);
                var config = JsonSerializer.Deserialize<CorradeConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                });

                if (config == null)
                {
                    _logger.LogWarning("Failed to deserialize Corrade config, using default");
                    config = new CorradeConfig();
                }

                lock (_configLock)
                {
                    _cachedConfig = config;
                    _configLastLoaded = DateTime.UtcNow;
                }

                // Update enabled status
                _isEnabled = config.Groups.Count > 0;

                _logger.LogInformation("Loaded Corrade configuration with {GroupCount} groups, plugin is {Status}", 
                    config.Groups.Count, _isEnabled ? "enabled" : "disabled");
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading Corrade configuration from {ConfigPath}", _configPath);
                return new CorradeConfig();
            }
        }

        public async Task SaveConfigurationAsync(CorradeConfig config)
        {
            try
            {
                // Ensure the data directory exists
                var dataDir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }

                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                await File.WriteAllTextAsync(_configPath, json);

                lock (_configLock)
                {
                    _cachedConfig = config;
                    _configLastLoaded = DateTime.UtcNow;
                }

                // Update enabled status
                _isEnabled = config.Groups.Count > 0;

                _logger.LogInformation("Saved Corrade configuration with {GroupCount} groups, plugin is now {Status}", 
                    config.Groups.Count, _isEnabled ? "enabled" : "disabled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Corrade configuration to {ConfigPath}", _configPath);
                throw;
            }
        }

        public async Task<bool> ValidateGroupPermissionAsync(Guid accountId, string groupUuid, string password)
        {
            try
            {
                var config = await LoadConfigurationAsync();
                var group = config.Groups.FirstOrDefault(g => 
                    g.GroupUuid.Equals(groupUuid, StringComparison.OrdinalIgnoreCase));

                if (group == null)
                {
                    _logger.LogWarning("Group {GroupUuid} not found in Corrade configuration", groupUuid);
                    return false;
                }

                if (!group.Password.Equals(password, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Invalid password for group {GroupUuid} in Corrade command", groupUuid);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating group permission for {GroupUuid}", groupUuid);
                return false;
            }
        }

        public async Task<bool> ValidateEntityPermissionAsync(Guid accountId, string entity, string? targetId, string groupUuid, string password)
        {
            try
            {
                // First validate the group and password
                if (!await ValidateGroupPermissionAsync(accountId, groupUuid, password))
                {
                    return false;
                }

                var config = await LoadConfigurationAsync();
                var group = config.Groups.First(g => 
                    g.GroupUuid.Equals(groupUuid, StringComparison.OrdinalIgnoreCase));

                // Check entity-specific permissions
                switch (entity.ToLowerInvariant())
                {
                    case "local":
                        if (!group.AllowLocalChat)
                        {
                            _logger.LogWarning("Local chat not allowed for group {GroupUuid}", groupUuid);
                            return false;
                        }
                        break;

                    case "group":
                        if (!group.AllowGroupRelay)
                        {
                            _logger.LogWarning("Group relay not allowed for group {GroupUuid}", groupUuid);
                            return false;
                        }
                        
                        // For group messages, verify the account is a member of the target group
                        if (!string.IsNullOrWhiteSpace(targetId))
                        {
                            var isGroupMember = await IsAccountGroupMemberAsync(accountId, targetId);
                            if (!isGroupMember)
                            {
                                _logger.LogWarning("Account {AccountId} is not a member of target group {TargetGroupId}", 
                                    accountId, targetId);
                                return false;
                            }
                        }
                        break;

                    case "avatar":
                        if (!group.AllowAvatarIM)
                        {
                            _logger.LogWarning("Avatar IM not allowed for group {GroupUuid}", groupUuid);
                            return false;
                        }
                        break;

                    default:
                        _logger.LogWarning("Unknown entity type: {Entity}", entity);
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating entity permission for {Entity}", entity);
                return false;
            }
        }

        /// <summary>
        /// Helper method to check if an account is a member of a specific group
        /// </summary>
        private async Task<bool> IsAccountGroupMemberAsync(Guid accountId, string groupUuid)
        {
            try
            {
                // Get cached groups for the account
                var groups = await _groupService.GetGroupsAsync(accountId);
                
                // Check if the target group is in the account's group list
                return groups.Any(g => g.Id.Equals(groupUuid, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking group membership for account {AccountId} and group {GroupId}", 
                    accountId, groupUuid);
                return false;
            }
        }

        /// <summary>
        /// Security check: Verify that the receiving account is a member of the target group
        /// This prevents sending messages to groups we're not a member of
        /// </summary>
        private async Task<bool> IsSenderGroupMemberAsync(Guid accountId, string senderId, string groupUuid, string senderName)
        {
            try
            {
                // Parse the group UUID
                if (!UUID.TryParse(groupUuid, out var groupId))
                {
                    _logger.LogWarning("Invalid group UUID format: {GroupUuid}", groupUuid);
                    return false;
                }

                // Check if our account is a member of the authorizing group
                // This uses the cached group data we already have
                var isAccountMember = await IsAccountGroupMemberAsync(accountId, groupUuid);
                
                if (!isAccountMember)
                {
                    _logger.LogWarning("Security: Command rejected from {SenderName} ({SenderId}) - receiving account {AccountId} is not a member of group {GroupId}", 
                        senderName, senderId, accountId, groupUuid);
                    return false;
                }

                // Verify the group is also configured in Corrade
                var config = await LoadConfigurationAsync();
                var configuredGroup = config.Groups.FirstOrDefault(g => 
                    g.GroupUuid.Equals(groupUuid, StringComparison.OrdinalIgnoreCase));
                
                if (configuredGroup == null)
                {
                    _logger.LogWarning("Security: Group {GroupId} not configured in Corrade - rejecting command from {SenderName}", 
                        groupUuid, senderName);
                    return false;
                }

                // Log successful verification
                _logger.LogInformation("Security: Command authorized from {SenderName} ({SenderId}) - account is member of group {GroupId}", 
                    senderName, senderId, groupUuid);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during security check for command from {SenderName} for group {GroupId}", 
                    senderName, groupUuid);
                return false;
            }
        }
    }
}