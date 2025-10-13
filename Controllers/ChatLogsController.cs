using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatLogsController : ControllerBase
    {
        private readonly IChatLogService _chatLogService;
        private readonly ILogger<ChatLogsController> _logger;

        public ChatLogsController(IChatLogService chatLogService, ILogger<ChatLogsController> logger)
        {
            _chatLogService = chatLogService;
            _logger = logger;
        }

        /// <summary>
        /// Get list of all chat log files for a specific account
        /// </summary>
        [HttpGet("{accountId}/files")]
        public async Task<ActionResult<IEnumerable<string>>> GetChatLogFiles(Guid accountId)
        {
            try
            {
                var files = await _chatLogService.GetChatLogFilesAsync(accountId);
                return Ok(files);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat log files for account {AccountId}", accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Read content from a specific chat log file
        /// </summary>
        [HttpGet("{accountId}/files/{fileName}")]
        public async Task<ActionResult<string>> GetChatLogContent(Guid accountId, string fileName, [FromQuery] int maxLines = 100)
        {
            try
            {
                // Basic validation of file name (security check)
                if (string.IsNullOrEmpty(fileName) || 
                    fileName.Contains("..") || 
                    fileName.Contains("/") || 
                    fileName.Contains("\\") ||
                    !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Invalid file name");
                }

                var content = await _chatLogService.ReadChatLogAsync(accountId, fileName, maxLines);
                return Ok(new { fileName, content, maxLines });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading chat log {FileName} for account {AccountId}", fileName, accountId);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get the file path for a specific chat type and session
        /// </summary>
        [HttpGet("{accountId}/path")]
        public async Task<ActionResult<string>> GetChatLogPath(Guid accountId, [FromQuery] string chatType, [FromQuery] string? sessionName = null)
        {
            try
            {
                if (string.IsNullOrEmpty(chatType))
                {
                    return BadRequest("Chat type is required");
                }

                var path = await _chatLogService.GetChatLogPathAsync(accountId, chatType, sessionName);
                return Ok(new { chatType, sessionName, path = Path.GetFileName(path) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat log path for account {AccountId}, chatType {ChatType}", accountId, chatType);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}