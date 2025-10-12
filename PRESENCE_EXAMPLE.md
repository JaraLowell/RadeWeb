# Presence Management Example

This example demonstrates how to use the presence management functionality in RadegastWeb.

## Features Implemented

### 1. **Away Status**
- Set manually via API or UI
- Automatically set when browser is closed/hidden
- Uses Second Life `AWAY` animation (same as Radegast)
- Controlled via `Movement.Away` property

### 2. **Busy Status** 
- Set manually via API or UI
- Uses Second Life `BUSY` animation (same as Radegast)

### 3. **Account Switching**
- When you switch from one account to another, the previous account goes "Busy"
- Uses `BUSY` animation to indicate the account is not actively being used

### 4. **Automatic Browser Presence**
- Browser close/tab hidden → All accounts go "Away" (busy disabled)
- Browser return → Away disabled, non-active accounts go "Busy"

## API Endpoints

### Account-Specific Presence
```bash
# Set Away Status
POST /api/accounts/{accountId}/presence/away
Content-Type: application/json
{
  "isEnabled": true
}

# Set Busy Status  
POST /api/accounts/{accountId}/presence/busy
Content-Type: application/json
{
  "isEnabled": true
}

# Set as Active Account (others become unavailable)
POST /api/accounts/{accountId}/presence/active

# Get Presence Status
GET /api/accounts/{accountId}/presence
```

### Global Presence
```bash
# Handle Browser Close
POST /api/presence/browser-close

# Handle Browser Return
POST /api/presence/browser-return

# Set Active Account Globally
POST /api/presence/active-account
Content-Type: application/json
{
  "accountId": "guid-here-or-null"
}
```

## SignalR Events

### Client → Server
```javascript
// Set away status
connection.invoke("SetAwayStatus", accountId, true);

// Set busy status  
connection.invoke("SetBusyStatus", accountId, true);

// Set active account
connection.invoke("SetActiveAccount", accountId);

// Handle browser events
connection.invoke("HandleBrowserClose");
connection.invoke("HandleBrowserReturn");
```

### Server → Client
```javascript
// Presence status changed
connection.on("PresenceStatusChanged", (accountId, status, statusText) => {
    console.log(`Account ${accountId} is now ${statusText}`);
    // Update UI to show new status
});

// Presence errors
connection.on("PresenceError", (error) => {
    console.error("Presence error:", error);
});
```

## Usage Examples

### JavaScript Implementation

```javascript
// Example: Switch between accounts
async function switchToAccount(newAccountId) {
    // This will automatically:
    // 1. Set previous account to "unavailable" 
    // 2. Set new account to "online"
    await connection.invoke("SetActiveAccount", newAccountId);
}

// Example: Manual away toggle
async function toggleAway(accountId, isAway) {
    await connection.invoke("SetAwayStatus", accountId, isAway);
}

// Example: Browser visibility handling (automatic)
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        connection.invoke("HandleBrowserClose");
    } else {
        connection.invoke("HandleBrowserReturn");
    }
});
```

### Behavior Flow

#### Account Switching
1. User has accounts A, B, C logged in
2. User is actively using account A
3. User switches to account B
4. **Result**: A→Busy, B→Online, C→Busy

#### Browser Close/Hide
1. User has accounts A (active), B, C logged in  
2. User closes browser or switches to another app
3. **Result**: A→Away, B→Away, C→Away (busy disabled)

#### Browser Return
1. User returns to browser
2. User was previously active on account A
3. **Result**: A→Online, B→Busy, C→Busy

#### Manual Status Setting
- Away and Busy can be manually toggled
- Manual settings persist until changed or overridden by browser events
- Busy is also set automatically by account switching

## Status Priority

The status priority (highest to lowest):
1. **Away** (browser closed/hidden)
2. **Busy** (manually set or account switching)
3. **Online** (default active state)

## Technical Implementation

### PresenceService
- Manages all presence state for all accounts
- Handles automatic status switching logic
- Integrates with LibreMetaverse's animation system
- Provides events for UI updates

### SignalR Integration
- Real-time status updates to all connected clients
- Bidirectional communication for presence changes
- Automatic reconnection handling

### Browser Detection
- Uses Page Visibility API
- Handles beforeunload events
- Graceful degradation for older browsers

## Visual Indicators

The UI shows different presence states with:
- **Green dot**: Online
- **Yellow dot**: Away  
- **Red dot**: Busy

Status text appears next to account names showing the current state.