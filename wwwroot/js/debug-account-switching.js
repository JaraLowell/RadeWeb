/**
 * Debug utilities for account switching issues
 * Call these functions in the browser console to debug account switching problems
 */

// Debug account switching state
window.debugAccountSwitching = function() {
    if (window.radegastClient) {
        window.radegastClient.debugConnectionState();
    } else {
        console.error("RadegastClient not available");
    }
};

// Simulate rapid account switching to test for race conditions
window.testRapidAccountSwitching = async function(accountIds, intervalMs = 2000) {
    if (!window.radegastClient) {
        console.error("RadegastClient not available");
        return;
    }
    
    if (!Array.isArray(accountIds) || accountIds.length < 2) {
        console.error("Please provide an array of at least 2 account IDs");
        return;
    }
    
    console.log("Starting rapid account switching test...");
    let switchCount = 0;
    const maxSwitches = 10;
    
    const switchAccount = () => {
        if (switchCount >= maxSwitches) {
            console.log("Rapid switching test completed");
            return;
        }
        
        const accountId = accountIds[switchCount % accountIds.length];
        console.log(`Test switch ${switchCount + 1}: Switching to account ${accountId}`);
        
        window.radegastClient.selectAccount(accountId);
        switchCount++;
        
        setTimeout(switchAccount, intervalMs);
    };
    
    switchAccount();
};

// Monitor radar updates and log them
window.monitorRadarUpdates = function(enable = true) {
    if (!window.radegastClient) {
        console.error("RadegastClient not available");
        return;
    }
    
    if (enable) {
        // Intercept the updateNearbyAvatars method
        const originalUpdate = window.radegastClient.updateNearbyAvatars.bind(window.radegastClient);
        window.radegastClient.updateNearbyAvatars = function(avatars) {
            console.log(`[RADAR DEBUG] updateNearbyAvatars called:`, {
                currentAccount: this.currentAccountId,
                isSwitching: this.isSwitchingAccounts,
                avatarCount: avatars.length,
                firstAvatarAccount: avatars.length > 0 ? avatars[0].accountId : 'N/A',
                timestamp: new Date().toISOString()
            });
            return originalUpdate(avatars);
        };
        console.log("Radar update monitoring enabled");
    } else {
        // Restore original method (this is basic - in production you'd want a better restore mechanism)
        console.log("Radar update monitoring disabled (method needs to be restored manually)");
    }
};