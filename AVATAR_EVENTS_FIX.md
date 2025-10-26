# Avatar Events Not Flowing to Web Client - FIXED

## Problem Description

**Issue**: The radar system itself was working correctly (avatars visible in sim stats), but when users logged back in and selected an account, the **SignalR broadcasting** of nearby avatar events stopped working. The web client would not receive avatar updates despite the radar detecting avatars properly.

## Root Cause

The issue was that when users log back in and select an account, the **event subscription pipeline** between the `WebRadegastInstance` and the `RadegastBackgroundService` was not being properly refreshed. The avatar events (`AvatarAdded`, `AvatarRemoved`, `AvatarUpdated`) were still being fired by the SL client, but they weren't being relayed to SignalR and broadcast to the web client.

## Solution Implemented

### 1. Added RefreshAccountSubscriptionAsync Method
**File**: `Services/RadegastBackgroundService.cs`

Added a new method to force refresh event subscriptions:

```csharp
public async Task RefreshAccountSubscriptionAsync(Guid accountId)
{
    // Unsubscribe from all existing events
    instance.AvatarAdded -= OnAvatarAdded;
    instance.AvatarRemoved -= OnAvatarRemoved;
    instance.AvatarUpdated -= OnAvatarUpdated;
    // ... other events

    // Re-subscribe to all events
    instance.AvatarAdded += OnAvatarAdded;
    instance.AvatarRemoved += OnAvatarRemoved;
    instance.AvatarUpdated += OnAvatarUpdated;
    // ... other events
}
```

### 2. Added RefreshAvatarEvents SignalR Method
**File**: `Hubs/RadegastHub.cs`

Added a new SignalR method that clients can call to fix avatar event flow:

```csharp
public async Task RefreshAvatarEvents(string accountId)
{
    // Force refresh of event subscriptions
    await backgroundService.RefreshAccountSubscriptionAsync(accountGuid);
    
    // Immediately broadcast current nearby avatars
    var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
    await Clients.Group($"account_{accountId}").NearbyAvatarsUpdated(nearbyAvatars.ToList());
}
```

### 3. Automatic Fix in JoinAccountGroup
**File**: `Hubs/RadegastHub.cs`

Modified the `JoinAccountGroup` method to automatically refresh avatar events when users select an account:

```csharp
// After joining the SignalR group
await backgroundService.RefreshAccountSubscriptionAsync(accountGuid);

// Immediately send current nearby avatars to the joining client
var nearbyAvatars = await instance.GetNearbyAvatarsAsync();
if (avatarList.Count > 0)
{
    await Clients.Caller.NearbyAvatarsUpdated(avatarList);
}
```

## How to Use

### Automatic Fix (Preferred)
The fix is now automatic - when you select an account in the web client, the `JoinAccountGroup` method will automatically refresh the avatar events and send current nearby avatars.

### Manual Fix (If Needed)
If avatar events still aren't flowing, you can manually trigger the fix:

```javascript
// Run in browser console
window.radegastClient.connection.invoke('RefreshAvatarEvents', 'YOUR_ACCOUNT_ID');
```

### Diagnostic Tool
Use the enhanced debug method to verify the fix worked:

```javascript
// Run in browser console to see detailed radar diagnostics
window.radegastClient.connection.invoke('DebugRadarSync', 'YOUR_ACCOUNT_ID');
```

## Technical Details

### Event Flow Pipeline
1. **SL Client** detects nearby avatars → fires `Objects.AvatarUpdate` event
2. **WebRadegastInstance** processes avatar → fires `AvatarAdded`/`AvatarUpdated` events  
3. **RadegastBackgroundService** receives events → broadcasts via SignalR
4. **Web Client** receives SignalR messages → updates radar display

### What Was Broken
Step 3 was failing - the RadegastBackgroundService event handlers were not properly subscribed, so avatar events weren't being broadcast to SignalR.

### What the Fix Does
- Forces re-subscription of all event handlers in RadegastBackgroundService
- Immediately broadcasts current avatar data to restore the display
- Logs the refresh process for debugging
- Provides both automatic and manual triggers

## Verification

After implementing this fix, you should see:

1. **In Server Logs**: Messages about event subscription refresh
2. **In Browser Console**: Radar debug showing avatar events flowing
3. **In Web Client**: Nearby avatars appearing in the radar/people list
4. **Automatic**: Works when switching between accounts without manual intervention

## Files Modified

- `Services/RadegastBackgroundService.cs` - Added RefreshAccountSubscriptionAsync method
- `Hubs/RadegastHub.cs` - Added RefreshAvatarEvents method and automatic refresh in JoinAccountGroup
- `RADAR_DEBUG_GUIDE.md` - Updated with new troubleshooting steps

This fix specifically addresses the issue where "radar is working but web client doesn't get the data" after account selection.