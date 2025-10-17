using OpenMetaverse;
using RadegastWeb.Models;
using System.Collections.Concurrent;

namespace RadegastWeb.Services
{
    public interface IScriptDialogService
    {
        /// <summary>
        /// Event fired when a script dialog is received
        /// </summary>
        event EventHandler<Models.ScriptDialogEventArgs>? DialogReceived;
        
        /// <summary>
        /// Event fired when a script permission request is received
        /// </summary>
        event EventHandler<Models.ScriptPermissionEventArgs>? PermissionReceived;
        
        /// <summary>
        /// Event fired when a dialog is closed
        /// </summary>
        event EventHandler<string>? DialogClosed;
        
        /// <summary>
        /// Event fired when a permission request is closed
        /// </summary>
        event EventHandler<string>? PermissionClosed;
        
        /// <summary>
        /// Handle a received script dialog
        /// </summary>
        Task HandleScriptDialogAsync(Guid accountId, string message, string objectName, UUID imageId, UUID objectId, string firstName, string lastName, int channel, List<string> buttons);
        
        /// <summary>
        /// Handle a received script permission request
        /// </summary>
        Task HandleScriptPermissionAsync(Guid accountId, UUID taskId, UUID itemId, string objectName, string objectOwner, ScriptPermission permissions);
        
        /// <summary>
        /// Respond to a script dialog
        /// </summary>
        Task<bool> RespondToDialogAsync(ScriptDialogResponseRequest request);
        
        /// <summary>
        /// Dismiss a script dialog
        /// </summary>
        Task<bool> DismissDialogAsync(ScriptDialogDismissRequest request);
        
        /// <summary>
        /// Respond to a script permission request
        /// </summary>
        Task<bool> RespondToPermissionAsync(ScriptPermissionResponseRequest request);
        
        /// <summary>
        /// Get active dialogs for an account
        /// </summary>
        Task<IEnumerable<ScriptDialogDto>> GetActiveDialogsAsync(Guid accountId);
        
        /// <summary>
        /// Get active permission requests for an account
        /// </summary>
        Task<IEnumerable<ScriptPermissionDto>> GetActivePermissionsAsync(Guid accountId);
        
        /// <summary>
        /// Clean up expired dialogs
        /// </summary>
        Task CleanupExpiredDialogsAsync();
    }
    
    public class ScriptDialogService : IScriptDialogService
    {
        private readonly ILogger<ScriptDialogService> _logger;
        private readonly IAccountService _accountService;
        private readonly ConcurrentDictionary<string, ScriptDialogDto> _activeDialogs = new();
        private readonly ConcurrentDictionary<string, ScriptPermissionDto> _activePermissions = new();
        private readonly System.Threading.Timer _cleanupTimer;
        
        // Events for dialog notifications
        public event EventHandler<Models.ScriptDialogEventArgs>? DialogReceived;
        public event EventHandler<Models.ScriptPermissionEventArgs>? PermissionReceived;
        public event EventHandler<string>? DialogClosed;
        public event EventHandler<string>? PermissionClosed;
        
        public ScriptDialogService(ILogger<ScriptDialogService> logger, IAccountService accountService)
        {
            _logger = logger;
            _accountService = accountService;
            
            // Setup cleanup timer to run every 5 minutes
            _cleanupTimer = new System.Threading.Timer(
                async _ => await CleanupExpiredDialogsAsync(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5)
            );
        }
        
        public Task HandleScriptDialogAsync(Guid accountId, string message, string objectName, UUID imageId, UUID objectId, string firstName, string lastName, int channel, List<string> buttons)
        {
            try
            {
                var dialogId = Guid.NewGuid().ToString();
                
                // Check if this is a text input dialog (llTextBox)
                var isTextInput = buttons.Count == 1 && buttons[0] == "!!llTextBox!!";
                
                var dialog = new ScriptDialogDto
                {
                    DialogId = dialogId,
                    AccountId = accountId,
                    Message = message,
                    ObjectName = objectName,
                    ObjectId = objectId.ToString(),
                    OwnerFirstName = firstName,
                    OwnerLastName = lastName,
                    Channel = channel,
                    Buttons = isTextInput ? new List<string>() : buttons,
                    ImageId = imageId != UUID.Zero ? imageId.ToString() : null,
                    IsTextInput = isTextInput,
                    ReceivedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5) // Dialogs expire after 5 minutes
                };
                
                // Store the dialog
                _activeDialogs[dialogId] = dialog;
                
                _logger.LogInformation("Script dialog received for account {AccountId} from object {ObjectName} ({ObjectId})", 
                    accountId, objectName, objectId);
                
                // Fire event for SignalR broadcast
                DialogReceived?.Invoke(this, new Models.ScriptDialogEventArgs(dialog));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling script dialog for account {AccountId}", accountId);
            }
            
            return Task.CompletedTask;
        }
        
        public Task HandleScriptPermissionAsync(Guid accountId, UUID taskId, UUID itemId, string objectName, string objectOwner, ScriptPermission permissions)
        {
            try
            {
                var requestId = Guid.NewGuid().ToString();
                
                var permission = new ScriptPermissionDto
                {
                    RequestId = requestId,
                    AccountId = accountId,
                    ObjectName = objectName,
                    ObjectId = taskId.ToString(),
                    ItemId = itemId.ToString(),
                    ObjectOwner = objectOwner,
                    Permissions = permissions,
                    PermissionsDescription = GetPermissionDescription(permissions),
                    ReceivedAt = DateTime.UtcNow
                };
                
                // Store the permission request
                _activePermissions[requestId] = permission;
                
                _logger.LogInformation("Script permission request received for account {AccountId} from object {ObjectName} ({ObjectId}): {Permissions}", 
                    accountId, objectName, taskId, permissions);
                
                // Fire event for SignalR broadcast
                PermissionReceived?.Invoke(this, new Models.ScriptPermissionEventArgs(permission));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling script permission request for account {AccountId}", accountId);
            }
            
            return Task.CompletedTask;
        }
        
        public Task<bool> RespondToDialogAsync(ScriptDialogResponseRequest request)
        {
            try
            {
                if (!_activeDialogs.TryGetValue(request.DialogId, out var dialog))
                {
                    _logger.LogWarning("Dialog {DialogId} not found for account {AccountId}", request.DialogId, request.AccountId);
                    return Task.FromResult(false);
                }
                
                if (dialog.AccountId != request.AccountId)
                {
                    _logger.LogWarning("Account {AccountId} attempted to respond to dialog {DialogId} belonging to account {DialogAccountId}", 
                        request.AccountId, request.DialogId, dialog.AccountId);
                    return Task.FromResult(false);
                }
                
                if (dialog.IsResponded)
                {
                    _logger.LogWarning("Dialog {DialogId} has already been responded to", request.DialogId);
                    return Task.FromResult(false);
                }
                
                var instance = _accountService.GetInstance(request.AccountId);
                if (instance == null || !instance.IsConnected)
                {
                    _logger.LogWarning("Account {AccountId} not found or not connected", request.AccountId);
                    return Task.FromResult(false);
                }
                
                // Send the response to Second Life
                if (dialog.IsTextInput)
                {
                    // For text input dialogs, send the text
                    var textToSend = request.TextInput ?? "";
                    instance.Client.Self.ReplyToScriptDialog(dialog.Channel, 0, textToSend, UUID.Parse(dialog.ObjectId));
                    _logger.LogInformation("Sent text response '{Text}' to dialog {DialogId} for account {AccountId}", 
                        textToSend, request.DialogId, request.AccountId);
                }
                else
                {
                    // For button dialogs, send the button index and text
                    if (request.ButtonIndex >= 0 && request.ButtonIndex < dialog.Buttons.Count)
                    {
                        instance.Client.Self.ReplyToScriptDialog(dialog.Channel, request.ButtonIndex, request.ButtonText, UUID.Parse(dialog.ObjectId));
                        _logger.LogInformation("Sent button response '{ButtonText}' (index {ButtonIndex}) to dialog {DialogId} for account {AccountId}", 
                            request.ButtonText, request.ButtonIndex, request.DialogId, request.AccountId);
                    }
                    else
                    {
                        _logger.LogWarning("Invalid button index {ButtonIndex} for dialog {DialogId}", request.ButtonIndex, request.DialogId);
                        return Task.FromResult(false);
                    }
                }
                
                // Mark as responded and remove from active dialogs
                dialog.IsResponded = true;
                _activeDialogs.TryRemove(request.DialogId, out _);
                
                // Fire event for cleanup
                DialogClosed?.Invoke(this, request.DialogId);
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to script dialog {DialogId} for account {AccountId}", 
                    request.DialogId, request.AccountId);
                return Task.FromResult(false);
            }
        }
        
        public Task<bool> DismissDialogAsync(ScriptDialogDismissRequest request)
        {
            try
            {
                if (!_activeDialogs.TryGetValue(request.DialogId, out var dialog))
                {
                    _logger.LogWarning("Dialog {DialogId} not found for account {AccountId}", request.DialogId, request.AccountId);
                    return Task.FromResult(false);
                }
                
                if (dialog.AccountId != request.AccountId)
                {
                    _logger.LogWarning("Account {AccountId} attempted to dismiss dialog {DialogId} belonging to account {DialogAccountId}", 
                        request.AccountId, request.DialogId, dialog.AccountId);
                    return Task.FromResult(false);
                }
                
                // Remove the dialog without sending a response
                _activeDialogs.TryRemove(request.DialogId, out _);
                
                _logger.LogInformation("Dismissed dialog {DialogId} for account {AccountId}", request.DialogId, request.AccountId);
                
                // Fire event for cleanup
                DialogClosed?.Invoke(this, request.DialogId);
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dismissing script dialog {DialogId} for account {AccountId}", 
                    request.DialogId, request.AccountId);
                return Task.FromResult(false);
            }
        }
        
        public Task<bool> RespondToPermissionAsync(ScriptPermissionResponseRequest request)
        {
            try
            {
                if (!_activePermissions.TryGetValue(request.RequestId, out var permission))
                {
                    _logger.LogWarning("Permission request {RequestId} not found for account {AccountId}", request.RequestId, request.AccountId);
                    return Task.FromResult(false);
                }
                
                if (permission.AccountId != request.AccountId)
                {
                    _logger.LogWarning("Account {AccountId} attempted to respond to permission request {RequestId} belonging to account {PermissionAccountId}", 
                        request.AccountId, request.RequestId, permission.AccountId);
                    return Task.FromResult(false);
                }
                
                if (permission.IsResponded)
                {
                    _logger.LogWarning("Permission request {RequestId} has already been responded to", request.RequestId);
                    return Task.FromResult(false);
                }
                
                var instance = _accountService.GetInstance(request.AccountId);
                if (instance == null || !instance.IsConnected)
                {
                    _logger.LogWarning("Account {AccountId} not found or not connected", request.AccountId);
                    return Task.FromResult(false);
                }
                
                if (request.Mute)
                {
                    // Mute the object
                    instance.Client.Self.UpdateMuteListEntry(MuteType.Object, UUID.Parse(permission.ObjectId), permission.ObjectName);
                    _logger.LogInformation("Muted object {ObjectName} ({ObjectId}) for account {AccountId}", 
                        permission.ObjectName, permission.ObjectId, request.AccountId);
                }
                else
                {
                    // Send permission response
                    var permissionsToGrant = request.Grant ? permission.Permissions : 0;
                    
                    // Find the simulator for this object (assuming current sim for now)
                    var simulator = instance.Client.Network.CurrentSim;
                    if (simulator != null)
                    {
                        instance.Client.Self.ScriptQuestionReply(simulator, UUID.Parse(permission.ItemId), UUID.Parse(permission.ObjectId), permissionsToGrant);
                        _logger.LogInformation("Sent permission response ({Grant}) to object {ObjectName} for account {AccountId}: {Permissions}", 
                            request.Grant ? "Grant" : "Deny", permission.ObjectName, request.AccountId, permissionsToGrant);
                    }
                    else
                    {
                        _logger.LogWarning("No current simulator found for permission response for account {AccountId}", request.AccountId);
                        return Task.FromResult(false);
                    }
                }
                
                // Mark as responded and remove from active permissions
                permission.IsResponded = true;
                _activePermissions.TryRemove(request.RequestId, out _);
                
                // Fire event for cleanup
                PermissionClosed?.Invoke(this, request.RequestId);
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error responding to script permission request {RequestId} for account {AccountId}", 
                    request.RequestId, request.AccountId);
                return Task.FromResult(false);
            }
        }
        
        public Task<IEnumerable<ScriptDialogDto>> GetActiveDialogsAsync(Guid accountId)
        {
            var dialogs = _activeDialogs.Values
                .Where(d => d.AccountId == accountId && !d.IsResponded)
                .OrderBy(d => d.ReceivedAt)
                .AsEnumerable();
            
            return Task.FromResult(dialogs);
        }
        
        public Task<IEnumerable<ScriptPermissionDto>> GetActivePermissionsAsync(Guid accountId)
        {
            var permissions = _activePermissions.Values
                .Where(p => p.AccountId == accountId && !p.IsResponded)
                .OrderBy(p => p.ReceivedAt)
                .AsEnumerable();
            
            return Task.FromResult(permissions);
        }
        
        public Task CleanupExpiredDialogsAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredDialogs = _activeDialogs.Values
                    .Where(d => d.ExpiresAt.HasValue && d.ExpiresAt.Value < now)
                    .ToList();
                
                foreach (var dialog in expiredDialogs)
                {
                    _activeDialogs.TryRemove(dialog.DialogId, out _);
                    _logger.LogDebug("Cleaned up expired dialog {DialogId} for account {AccountId}", 
                        dialog.DialogId, dialog.AccountId);
                    
                    // Fire event for cleanup
                    DialogClosed?.Invoke(this, dialog.DialogId);
                }
                
                // Clean up old permission requests (expire after 10 minutes)
                var expiredPermissions = _activePermissions.Values
                    .Where(p => p.ReceivedAt.AddMinutes(10) < now)
                    .ToList();
                
                foreach (var permission in expiredPermissions)
                {
                    _activePermissions.TryRemove(permission.RequestId, out _);
                    _logger.LogDebug("Cleaned up expired permission request {RequestId} for account {AccountId}", 
                        permission.RequestId, permission.AccountId);
                    
                    // Fire event for cleanup
                    PermissionClosed?.Invoke(this, permission.RequestId);
                }
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during dialog cleanup");
                return Task.CompletedTask;
            }
        }
        
        private string GetPermissionDescription(ScriptPermission permissions)
        {
            var descriptions = new List<string>();
            
            if ((permissions & ScriptPermission.Debit) != 0)
                descriptions.Add("take money from you");
            if ((permissions & ScriptPermission.TakeControls) != 0)
                descriptions.Add("control your avatar");
            if ((permissions & ScriptPermission.TriggerAnimation) != 0)
                descriptions.Add("animate your avatar");
            if ((permissions & ScriptPermission.Attach) != 0)
                descriptions.Add("attach to your avatar");
            if ((permissions & ScriptPermission.ChangeLinks) != 0)
                descriptions.Add("change its link set");
            if ((permissions & ScriptPermission.TrackCamera) != 0)
                descriptions.Add("control your camera");
            if ((permissions & ScriptPermission.ControlCamera) != 0)
                descriptions.Add("move your camera");
            if ((permissions & ScriptPermission.Teleport) != 0)
                descriptions.Add("teleport you");
            if ((permissions & ScriptPermission.ChangePermissions) != 0)
                descriptions.Add("change its permissions");
            
            return descriptions.Count > 0 ? string.Join(", ", descriptions) : "unknown permissions";
        }
        
        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
    }
}