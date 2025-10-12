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
        void CleanupAccount(Guid accountId);
        event EventHandler<NoticeReceivedEventArgs>? NoticeReceived;
    }

    public class NoticeService : INoticeService
    {
        private readonly ILogger<NoticeService> _logger;
        private readonly IDbContextFactory<RadegastDbContext> _dbContextFactory;

        public event EventHandler<NoticeReceivedEventArgs>? NoticeReceived;

        public NoticeService(ILogger<NoticeService> logger, IDbContextFactory<RadegastDbContext> dbContextFactory)
        {
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<NoticeDto?> ProcessIncomingNoticeAsync(Guid accountId, InstantMessage im)
        {
            try
            {
                NoticeDto? notice = null;
                string sessionId = "";
                string displayMessage = "";

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

        private Task<NoticeDto?> ProcessGroupNoticeAsync(Guid accountId, InstantMessage im)
        {
            try
            {
                // Extract group ID from binary bucket
                var groupId = im.BinaryBucket.Length >= 18 ? new UUID(im.BinaryBucket, 2) : im.FromAgentID;

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

                var notice = new NoticeDto
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = title.Trim(),
                    Message = message.Trim(),
                    FromName = im.FromAgentName,
                    FromId = im.FromAgentID.ToString(),
                    GroupId = groupId.ToString(),
                    Timestamp = DateTime.UtcNow,
                    Type = NoticeType.Group,
                    HasAttachment = hasAttachment,
                    AttachmentName = attachmentName,
                    AttachmentType = attachmentType,
                    AccountId = accountId,
                    RequiresAcknowledgment = false, // Group notices don't typically require acknowledgment
                    IsAcknowledged = true
                };

                return Task.FromResult<NoticeDto?>(notice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing group notice");
                return Task.FromResult<NoticeDto?>(null);
            }
        }

        private Task<NoticeDto?> ProcessGroupNoticeRequestedAsync(Guid accountId, InstantMessage im)
        {
            try
            {
                // This is when someone requests a specific group notice - similar to group notice but requires acknowledgment
                var groupId = im.BinaryBucket.Length >= 18 ? new UUID(im.BinaryBucket, 2) : im.FromAgentID;

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

                var notice = new NoticeDto
                {
                    Id = im.IMSessionID.ToString(), // Use session ID for acknowledgment
                    Title = title.Trim(),
                    Message = message.Trim(),
                    FromName = im.FromAgentName,
                    FromId = im.FromAgentID.ToString(),
                    GroupId = groupId.ToString(),
                    Timestamp = DateTime.UtcNow,
                    Type = NoticeType.Group,
                    HasAttachment = hasAttachment,
                    AttachmentName = attachmentName,
                    AttachmentType = attachmentType,
                    AccountId = accountId,
                    RequiresAcknowledgment = hasAttachment, // Only acknowledge if there's an attachment to accept
                    IsAcknowledged = false
                };

                return Task.FromResult<NoticeDto?>(notice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing group notice requested");
                return Task.FromResult<NoticeDto?>(null);
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
                IsAcknowledged = true
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
                    IsAcknowledged = true
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

                return notices.Select(ConvertToDto);
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

                return notices.Select(ConvertToDto);
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
            var formattedMessage = $"[{timestamp}] {notice.FromName} {notice.Title}";
            
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
    }
}