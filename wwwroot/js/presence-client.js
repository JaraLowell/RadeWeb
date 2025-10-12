// Enhanced main.js with presence management functionality

class RadegastWebClient {
    constructor() {
        this.connection = null;
        this.accounts = [];
        this.currentAccount = null;
        this.isConnected = false;
        this.presenceStates = new Map(); // Track presence for each account
        
        // Initialize the client
        this.init();
        
        // Setup browser visibility detection for away/return handling
        this.setupBrowserVisibilityDetection();
    }

    async init() {
        console.log('Initializing RadegastWeb client...');
        
        // Initialize SignalR connection
        await this.initializeSignalR();
        
        // Load accounts
        await this.loadAccounts();
        
        // Setup event handlers
        this.setupEventHandlers();
        
        console.log('RadegastWeb client initialized');
    }

    async initializeSignalR() {
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl("/radegasthub")
            .withAutomaticReconnect()
            .build();

        // Setup SignalR event handlers
        this.connection.on("ReceiveChat", (chatMessage) => {
            this.onChatReceived(chatMessage);
        });

        this.connection.on("AccountStatusChanged", (status) => {
            this.onAccountStatusChanged(status);
        });

        this.connection.on("PresenceStatusChanged", (accountId, status, statusText) => {
            this.onPresenceStatusChanged(accountId, status, statusText);
        });

        this.connection.on("NearbyAvatarsUpdated", (avatars) => {
            this.onNearbyAvatarsUpdated(avatars);
        });

        this.connection.on("RegionInfoUpdated", (regionInfo) => {
            this.onRegionInfoUpdated(regionInfo);
        });

        this.connection.on("PresenceError", (error) => {
            console.error("Presence error:", error);
            this.showError(`Presence error: ${error}`);
        });

        try {
            await this.connection.start();
            this.isConnected = true;
            console.log("SignalR connected");
        } catch (err) {
            console.error("SignalR connection failed:", err);
            this.showError("Failed to connect to real-time services");
        }
    }

    async loadAccounts() {
        try {
            const response = await fetch('/api/accounts');
            if (response.ok) {
                this.accounts = await response.json();
                this.updateAccountsList();
            } else {
                console.error('Failed to load accounts');
            }
        } catch (error) {
            console.error('Error loading accounts:', error);
        }
    }

    setupBrowserVisibilityDetection() {
        // Handle browser visibility changes
        document.addEventListener('visibilitychange', () => {
            if (document.hidden) {
                this.handleBrowserClose();
            } else {
                this.handleBrowserReturn();
            }
        });

        // Handle browser/tab close
        window.addEventListener('beforeunload', () => {
            this.handleBrowserClose();
        });

        // Handle page focus/blur as backup
        window.addEventListener('focus', () => {
            if (!document.hidden) {
                this.handleBrowserReturn();
            }
        });

        window.addEventListener('blur', () => {
            // Small delay to avoid triggering on quick focus changes
            setTimeout(() => {
                if (document.hidden) {
                    this.handleBrowserClose();
                }
            }, 1000);
        });
    }

    async handleBrowserClose() {
        console.log('Browser going away - setting accounts to away mode');
        if (this.connection && this.isConnected) {
            try {
                await this.connection.invoke("HandleBrowserClose");
            } catch (error) {
                console.error('Error handling browser close:', error);
            }
        }
    }

    async handleBrowserReturn() {
        console.log('Browser returned - updating account statuses');
        if (this.connection && this.isConnected) {
            try {
                await this.connection.invoke("HandleBrowserReturn");
            } catch (error) {
                console.error('Error handling browser return:', error);
            }
        }
    }

    async setActiveAccount(accountId) {
        console.log(`Setting active account to: ${accountId}`);
        
        // Update local state
        const previousAccount = this.currentAccount;
        this.currentAccount = accountId;
        
        // Update UI to show active account
        this.updateAccountsList();
        
        // Join the account's SignalR group
        if (this.connection && this.isConnected) {
            try {
                // Leave previous account group
                if (previousAccount) {
                    await this.connection.invoke("LeaveAccountGroup", previousAccount);
                }
                
                // Join new account group
                if (accountId) {
                    await this.connection.invoke("JoinAccountGroup", accountId);
                    await this.connection.invoke("SetActiveAccount", accountId);
                }
            } catch (error) {
                console.error('Error setting active account:', error);
            }
        }
        
        // Update presence states for other accounts
        this.updateAccountPresenceStates();
    }

    async setAwayStatus(accountId, isAway) {
        console.log(`Setting away status for ${accountId}: ${isAway}`);
        
        if (this.connection && this.isConnected) {
            try {
                await this.connection.invoke("SetAwayStatus", accountId, isAway);
            } catch (error) {
                console.error('Error setting away status:', error);
                this.showError(`Failed to set away status: ${error.message}`);
            }
        }
    }

    async setBusyStatus(accountId, isBusy) {
        console.log(`Setting busy status for ${accountId}: ${isBusy}`);
        
        if (this.connection && this.isConnected) {
            try {
                await this.connection.invoke("SetBusyStatus", accountId, isBusy);
            } catch (error) {
                console.error('Error setting busy status:', error);
                this.showError(`Failed to set busy status: ${error.message}`);
            }
        }
    }

    onPresenceStatusChanged(accountId, status, statusText) {
        console.log(`Presence status changed for ${accountId}: ${status} (${statusText})`);
        
        // Update local presence state
        this.presenceStates.set(accountId, { status, statusText });
        
        // Update UI
        this.updateAccountPresenceDisplay(accountId, status, statusText);
        
        // Show notification
        this.showInfo(`Account ${this.getAccountDisplayName(accountId)} is now ${statusText}`);
    }

    updateAccountPresenceDisplay(accountId, status, statusText) {
        const accountElement = document.querySelector(`[data-account-id="${accountId}"]`);
        if (accountElement) {
            const statusElement = accountElement.querySelector('.account-status');
            if (statusElement) {
                statusElement.textContent = statusText;
                statusElement.className = `account-status status-${status.toLowerCase()}`;
            }
        }
    }

    updateAccountPresenceStates() {
        // This would typically update UI elements to show which accounts are active/away/unavailable
        this.accounts.forEach(account => {
            const presence = this.presenceStates.get(account.accountId) || { status: 'Online', statusText: 'Online' };
            this.updateAccountPresenceDisplay(account.accountId, presence.status, presence.statusText);
        });
    }

    getAccountDisplayName(accountId) {
        const account = this.accounts.find(a => a.accountId === accountId);
        return account ? account.displayName : 'Unknown Account';
    }

    setupEventHandlers() {
        // Add event listeners for UI elements
        document.addEventListener('click', (e) => {
            // Handle account selection
            if (e.target.matches('[data-action="select-account"]')) {
                const accountId = e.target.dataset.accountId;
                this.setActiveAccount(accountId);
            }
            
            // Handle away status toggle
            if (e.target.matches('[data-action="toggle-away"]')) {
                const accountId = e.target.dataset.accountId;
                const isAway = e.target.dataset.away === 'true';
                this.setAwayStatus(accountId, !isAway);
                e.target.dataset.away = (!isAway).toString();
            }
            
            // Handle busy status toggle
            if (e.target.matches('[data-action="toggle-busy"]')) {
                const accountId = e.target.dataset.accountId;
                const isBusy = e.target.dataset.busy === 'true';
                this.setBusyStatus(accountId, !isBusy);
                e.target.dataset.busy = (!isBusy).toString();
            }
        });
    }

    updateAccountsList() {
        const accountsContainer = document.getElementById('accounts-list');
        if (!accountsContainer) return;

        accountsContainer.innerHTML = this.accounts.map(account => `
            <div class="account-item" data-account-id="${account.accountId}" ${account.accountId === this.currentAccount ? 'data-active="true"' : ''}>
                <div class="account-info">
                    <h3>${account.displayName}</h3>
                    <div class="account-status status-${account.isConnected ? 'online' : 'offline'}">
                        ${account.status}
                    </div>
                    <div class="account-region">${account.currentRegion || 'Not connected'}</div>
                </div>
                <div class="account-controls">
                    <button data-action="select-account" data-account-id="${account.accountId}">
                        ${account.accountId === this.currentAccount ? 'Active' : 'Switch To'}
                    </button>
                    ${account.isConnected ? `
                        <button data-action="toggle-away" data-account-id="${account.accountId}" data-away="false">
                            Away
                        </button>
                        <button data-action="toggle-busy" data-account-id="${account.accountId}" data-busy="false">
                            Busy
                        </button>
                    ` : ''}
                </div>
            </div>
        `).join('');
    }

    onAccountStatusChanged(status) {
        // Update the account in our local list
        const accountIndex = this.accounts.findIndex(a => a.accountId === status.accountId);
        if (accountIndex !== -1) {
            this.accounts[accountIndex] = status;
            this.updateAccountsList();
        }
    }

    onChatReceived(chatMessage) {
        console.log('Chat received:', chatMessage);
        // Handle chat message display
    }

    onNearbyAvatarsUpdated(avatars) {
        console.log('Nearby avatars updated:', avatars);
        // Handle avatar list updates
    }

    onRegionInfoUpdated(regionInfo) {
        console.log('Region info updated:', regionInfo);
        // Handle region information updates
    }

    showError(message) {
        console.error(message);
        // Show error notification to user
        this.showNotification(message, 'error');
    }

    showInfo(message) {
        console.info(message);
        // Show info notification to user
        this.showNotification(message, 'info');
    }

    showNotification(message, type = 'info') {
        // Simple notification system
        const notification = document.createElement('div');
        notification.className = `notification notification-${type}`;
        notification.textContent = message;
        
        document.body.appendChild(notification);
        
        setTimeout(() => {
            notification.remove();
        }, 5000);
    }
}

// Initialize the client when the page loads
document.addEventListener('DOMContentLoaded', () => {
    window.radegastClient = new RadegastWebClient();
});