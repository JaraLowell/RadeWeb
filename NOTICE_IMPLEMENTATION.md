# Notice Handling Implementation for RadegastWeb

## Overview

This implementation adds comprehensive notice handling to RadegastWeb, supporting both group notices and region notices as per Second Life protocol specifications. The system automatically acknowledges notices when appropriate and displays them in the appropriate chat channels with special styling.

## Key Features

### 1. Notice Types Supported
- **Group Notices** (`InstantMessageDialog.GroupNotice`) - Displayed in group chat
- **Group Notice Requests** (`InstantMessageDialog.GroupNoticeRequested`) - Displayed in group chat with optional acknowledgment
- **Region Notices** - Displayed in local chat (from system alerts or "Second Life" IMs)

### 2. Notice Formatting
All notices are displayed with the format: `[time] [from] [topic] \n [msg]` as requested, with:
- White text on `#164482` background
- Special CSS class `.chat-message.notice` for styling
- Proper parsing of notice title and message from the `|` separated format

### 3. Automatic Acknowledgment
- Group notices with attachments that require acknowledgment are handled automatically
- Users can accept attachments through a modal dialog
- Acknowledgment messages are sent back to the server using the proper `InstantMessageDialog` types

## Implementation Details

### Backend Components

#### 1. NoticeService (`Services/NoticeService.cs`)
- **Interface**: `INoticeService`
- **Key Methods**:
  - `ProcessIncomingNoticeAsync()` - Processes incoming notices from various dialog types
  - `ProcessRegionAlertAsync()` - Handles region-wide alerts
  - `AcknowledgeNoticeAsync()` - Sends acknowledgment for notices requiring it
  - `GetRecentNoticesAsync()` - Retrieves notice history

#### 2. NoticeDto Model (`Models/NoticeDto.cs`)
```csharp
public class NoticeDto
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Message { get; set; }
    public string FromName { get; set; }
    public string FromId { get; set; }
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    public DateTime Timestamp { get; set; }
    public NoticeType Type { get; set; } // Group, Region, System
    public bool HasAttachment { get; set; }
    public string? AttachmentName { get; set; }
    public string? AttachmentType { get; set; }
    public bool RequiresAcknowledgment { get; set; }
    public bool IsAcknowledged { get; set; }
}
```

#### 3. WebRadegastInstance Updates
- Added notice event handling in `Self_IM()` method
- Integrated `NoticeService` for processing
- Added `NoticeReceived` event for real-time updates
- Updated alert message handling for region notices

### Frontend Components

#### 1. CSS Styling (`wwwroot/css/main.css`)
```css
.chat-message.notice {
    border-left-color: #164482;
    background-color: var(--notice-bg);
    color: white;
    padding: 8px 12px;
    border-radius: 4px;
    margin: 4px 0;
}
```

#### 2. JavaScript Handling (`wwwroot/js/main.js`)
- **SignalR Events**: `NoticeReceived`, `RecentNoticesLoaded`
- **Notice Modal**: Bootstrap modal for notices with attachments
- **Acknowledgment**: `acknowledgeNotice()` method for user interaction

### Message Flow

#### Group Notice Flow
1. Group notice received via `InstantMessageDialog.GroupNotice` or `GroupNoticeRequested`
2. `NoticeService.ProcessIncomingNoticeAsync()` parses the message
3. Notice is formatted and sent to appropriate group chat channel
4. If attachment present, modal dialog shown for acknowledgment
5. User can accept attachment, triggering acknowledgment back to server

#### Region Notice Flow
1. Region notice received via `AlertMessageEventArgs` or "Second Life" IM
2. `NoticeService.ProcessRegionAlertAsync()` processes the message
3. Notice is formatted and sent to local chat channel
4. No acknowledgment required for region notices

## Configuration

### Service Registration (`Program.cs`)
```csharp
builder.Services.AddSingleton<INoticeService, NoticeService>();
```

### CSS Variables
```css
:root {
    --notice-bg: #164482; /* Notice background color */
}
```

## Usage Examples

### Receiving a Group Notice
When a group notice is received:
1. Appears in the group chat tab with blue background
2. Shows title, sender, message, and attachment info if present
3. If attachment requires acceptance, modal dialog appears
4. User can click "Accept Attachment" to acknowledge

### Receiving a Region Notice
When a region-wide notice is received:
1. Appears in local chat with blue background
2. Shows system sender and message
3. No user interaction required

## Testing

To test the notice handling:

1. **Group Notices**: Join a group and have someone send a group notice with/without attachment
2. **Region Notices**: Trigger region restart warnings or other system alerts
3. **Acknowledgment**: Test accepting attachments from group notices

## Technical Notes

### Notice Message Parsing
- Group notices use format: `"title|message"` in the IM message field
- Binary bucket contains group ID and attachment information
- Attachment type is determined from `AssetType` in binary bucket[1]

### Acknowledgment Protocol
Following Radegast's implementation:
- `GroupNoticeInventoryAccepted` dialog for accepting attachments
- Session ID from original notice used for acknowledgment matching
- Proper destination folder ID sent in binary bucket

### Error Handling
- All notice processing wrapped in try-catch blocks
- Graceful degradation if notice parsing fails
- Logging for debugging notice issues

## Future Enhancements

1. **Notice History Panel**: UI panel showing recent notices
2. **Notice Preferences**: User settings for notice handling
3. **Sound Notifications**: Audio alerts for important notices
4. **Notice Search**: Search through notice history
5. **Batch Operations**: Mark multiple notices as read

---

This implementation provides complete notice handling compatible with Second Life protocol specifications while maintaining the requested formatting and styling requirements.