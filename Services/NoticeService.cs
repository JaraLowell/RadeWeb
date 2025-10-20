using Microsoft.EntityFrameworkCore;
using OpenMetaverse;
using RadegastWeb.Data;
using RadegastWeb.Models;

namespace RadegastWeb.Services
{
    public interface INoticeService
    {
        Task<NoticeDto?> ProcessIncomingNoticeAsync(Guid accountId, InstantMessage im);
        Task<NoticeDto?> ProcessRegionAlertAsync(Guid accountId, string message, string? fromName = null);
        Task AcknowledgeNoticeAsync(Guid accountId, string noticeId);
        Task<IEnumerable<NoticeDto>> GetRecentNoticesAsync(Guid accountId, int count = 20);
        Task<IEnumerable<NoticeDto>> GetUnreadNoticesAsync(Guid accountId);
        Task MarkNoticeAsReadAsync(Guid accountId, string noticeId);
        Task DismissNoticeAsync(Guid accountId, string noticeId);
        Task SendNoticeAcknowledgmentToSecondLifeAsync(Guid accountId, InstantMessage originalMessage, bool hasAttachment);
        void CleanupAccount(Guid accountId);
        event EventHandler<NoticeReceivedEventArgs>? NoticeReceived;
    }

    public class NoticeService : INoticeService
    {
        private readonly ILogger<NoticeService> _logger;
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;
        private readonly IGroupService _groupService;
        private readonly ISLTimeService _slTimeService;

        public event EventHandler<NoticeReceivedEventArgs>? NoticeReceived;

        public NoticeService(ILogger<NoticeService> logger, IDbContextFactory<RadegastDbContext> dbContextFactory, IGroupService groupService, ISLTimeService slTimeService)
        {
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _groupService = groupService;
            _slTimeService = slTimeService;
        }

        public async Task<NoticeDto?> ProcessIncomingNoticeAsync(Guid accountId, InstantMessage im)
        {
            try
            {
                // Log all incoming instant messages to understand what's being processed
                _logger.LogDebug("Processing incoming IM for notice check - AccountId: {AccountId}, Dialog: {Dialog}, FromAgentName: {FromAgentName}, Message: {Message}", 
                    accountId, im.Dialog, im.FromAgentName, im.Message);

                NoticeDto? notice = null;
                string sessionId = "";
                string displayMessage = "";

                // Filter out status-related messages that shouldn't be processed as notices
                if (IsStatusRelatedMessage(im))
                {
                    _logger.LogDebug("Skipping status-related message from being processed as notice - Dialog: {Dialog}, Message: {Message}", 
                        im.Dialog, im.Message);
                    return null;
                }

                switch (im.Dialog)
                {
                    case InstantMessageDialog.GroupNotice:
                        notice = await ProcessGroupNoticeAsync(accountId, im);
                        if (notice != null)
                        {
                            sessionId = $"group-{notice.GroupId}";
                            displayMessage = FormatGroupNoticeForChat(notice);
                        }
                        break;

                    case InstantMessageDialog.GroupNoticeRequested:
                        notice = await ProcessGroupNoticeRequestedAsync(accountId, im);
                        if (notice != null)
                        {
                            sessionId = $"group-{notice.GroupId}";
                            displayMessage = FormatGroupNoticeForChat(notice);
                        }
                        break;

                    // Region notices typically come as AlertMessage events, not IM
                    // But we can handle them here if they come as MessageFromAgent from "Second Life"
                    case InstantMessageDialog.MessageFromAgent:
                        if (im.FromAgentName == "Second Life")
                        {
                            notice = await ProcessRegionNoticeAsync(accountId, im);
                            if (notice != null)
                            {
                                sessionId = "local-chat";
                                displayMessage = FormatRegionNoticeForChat(notice);
                            }
                        }
                        break;

                    default:
                        // Log unexpected dialog types that are reaching this processor
                        _logger.LogDebug("Unexpected IM dialog type {Dialog} reached notice processor - FromAgentName: {FromAgentName}, Message: {Message}", 
                            im.Dialog, im.FromAgentName, im.Message);
                        break;
                }

                if (notice != null)
                {
                    // Save to database
                    await SaveNoticeAsync(notice);

                    // Fire the event
                    NoticeReceived?.Invoke(this, new NoticeReceivedEventArgs(notice, sessionId, displayMessage));

                    _logger.LogInformation("Processed {NoticeType} notice from {FromName}: {Title}", 
                        notice.Type, notice.FromName, notice.Title);
                }

                return notice;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing incoming notice for account {AccountId}", accountId);
                return null;
            }
        }

        private bool IsStatusRelatedMessage(InstantMessage im)
        {
            // Check for known status-related messages that shouldn't be processed as notices
            if (im.Dialog == InstantMessageDialog.FriendshipOffered ||
                im.Dialog == InstantMessageDialog.FriendshipAccepted ||
                im.Dialog == InstantMessageDialog.FriendshipDeclined)
            {
                return true;
            }

            // Check for specific status messages by content - these are often MessageFromAgent type
            if (im.FromAgentName == "Second Life")
            {
                var message = im.Message?.ToLowerInvariant() ?? "";
                if (message.Contains("is now online") || 
                    message.Contains("is now offline") ||
                    message.Contains("away") ||
                    message.Contains("busy") ||
                    message.Contains("status"))
                {
                    return true;
                }
            }

            // Also check for messages that look like status updates from other agents
            if (!string.IsNullOrEmpty(im.Message))
            {
                var message = im.Message.ToLowerInvariant();
                if ((message.Contains("away") || message.Contains("busy") || message.Contains("online")) &&
                    (message.Contains("status") || message.Length < 50)) // Short messages are likely status updates
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<NoticeDto?> ProcessGroupNoticeAsync(Guid accountId, InstantMessage im)
        {
            try
            {
                // Extract group ID from binary bucket
                var groupId = im.BinaryBucket.Length >= 18 ? new UUID(im.BinaryBucket, 2) : im.FromAgentID;

                // Check if this group is ignored
                var isIgnored = await _groupService.IsGroupIgnoredAsync(accountId, groupId.ToString());
                if (isIgnored)
                {
                    _logger.LogDebug("Skipping group notice from ignored group {GroupId} on account {AccountId}", 
                        groupId, accountId);
                    return null;
                }

                // Parse the notice message (format: "title|message")
                var parts = im.Message.Split('|', 2);
                var title = parts.Length > 0 ? parts[0] : "Group Notice";
                var message = parts.Length > 1 ? parts[1] : im.Message;

                // Check for attachment
                bool hasAttachment = false;
                string? attachmentName = null;
                string? attachmentType = null;

                if (im.BinaryBucket.Length > 18 && im.BinaryBucket[0] != 0)
                {
                    hasAttachment = true;
                    var assetType = (AssetType)im.BinaryBucket[1];
                    attachmentType = assetType.ToString();
                    
                    if (im.BinaryBucket.Length > 18)
                    {
                        attachmentName = System.Text.Encoding.UTF8.GetString(im.BinaryBucket, 18, im.BinaryBucket.Length - 18);
                        attachmentName = attachmentName.TrimEnd('\0'); // Remove null terminators
                    }
                }

                // Get group name from group cache
                var groupName = await _groupService.GetGroupNameAsync(accountId, groupId.ToString(), "Unknown Group");

                var notice = new NoticeDto
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = title.Trim(),
                    Message = message.Trim(),
                    FromName = im.FromAgentName,
                    FromId = im.FromAgentID.ToString(),
                    GroupId = groupId.ToString(),
                    GroupName = groupName,
                    Timestamp = DateTime.UtcNow,
                    Type = NoticeType.Group,
                    HasAttachment = hasAttachment,
                    AttachmentName = attachmentName,
                    AttachmentType = attachmentType,
                    AccountId = accountId,
                    RequiresAcknowledgment = hasAttachment, // Group notices with attachments require acknowledgment
                    IsAcknowledged = !hasAttachment, // Auto-acknowledge notices without attachments
                    SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss"),
                    SLTDateTime = _slTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, HH:mm:ss")
                };

                return notice;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing group notice");
                return null;
            }
        }

        private async Task<NoticeDto?> ProcessGroupNoticeRequestedAsync(Guid accountId, InstantMessage im)
        {
            try
            {
                // This is when someone requests a specific group notice - similar to group notice but requires acknowledgment
                var groupId = im.BinaryBucket.Length >= 18 ? new UUID(im.BinaryBucket, 2) : im.FromAgentID;

                // Check if this group is ignored
                var isIgnored = await _groupService.IsGroupIgnoredAsync(accountId, groupId.ToString());
                if (isIgnored)
                {
                    _logger.LogDebug("Skipping group notice requested from ignored group {GroupId} on account {AccountId}", 
                        groupId, accountId);
                    return null;
                }

                var parts = im.Message.Split('|', 2);
                var title = parts.Length > 0 ? parts[0] : "Group Notice";
                var message = parts.Length > 1 ? parts[1] : im.Message;

                // Check for attachment (similar to group notice)
                bool hasAttachment = false;
                string? attachmentName = null;
                string? attachmentType = null;

                if (im.BinaryBucket.Length > 18 && im.BinaryBucket[0] != 0)
                {
                    hasAttachment = true;
                    var assetType = (AssetType)im.BinaryBucket[1];
                    attachmentType = assetType.ToString();
                    
                    if (im.BinaryBucket.Length > 18)
                    {
                        attachmentName = System.Text.Encoding.UTF8.GetString(im.BinaryBucket, 18, im.BinaryBucket.Length - 18);
                        attachmentName = attachmentName.TrimEnd('\0');
                    }
                }

                // Get group name from group cache
                var groupName = await _groupService.GetGroupNameAsync(accountId, groupId.ToString(), "Unknown Group");

                var notice = new NoticeDto
                {
                    Id = im.IMSessionID.ToString(), // Use session ID for acknowledgment
                    Title = title.Trim(),
                    Message = message.Trim(),
                    FromName = im.FromAgentName,
                    FromId = im.FromAgentID.ToString(),
                    GroupId = groupId.ToString(),
                    GroupName = groupName,
                    Timestamp = DateTime.UtcNow,
                    Type = NoticeType.Group,
                    HasAttachment = hasAttachment,
                    AttachmentName = attachmentName,
                    AttachmentType = attachmentType,
                    AccountId = accountId,
                    RequiresAcknowledgment = true, // Always require acknowledgment for GroupNoticeRequested
                    IsAcknowledged = false,
                    SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss"),
                    SLTDateTime = _slTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, HH:mm:ss")
                };

                return notice;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing group notice requested");
                return null;
            }
        }

        private Task<NoticeDto?> ProcessRegionNoticeAsync(Guid accountId, InstantMessage im)
        {
            // Region notices from "Second Life" system
            var notice = new NoticeDto
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Region Notice",
                Message = im.Message,
                FromName = "Second Life",
                FromId = UUID.Zero.ToString(),
                Timestamp = DateTime.UtcNow,
                Type = NoticeType.Region,
                HasAttachment = false,
                AccountId = accountId,
                RequiresAcknowledgment = false,
                IsAcknowledged = true,
                SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss"),
                SLTDateTime = _slTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, HH:mm:ss")
            };

            return Task.FromResult<NoticeDto?>(notice);
        }

        public async Task<NoticeDto?> ProcessRegionAlertAsync(Guid accountId, string message, string? fromName = null)
        {
            try
            {
                var notice = new NoticeDto
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = "Region Notice",
                    Message = message,
                    FromName = fromName ?? "System",
                    FromId = UUID.Zero.ToString(),
                    Timestamp = DateTime.UtcNow,
                    Type = NoticeType.Region,
                    HasAttachment = false,
                    AccountId = accountId,
                    RequiresAcknowledgment = false,
                    IsAcknowledged = true,
                    SLTTime = _slTimeService.FormatSLT(DateTime.UtcNow, "HH:mm:ss"),
                    SLTDateTime = _slTimeService.FormatSLTWithDate(DateTime.UtcNow, "MMM dd, HH:mm:ss")
                };

                // Save to database
                await SaveNoticeAsync(notice);

                // Fire the event for region notice (goes to local chat)
                var displayMessage = FormatRegionNoticeForChat(notice);
                NoticeReceived?.Invoke(this, new NoticeReceivedEventArgs(notice, "local-chat", displayMessage));

                return notice;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing region alert for account {AccountId}", accountId);
                return null;
            }
        }

        public async Task AcknowledgeNoticeAsync(Guid accountId, string noticeId)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                var notice = await context.Notices
                    .FirstOrDefaultAsync(n => n.Id == Guid.Parse(noticeId) && n.AccountId == accountId);

                if (notice != null)
                {
                    notice.IsAcknowledged = true;
                    await context.SaveChangesAsync();
                    _logger.LogInformation("Acknowledged notice {NoticeId} for account {AccountId}", noticeId, accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acknowledging notice {NoticeId} for account {AccountId}", noticeId, accountId);
            }
        }

        public async Task<IEnumerable<NoticeDto>> GetRecentNoticesAsync(Guid accountId, int count = 20)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                var notices = await context.Notices
                    .Where(n => n.AccountId == accountId)
                    .OrderByDescending(n => n.Timestamp)
                    .Take(count)
                    .ToListAsync();

                var dtos = new List<NoticeDto>();
                foreach (var notice in notices)
                {
                    dtos.Add(await ConvertToDtoAsync(notice));
                }

                return dtos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent notices for account {AccountId}", accountId);
                return Enumerable.Empty<NoticeDto>();
            }
        }

        public async Task<IEnumerable<NoticeDto>> GetUnreadNoticesAsync(Guid accountId)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                var notices = await context.Notices
                    .Where(n => n.AccountId == accountId && !n.IsRead)
                    .OrderByDescending(n => n.Timestamp)
                    .ToListAsync();

                var dtos = new List<NoticeDto>();
                foreach (var notice in notices)
                {
                    dtos.Add(await ConvertToDtoAsync(notice));
                }

                return dtos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting unread notices for account {AccountId}", accountId);
                return Enumerable.Empty<NoticeDto>();
            }
        }

        public async Task MarkNoticeAsReadAsync(Guid accountId, string noticeId)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                var notice = await context.Notices
                    .FirstOrDefaultAsync(n => n.Id == Guid.Parse(noticeId) && n.AccountId == accountId);

                if (notice != null)
                {
                    notice.IsRead = true;
                    await context.SaveChangesAsync();
                    _logger.LogInformation("Marked notice {NoticeId} as read for account {AccountId}", noticeId, accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking notice {NoticeId} as read for account {AccountId}", noticeId, accountId);
            }
        }

        public async Task DismissNoticeAsync(Guid accountId, string noticeId)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                var notice = await context.Notices
                    .FirstOrDefaultAsync(n => n.Id == Guid.Parse(noticeId) && n.AccountId == accountId);

                if (notice != null)
                {
                    context.Notices.Remove(notice);
                    await context.SaveChangesAsync();
                    _logger.LogInformation("Dismissed notice {NoticeId} for account {AccountId}", noticeId, accountId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dismissing notice {NoticeId} for account {AccountId}", noticeId, accountId);
            }
        }

        private async Task SaveNoticeAsync(NoticeDto noticeDto)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                
                var notice = new Notice
                {
                    Id = Guid.Parse(noticeDto.Id),
                    AccountId = noticeDto.AccountId,
                    Title = noticeDto.Title,
                    Message = noticeDto.Message,
                    FromName = noticeDto.FromName,
                    FromId = noticeDto.FromId,
                    GroupId = noticeDto.GroupId,
                    GroupName = noticeDto.GroupName,
                    Timestamp = noticeDto.Timestamp,
                    Type = noticeDto.Type.ToString(),
                    HasAttachment = noticeDto.HasAttachment,
                    AttachmentName = noticeDto.AttachmentName,
                    AttachmentType = noticeDto.AttachmentType,
                    RequiresAcknowledgment = noticeDto.RequiresAcknowledgment,
                    IsAcknowledged = noticeDto.IsAcknowledged,
                    IsRead = false // New notices are always unread
                };

                context.Notices.Add(notice);
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving notice to database");
                throw;
            }
        }

        private async Task<NoticeDto> ConvertToDtoAsync(Notice notice)
        {
            var dto = new NoticeDto
            {
                Id = notice.Id.ToString(),
                AccountId = notice.AccountId,
                Title = notice.Title,
                Message = notice.Message,
                FromName = notice.FromName,
                FromId = notice.FromId,
                GroupId = notice.GroupId,
                GroupName = notice.GroupName,
                Timestamp = notice.Timestamp,
                Type = Enum.Parse<NoticeType>(notice.Type),
                HasAttachment = notice.HasAttachment,
                AttachmentName = notice.AttachmentName,
                AttachmentType = notice.AttachmentType,
                RequiresAcknowledgment = notice.RequiresAcknowledgment,
                IsAcknowledged = notice.IsAcknowledged,
                IsRead = notice.IsRead,
                SLTTime = _slTimeService.FormatSLT(notice.Timestamp, "HH:mm:ss"),
                SLTDateTime = _slTimeService.FormatSLTWithDate(notice.Timestamp, "MMM dd, HH:mm:ss")
            };

            // If GroupName is missing but GroupId is present, try to get it from group cache
            if (string.IsNullOrEmpty(dto.GroupName) && !string.IsNullOrEmpty(dto.GroupId))
            {
                dto.GroupName = await _groupService.GetGroupNameAsync(notice.AccountId, dto.GroupId, "Unknown Group");
            }

            return dto;
        }

        private NoticeDto ConvertToDto(Notice notice)
        {
            return new NoticeDto
            {
                Id = notice.Id.ToString(),
                AccountId = notice.AccountId,
                Title = notice.Title,
                Message = notice.Message,
                FromName = notice.FromName,
                FromId = notice.FromId,
                GroupId = notice.GroupId,
                GroupName = notice.GroupName,
                Timestamp = notice.Timestamp,
                Type = Enum.Parse<NoticeType>(notice.Type),
                HasAttachment = notice.HasAttachment,
                AttachmentName = notice.AttachmentName,
                AttachmentType = notice.AttachmentType,
                RequiresAcknowledgment = notice.RequiresAcknowledgment,
                IsAcknowledged = notice.IsAcknowledged,
                IsRead = notice.IsRead
            };
        }

        private string FormatGroupNoticeForChat(NoticeDto notice)
        {
            var timestamp = notice.Timestamp.ToString("HH:mm");
            var groupPrefix = !string.IsNullOrEmpty(notice.GroupName) ? $"[{notice.GroupName}] " : "";
            var formattedMessage = $"[{timestamp}] {groupPrefix}{notice.FromName} {notice.Title}";
            
            if (!string.IsNullOrEmpty(notice.Message))
            {
                formattedMessage += $"\n{notice.Message}";
            }

            if (notice.HasAttachment && !string.IsNullOrEmpty(notice.AttachmentName))
            {
                formattedMessage += $"\n📎 Attachment: {notice.AttachmentName}";
            }

            return formattedMessage;
        }

        private string FormatRegionNoticeForChat(NoticeDto notice)
        {
            var timestamp = notice.Timestamp.ToString("HH:mm");
            return $"[{timestamp}] {notice.FromName} {notice.Title}\n{notice.Message}";
        }

        public void CleanupAccount(Guid accountId)
        {
            // Database notices persist even after cleanup, which is what we want
            // This method is kept for interface compatibility
            _logger.LogInformation("Notice cleanup requested for account {AccountId} - notices persisted in database", accountId);
        }

        public async Task SendNoticeAcknowledgmentToSecondLifeAsync(Guid accountId, InstantMessage originalMessage, bool hasAttachment)
        {
            try
            {
                // This method is called by WebRadegastInstance to send the actual acknowledgment to Second Life
                // The WebRadegastInstance has access to the GridClient to send the IM response
                _logger.LogInformation("Notice acknowledgment requested for account {AccountId}, message from {FromAgent}, hasAttachment: {HasAttachment}", 
                    accountId, originalMessage.FromAgentName, hasAttachment);
                
                // The actual IM sending is handled by WebRadegastInstance since it has the GridClient
                // This method is here for interface completeness and logging
                
                // Mark as acknowledged in database if we have the notice ID
                if (originalMessage.IMSessionID != UUID.Zero)
                {
                    await AcknowledgeNoticeAsync(accountId, originalMessage.IMSessionID.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing notice acknowledgment for account {AccountId}", accountId);
            }
        }
    }
}