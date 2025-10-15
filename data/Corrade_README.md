# Corrade Plugin for RadegastWeb

## Overview

The Corrade plugin enables RadegastWeb to respond to whisper commands in Second Life/OpenSimulator, allowing remote control of messaging functionality. This plugin is inspired by the Corrade bot system and provides a secure, configurable way to relay messages between different chat contexts.

**Note:** The plugin is disabled by default and only activates when at least one group is configured in the `data/corrade.json` file.

## Features

- **Account-Specific Processing**: Link Corrade to a specific account to avoid processing whispers on all logged-in accounts
- **Object Command Support**: Optionally allow objects (not just avatars) to send Corrade commands
- **Automatic Activation**: Plugin only runs when groups are configured
- **Whisper Command Processing**: Automatically detects and processes Corrade commands sent via whisper
- **Multi-Entity Support**: Send messages to local chat, groups, or individual avatars
- **Password Protection**: Each group configuration requires a password for security
- **Permission System**: Fine-grained control over what types of messages can be relayed
- **Web Management**: Full configuration management through the web interface
- **Authentication**: All API endpoints require proper authentication

## Command Format

The basic command format follows the URL-encoded parameter style:

```
command=tell&group=GROUP_UUID&password=PASSWORD&entity=ENTITY_TYPE&message=MESSAGE
```

### Parameters

- `command`: Must be "tell" (currently the only supported command)
- `group`: UUID of the group that authorizes this command
- `password`: Password for the specified group
- `entity`: Target entity type (`local`, `group`, or `avatar`)
- `message`: The message to send
- `target`: Target UUID (required for `avatar` entities, optional for `group` entities - defaults to authorizing group)

### Entity Types

1. **local**: Send message to local chat
   - Example: `command=tell&group=GROUP_UUID&password=PASSWORD&entity=local&message=Hello everyone!`

2. **group**: Send message to a group chat
   - Example: `command=tell&group=GROUP_UUID&password=PASSWORD&entity=group&target=TARGET_GROUP_UUID&message=Hello group!`
   - Example (target defaults to authorizing group): `command=tell&group=GROUP_UUID&password=PASSWORD&entity=group&message=Hello my group!`

3. **avatar**: Send instant message to an avatar
   - Example: `command=tell&group=GROUP_UUID&password=PASSWORD&entity=avatar&target=AVATAR_UUID&message=Hello there!`

## Initial Setup

1. **Check Plugin Status**: The plugin starts disabled by default
2. **Access Configuration**: Go to `http://localhost:15269/corrade.html` (requires login)
3. **Add Groups**: Configure at least one group to enable the plugin
4. **Verify Activation**: Plugin will automatically activate when groups are configured

### Default Configuration

The plugin ships with an empty configuration file (`data/corrade.json`):

```json
{
  "linkedAccountId": null,
  "allowObjectCommands": false,
  "groups": []
}
```

### Configuration Options

- **linkedAccountId**: (Optional) UUID of the specific account that should process Corrade whispers. If `null` or empty, all accounts will process whispers (legacy behavior). This is useful when you have multiple accounts logged in but only want one specific account to handle Corrade commands.

- **allowObjectCommands**: (Boolean) Whether to allow objects (scripted objects, HUDs, etc.) to send Corrade commands via whispers. Default is `false` for security reasons. When enabled, both avatars and objects can send commands.

- **groups**: Array of group configurations that can authorize Corrade commands.

### First Group Setup

1. Navigate to the web interface at `/corrade.html`
2. Login with your RadegastWeb credentials
3. Add a new group in the "Add New Group" section:
   - **Group UUID**: The UUID of a group that can authorize commands
   - **Password**: A secure password for this group
   - **Group Name**: Optional display name
   - **Permissions**: Choose what the group can authorize

The plugin will automatically enable once you add the first group.

### Example Configuration

Once configured, `data/corrade.json` will look like this:

```json
{
  "linkedAccountId": "12345678-abcd-1234-abcd-123456789012",
  "allowObjectCommands": true,
  "groups": [
    {
      "groupUuid": "12345678-1234-1234-1234-123456789abc",
      "password": "secure_password_here",
      "groupName": "My Group",
      "allowLocalChat": true,
      "allowGroupRelay": true,
      "allowAvatarIM": true
    }
  ]
}
```

### Group Permissions

Each group configuration supports these permissions:

- `allowLocalChat`: Allow sending messages to local chat
- `allowGroupRelay`: Allow sending messages to other groups
- `allowAvatarIM`: Allow sending instant messages to avatars

### Convenient Defaults

**Group Target Auto-Detection**: When sending to a group entity, if no `target` parameter is provided, the command will automatically use the authorizing group UUID as the target. This makes it easy to send messages to the same group that's authorizing the command without having to specify the target explicitly.

Examples:
- `command=tell&group=ABC123&password=pass&entity=group&message=Hello!` → Sends to group ABC123
- `command=tell&group=ABC123&password=pass&entity=group&target=XYZ789&message=Hello!` → Sends to group XYZ789

## Account-Specific Configuration

### Linking Corrade to a Specific Account

When you have multiple RadegastWeb accounts logged in simultaneously, you may want only one specific account to process Corrade whisper commands. This prevents command processing overhead on all accounts and provides clearer control.

**To link Corrade to a specific account:**

1. Navigate to the web interface at `/corrade.html`
2. In the configuration section, set the "Linked Account ID" field to the UUID of the account that should handle Corrade commands
3. Save the configuration

**Examples:**
- **Linked Account**: Only account `12345678-abcd-1234-abcd-123456789012` processes whispers
- **No Linked Account** (null/empty): All logged-in accounts process whispers (legacy behavior)

### Object Command Support

By default, only avatars can send Corrade whisper commands. However, you can enable object support to allow scripted objects, HUDs, and other non-avatar sources to send commands.

**Security Considerations for Object Commands:**
- Objects can be created by any user in areas that allow building
- Objects can send automated/scripted commands potentially at high frequency
- Enable this feature only if you trust the environment and have appropriate group security

**To enable object commands:**

1. Set `allowObjectCommands` to `true` in the configuration
2. Objects can now whisper Corrade commands using the same format as avatars
3. All normal security checks (group membership, passwords, permissions) still apply

## Command Sources

With the new configuration options, Corrade commands can come from:

1. **Avatars** (always supported): Regular users whispering commands
2. **Objects** (when enabled): Scripted objects, HUDs, vehicles, etc.

All command sources must still:
- Send valid command syntax
- Provide correct group UUID and password
- Meet all security requirements (the receiving account must be in the authorizing group)

## Security Features

1. **Group Membership Verification**: The receiving RadegastWeb account must be a member of the authorizing group
2. **Password Protection**: Each command must include a valid password
3. **Group Membership Validation**: For group messages, the account must be a member of the target group
4. **Permission Checking**: Commands are validated against the group's allowed entity types
5. **Input Validation**: All UUIDs and parameters are validated before processing

### Security Flow

When a whisper command is received:

1. **Plugin Status Check**: Verify the plugin is enabled
2. **Command Parsing**: Parse and validate the command syntax
3. **Group Membership Check**: Verify the receiving account is a member of the authorizing group
4. **Password Verification**: Check the provided password against the group configuration
5. **Permission Check**: Ensure the group allows the requested entity type
6. **Execution**: Process the command if all checks pass

All security failures are logged with warnings for monitoring purposes.

## API Endpoints

All API endpoints require authentication using your RadegastWeb login credentials:

### GET /api/corrade/status
Get plugin status and group count.

**Response:**
```json
{
  "isEnabled": true,
  "linkedAccountId": "12345678-abcd-1234-abcd-123456789012",
  "allowObjectCommands": true,
  "groupCount": 1,
  "groups": [...],
  "lastUpdated": "2025-10-15T10:30:00Z"
}
```

### GET /api/corrade/config
Get current configuration (passwords hidden).

### POST /api/corrade/config
Update the entire configuration.

### POST /api/corrade/config/groups
Add a new group to the configuration.

### DELETE /api/corrade/config/groups/{groupUuid}
Remove a group from the configuration.

### POST /api/corrade/test-command
Test a command without executing it.

## Usage Examples

### Basic Setup

1. Configure a group in `data/corrade.json` or via the web interface
2. Ensure your RadegastWeb account is logged in and connected
3. Have someone whisper a command to your avatar

### Example Whisper Commands

**Send to local chat:**
```
command=tell&group=12345678-1234-1234-1234-123456789abc&password=mypassword&entity=local&message=Hello everyone in local chat!
```

**Send to group (specific target):**
```
command=tell&group=12345678-1234-1234-1234-123456789abc&password=mypassword&entity=group&target=87654321-4321-4321-4321-210987654321&message=Hello group members!
```

**Send to group (same as authorizing group):**
```
command=tell&group=12345678-1234-1234-1234-123456789abc&password=mypassword&entity=group&message=Hello my group!
```

**Send IM to avatar:**
```
command=tell&group=12345678-1234-1234-1234-123456789abc&password=mypassword&entity=avatar&target=11111111-2222-3333-4444-555555555555&message=Hello there!
```

## Error Handling

The plugin provides detailed error messages for common issues:

- `PLUGIN_DISABLED`: Plugin is not enabled (no groups configured)
- `NOT_AUTHORIZED`: Receiving account is not a member of the authorizing group
- `PERMISSION_DENIED`: Invalid password or insufficient permissions
- `ACCOUNT_OFFLINE`: The RadegastWeb account is not connected
- `NOT_GROUP_MEMBER`: Account is not a member of the target group
- `INVALID_ENTITY`: Unknown entity type specified
- `MISSING_TARGET`: Target UUID required but not provided

## Logging

All Corrade command processing is logged with appropriate detail levels:

- **Information**: Successful command processing
- **Warning**: Permission denials and validation failures  
- **Error**: Processing errors and exceptions

## Security Considerations

1. **Password Storage**: Passwords are stored in plain text in the configuration file. Ensure proper file permissions.
2. **Group Membership**: The RadegastWeb account must be a member of any group used for authorization.
3. **Network Security**: Commands are sent via SL whispers, which may be logged by the grid operator.
4. **Access Control**: Only configure trusted groups with appropriate permissions.
5. **Monitoring**: All command attempts are logged, including security violations for monitoring.
6. **Rate Limiting**: Consider implementing rate limiting for high-traffic scenarios (not currently implemented).

### Group Security Model

The security model requires:
- The **RadegastWeb account** must be a member of the authorizing group
- The **group** must be configured in the Corrade plugin
- The **password** must match the configured group password
- For group messages, the **account** must be a member of the target group

This ensures only groups that the RadegastWeb account is a member of can be used for command authorization.

## Troubleshooting

### Common Issues

1. **Plugin not responding**: Check that at least one group is configured and the plugin is enabled
2. **Commands not working**: Check that the account is connected and the whisper was received
3. **"Not authorized" errors**: Ensure the RadegastWeb account is a member of the authorizing group
4. **Permission denied**: Verify the group UUID and password in the configuration
5. **Group messages failing**: Ensure the account is a member of the target group
6. **Invalid command format**: Use the command tester in the web interface to validate syntax

### Debug Steps

1. Check the RadegastWeb logs for Corrade-related messages
2. Use the web interface command tester to validate command syntax
3. Verify group membership through the RadegastWeb groups interface
4. Check the configuration file format and permissions

## Future Enhancements

Potential improvements for future versions:

- Additional command types (status queries, user information)
- Rate limiting and throttling
- Command history and logging
- Integration with external APIs
- Support for encrypted passwords
- Scheduled message sending
- Custom response messages