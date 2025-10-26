# Radar/Avatar Sync Debugging Guide

This guide helps debug issues where the client connects and joins account groups but radar/nearby avatars don't appear.

## Problem Description
The logs show:
- Client successfully connects to SignalR hub
- Client successfully joins account group 
- But radar doesn't show avatars and busy/afk status isn't working

**COMMON CAUSE**: Account not logged into Second Life! Check for "No account instance found" errors in server logs.

## Enhanced Debugging Features Added

### Server-Side Debug Methods
Added to `RadegastHub.cs`:

1. **DebugRadarSync(accountId)** - Comprehensive radar diagnostic
   - Checks connection tracking state
   - Verifies account instance status
   - Tests avatar data retrieval
   - Tests both direct and group SignalR broadcasts

### Client-Side Debug Methods
Added to `main.js` and available in browser console:

1. **`window.radegastClient.debugConnectionState()`** - Basic connection info
2. **`window.radegastClient.debugRadarSync()`** - Radar-specific debugging
3. **`window.radegastClient.debugForceAvatarUpdate()`** - Manual avatar request

### Enhanced Logging
Added detailed logging to:
- `RadegastBackgroundService.cs` - Avatar event broadcasting
- `AccountService.cs` - Avatar data retrieval

## Debugging Steps

### Step 0: FIRST CHECK - Is the account logged into Second Life?
**Most radar/sit issues are caused by accounts not being logged into SL:**

1. Check the account status in the web interface - it should show "Online"
2. Look for "No account instance found" errors in server logs
3. If offline, click the LOGIN button (green arrow) next to the account
4. Wait for connection and check server logs for login messages

### Step 0.5: NEW FIX - Avatar Events Not Flowing to Web Client
**If radar is working (you see avatars in stats) but web client doesn't receive updates:**

This is a common issue where the radar itself works fine (avatars detected in sim stats) but the SignalR event broadcasting to the web client has stopped working. This typically happens after logging back in and selecting an account.

**Quick Fix:**
```javascript
// Run in browser console to force refresh avatar events
window.radegastClient.connection.invoke('RefreshAvatarEvents', 'YOUR_ACCOUNT_ID');
```

**What this does:**
- Forces re-subscription of avatar events in the background service
- Immediately broadcasts current nearby avatars to the web client
- Restores the data flow from SL radar to web interface

**Automatic Fix:**
The `JoinAccountGroup` method now automatically refreshes avatar events when you select an account, so this should happen automatically when switching accounts.

### Step 1: Check Basic Connection
Open browser console and run:
```javascript
window.radegastClient.debugConnectionState();
```

### Step 2: Check Radar Sync
Run in browser console:
```javascript
window.radegastClient.debugRadarSync();
```

This will:
- Show client-side avatar state
- Trigger server-side radar diagnostics
- Test both direct and group broadcasts

### Step 3: Manual Avatar Request
Try forcing an avatar update:
```javascript
window.radegastClient.debugForceAvatarUpdate();
```

### Step 4: Check Server Logs
Look for these new log messages:

**Connection Tracking:**
```
[INFO] Tracked connections for account {AccountId}: {Count} [{Connections}]
[INFO] Is current connection {ConnectionId} tracked for account {AccountId}: {IsTracked}
```

**Avatar Broadcasting:**
```
[INFO] Broadcasting avatar added - Account: {AccountId}, Avatar: {AvatarName} ({AvatarId}), Total nearby: {Count}
[INFO] Broadcasting avatar update - Account: {AccountId}, Avatar: {AvatarName} ({AvatarId})
[INFO] Broadcasting avatar removed - Account: {AccountId}, Avatar: {AvatarId}, Remaining nearby: {Count}
```

**Direct Testing:**
```
[INFO] Testing direct broadcast to connection {ConnectionId}
[INFO] Testing group broadcast to account_{AccountId}
```

## Common Issues to Check

### 1. **MOST COMMON: Account Not Logged Into Second Life**
- Check server logs for "No account instance found for {accountId}" errors
- Verify account shows "Online" status in web interface  
- Use LOGIN button to connect account to Second Life
- **This fixes 90% of radar/movement issues**

### 2. SignalR Group Membership
- Verify connection is actually in SignalR group `account_{accountId}`
- Check for connection ID mismatches

### 3. Account Instance State  
- Verify account instance exists and is connected to SL
- Check if avatar events are being fired from the SL client

### 3. Avatar Data Filtering
- Client filters avatars by `avatar.accountId === this.currentAccountId`
- Verify account IDs match exactly (including case)

### 4. Broadcasting Issues
- Check if broadcasts are sent but not received
- Look for JavaScript errors in browser console

### 5. Timing Issues
- Account switching might cause stale data
- Check `isSwitchingAccounts` flag

## Expected Behavior

When working correctly, you should see:
1. Server logs showing avatar events being broadcast
2. Client console showing avatar updates received
3. Radar/People list updating with nearby avatars
4. Minimap showing yellow dots for other avatars

## Next Steps If Issue Persists

1. Compare account IDs in server logs vs client logs
2. Check if avatar events are being fired at all from SL client
3. Verify SignalR group membership persistence
4. Check for JavaScript errors blocking avatar updates
5. Test with multiple accounts to isolate the issue