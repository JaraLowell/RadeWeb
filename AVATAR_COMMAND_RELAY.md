# Avatar Command Relay Feature

This document describes the Avatar Command Relay feature that allows remote control of RadegastWeb bot accounts through whisper commands from a designated relay avatar.

## Overview

When an account has an `AvatarRelayUuid` configured, the bot will listen for specific whisper commands from that avatar and execute corresponding actions. This enables remote control of the bot's behavior in Second Life.

## Configuration

To enable command relay for an account:

1. Set the `AvatarRelayUuid` field in the account configuration to the UUID of the avatar that should be allowed to send commands
2. Ensure the account is connected and logged in to Second Life
3. The relay avatar can now send whisper commands to control the bot

## Supported Commands

### Sit Command
**Syntax:** `//sit <object-uuid>`
**Description:** Makes the bot sit on the specified object
**Example:** `//sit 12345678-1234-1234-1234-123456789abc`

**Notes:**
- The object must exist in the current region
- The bot must be able to reach the object
- If the object is not found, an error message will be sent back to the relay avatar

### Stand Command
**Syntax:** `//stand`
**Description:** Makes the bot stand up from its current position
**Example:** `//stand`

**Notes:**
- Stops all animations and returns the bot to standing position
- Works whether sitting on ground or on an object

### Say Command
**Syntax:** `//say <message>`
**Description:** Makes the bot say a message in local chat
**Example:** `//say Hello everyone!`

**Notes:**
- Message is sent to local chat channel (channel 0)
- Message appears as if spoken by the bot avatar
- Standard Second Life chat range limitations apply

### Instant Message Command
**Syntax:** `//im <avatar-uuid> <message>`
**Description:** Sends an instant message to the specified avatar
**Example:** `//im 87654321-4321-4321-4321-210987654321 Hello there!`

**Notes:**
- Avatar UUID must be valid Second Life avatar UUID
- Message is sent as a private instant message
- Standard Second Life IM limitations apply

## Security Features

1. **UUID Verification:** Only whispers from the exact UUID specified in `AvatarRelayUuid` are processed
2. **Connection Check:** Commands are only processed when the bot account is connected
3. **Feedback Messages:** Success/error feedback is sent back to the relay avatar via IM
4. **Command Validation:** All commands are validated before execution

## Feedback System

The bot provides feedback for all command attempts:

- **Success:** `✓ <success message>` sent via IM to relay avatar
- **Error:** `✗ <error message>` sent via IM to relay avatar

Example feedback messages:
- `✓ Sitting on object 12345678-1234-1234-1234-123456789abc`
- `✗ Failed to sit on object - object not found or unreachable`
- `✓ Said in local chat: Hello everyone!`
- `✗ Unknown command: //dance. Supported commands: //sit <uuid>, //stand, //say <message>, //im <uuid> <message>`

## Implementation Details

### Processing Pipeline
Commands are processed through the chat processing pipeline with priority 15, ensuring they are handled before general message processing but after basic filtering.

### Error Handling
- Invalid UUIDs are validated before attempting operations
- Network connectivity is checked before sending commands
- All errors are logged and reported back to the relay avatar
- Failed commands do not crash the processing pipeline

### Logging
All command relay activities are logged with appropriate log levels:
- Info: Successful command execution
- Warning: Failed command execution with details
- Debug: Commands from non-relay avatars (ignored)

## Testing

Use the test endpoints to verify command relay functionality:

### Test Command Execution
```http
POST /api/test/command-relay/test-command
Content-Type: application/json

{
  "accountId": "account-guid-here",
  "command": "//say Hello from relay test!",
  "senderName": "Test Relay Avatar"
}
```

### Get Configuration
```http
GET /api/test/command-relay/config/{accountId}
```

### List Available Commands
```http
GET /api/test/command-relay/commands
```

## Usage Examples

1. **Setting up a relay avatar:**
   - Configure bot account with AvatarRelayUuid = "12345678-1234-1234-1234-123456789abc"
   - From avatar 12345678-1234-1234-1234-123456789abc, whisper to the bot

2. **Remote control session:**
   ```
   Relay Avatar whispers: "//say I'm now under remote control"
   Bot responds via IM: "✓ Said in local chat: I'm now under remote control"
   
   Relay Avatar whispers: "//sit 87654321-4321-4321-4321-210987654321"
   Bot responds via IM: "✓ Sitting on object 87654321-4321-4321-4321-210987654321"
   
   Relay Avatar whispers: "//stand"
   Bot responds via IM: "✓ Standing up"
   ```

3. **Error handling:**
   ```
   Relay Avatar whispers: "//sit invalid-uuid"
   Bot responds via IM: "✗ Invalid UUID format: invalid-uuid"
   
   Relay Avatar whispers: "//dance"
   Bot responds via IM: "✗ Unknown command: //dance. Supported commands: //sit <uuid>, //stand, //say <message>, //im <uuid> <message>"
   ```

## Troubleshooting

### Commands Not Working
1. Verify AvatarRelayUuid is set correctly in account configuration
2. Ensure bot account is connected to Second Life
3. Check that commands are being sent as whispers, not other chat types
4. Verify the relay avatar UUID matches exactly (case-sensitive)

### No Feedback Messages
1. Check bot connection status
2. Verify AvatarRelayUuid is not the bot's own UUID (cannot IM self)
3. Check Second Life IM delivery status

### Commands Partially Working
1. For sit commands: verify object exists in current region
2. For IM commands: verify target avatar UUID is valid
3. Check bot permissions and region restrictions

## Related Features

- **IM Relay:** Incoming IMs are relayed to the AvatarRelayUuid
- **Proximity Detection:** Nearby avatar alerts sent to AvatarRelayUuid
- **Corrade Commands:** Traditional Corrade-style commands via whispers