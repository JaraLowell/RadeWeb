// RadegastWeb JavaScript Client with Tabbed Interface

class RadegastWebClient {
    constructor() {
        this.connection = null;
        this.accounts = [];
        this.currentAccountId = null;
        this.isConnecting = false;
        this.nearbyAvatars = [];
        this.chatSessions = {};
        this.currentChatSession = 'local';
        this.avatarRefreshInterval = null;
        this.closedGroupSessions = new Set(); // Track closed group sessions during this account session
        
        this.initializeSignalR();
        this.bindEvents();
        this.setupTabs();
        this.initializeDarkMode();
        
        // Load accounts after initialization and set up periodic refresh
        this.loadAccounts().catch(error => {
            console.error("Failed to load accounts on startup:", error);
        });
        
        // Set up periodic account status refresh every 30 seconds
        setInterval(() => {
            // Only refresh if we have accounts and the page is visible
            if (this.accounts.length > 0 && document.visibilityState === 'visible') {
                this.loadAccounts().catch(error => {
                    console.error("Failed to refresh accounts:", error);
                });
            }
        }, 30000);
    }

    async initializeSignalR() {
        try {
            if (typeof signalR === 'undefined') {
                console.error("SignalR library not loaded");
                return;
            }

            // Wait for authentication to be confirmed before connecting
            if (!window.authManager.isAuthenticated) {
                await new Promise(resolve => {
                    const checkAuth = () => {
                        if (window.authManager.isAuthenticated) {
                            resolve();
                        } else {
                            setTimeout(checkAuth, 100);
                        }
                    };
                    checkAuth();
                });
            }

            this.connection = new signalR.HubConnectionBuilder()
                .withUrl("/radegasthub")
                .withAutomaticReconnect()
                .build();

            this.connection.on("ReceiveChat", (chatMessage) => {
                this.displayChatMessage(chatMessage);
            });

            this.connection.on("AccountStatusChanged", (status) => {
                this.updateAccountStatus(status);
            });

            this.connection.on("ChatError", (error) => {
                this.showAlert("Chat Error: " + error, "danger");
            });

            this.connection.on("NearbyAvatarsUpdated", (avatars) => {
                this.updateNearbyAvatars(avatars);
            });

            this.connection.on("RegionInfoUpdated", (regionInfo) => {
                this.updateRegionInfo(regionInfo);
            });

            this.connection.on("IMSessionStarted", (session) => {
                this.createIMTab(session);
            });

            this.connection.on("IMSessionUpdated", (session) => {
                this.updateIMSession(session);
            });

            this.connection.on("GroupSessionUpdated", (session) => {
                this.updateGroupSession(session);
            });

            this.connection.on("ChatHistoryLoaded", (accountId, sessionId, messages) => {
                this.loadChatHistory(accountId, sessionId, messages);
            });

            this.connection.on("RecentSessionsLoaded", (accountId, sessions) => {
                this.loadRecentSessions(accountId, sessions);
            });

            this.connection.on("NoticeReceived", (noticeEvent) => {
                this.handleNoticeReceived(noticeEvent);
            });

            this.connection.on("RecentNoticesLoaded", (accountId, notices) => {
                this.loadRecentNotices(accountId, notices);
            });

            // Sit/Stand event handlers
            this.connection.on("SitStandSuccess", (message) => {
                this.showAlert(message, "success");
                this.refreshSittingStatus();
            });

            this.connection.on("SitStandError", (error) => {
                this.showAlert("Movement Error: " + error, "danger");
            });

            this.connection.on("ObjectInfoReceived", (objectInfo) => {
                this.displayObjectInfo(objectInfo);
            });

            this.connection.on("SittingStatusUpdated", (status) => {
                this.updateSittingStatus(status);
            });

            await this.connection.start();
        } catch (err) {
            console.error("SignalR Connection Error:", err);
            this.showAlert("Failed to connect to real-time service", "warning");
        }
    }

    initializeDarkMode() {
        // Check for saved theme preference or default to light mode
        const savedTheme = localStorage.getItem('theme') || 'light';
        this.setTheme(savedTheme);
        
        // Bind dark mode toggle button
        const darkModeToggle = document.getElementById('darkModeToggle');
        if (darkModeToggle) {
            darkModeToggle.addEventListener('click', () => {
                this.toggleDarkMode();
            });
        }
    }

    setTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        const darkModeIcon = document.getElementById('darkModeIcon');
        
        if (darkModeIcon) {
            if (theme === 'dark') {
                darkModeIcon.className = 'fas fa-sun';
            } else {
                darkModeIcon.className = 'fas fa-moon';
            }
        }
        
        // Save theme preference
        localStorage.setItem('theme', theme);
    }

    toggleDarkMode() {
        const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
        const newTheme = currentTheme === 'dark' ? 'light' : 'dark';
        this.setTheme(newTheme);
    }

    setupTabs() {
        // Set local chat as default active tab
        this.setActiveTab('local-chat');
        
        // Setup tab click handlers for existing tabs
        const localChatTab = document.getElementById('local-chat-tab');
        if (localChatTab) {
            localChatTab.addEventListener('click', (e) => {
                e.preventDefault();
                this.setActiveTab('local-chat');
            });
        }
    }

    setActiveTab(tabId) {
        // Remove active class from all tabs and content
        document.querySelectorAll('.nav-link').forEach(tab => tab.classList.remove('active'));
        document.querySelectorAll('.dropdown-item').forEach(item => item.classList.remove('active'));
        document.querySelectorAll('.tab-pane').forEach(pane => pane.classList.remove('active', 'show'));
        
        // Add active class to selected content
        const targetPane = document.getElementById(tabId);
        if (targetPane) {
            targetPane.classList.add('active', 'show');
            this.currentChatSession = tabId;
            
            // Mark corresponding tab/dropdown item as active
            const tabLink = document.querySelector(`[data-tab="${tabId}"]`);
            if (tabLink) {
                tabLink.classList.add('active');
            }
            
            // Load chat history for this session if it's not local chat
            if (tabId !== 'local-chat' && this.currentAccountId && this.connection) {
                const sessionId = tabId.startsWith('chat-') ? tabId.replace('chat-', '') : tabId;
                this.connection.invoke("GetChatHistory", this.currentAccountId, sessionId, 50, 0)
                    .catch(err => console.error("Failed to load chat history:", err));
            }
            
            // Clear unread count for this session
            if (tabId !== 'local-chat') {
                const sessionId = tabId.replace('chat-', '');
                if (this.chatSessions[sessionId]) {
                    this.chatSessions[sessionId].unreadCount = 0;
                    this.updateTabUnreadCount(sessionId, 0);
                }
            }
            
            // Scroll to bottom of chat after a short delay to ensure content is loaded
            setTimeout(() => {
                this.scrollChatToBottom(tabId);
            }, 100);
        }
        
        // Special handling for local chat
        if (tabId === 'local-chat') {
            const localChatTab = document.getElementById('local-chat-tab');
            if (localChatTab) {
                localChatTab.classList.add('active');
            }
            this.currentChatSession = 'local';
        }
    }

    scrollChatToBottom(tabId, smooth = false) {
        let messagesContainer;
        
        if (tabId === 'local-chat') {
            messagesContainer = document.getElementById('localChatMessages');
        } else if (tabId.startsWith('chat-')) {
            const sessionId = tabId.replace('chat-', '');
            messagesContainer = document.getElementById(`messages-${sessionId}`);
        }
        
        if (messagesContainer) {
            if (smooth) {
                messagesContainer.scrollTo({
                    top: messagesContainer.scrollHeight,
                    behavior: 'smooth'
                });
            } else {
                messagesContainer.scrollTop = messagesContainer.scrollHeight;
            }
            console.log(`Scrolled ${tabId} chat to bottom${smooth ? ' (smooth)' : ''}`);
        }
    }

    createIMTab(session) {
        const sessionId = session.sessionId;
        const existingTab = document.getElementById(`chat-${sessionId}`);
        
        if (existingTab) {
            // Tab already exists, just activate it
            this.setActiveTab(`chat-${sessionId}`);
            return;
        }
        
        // Create tab in the IM dropdown
        const imDropdown = document.getElementById('imTabsList');
        const newTabItem = document.createElement('li');
        newTabItem.innerHTML = `
            <a class="dropdown-item d-flex justify-content-between align-items-center" href="#" data-tab="chat-${sessionId}" onclick="radegastClient.setActiveTab('chat-${sessionId}')">
                <span>
                    ${session.sessionName}
                    <span class="badge bg-danger ms-2" id="badge-${sessionId}" style="display: none;">0</span>
                </span>
                <button class="btn btn-sm btn-outline-secondary ms-2" onclick="event.stopPropagation(); radegastClient.closeTab('${sessionId}', 'IM')" title="Close">
                    <i class="fas fa-times"></i>
                </button>
            </a>
        `;
        imDropdown.appendChild(newTabItem);
        
        // Create content pane
        const contentContainer = document.querySelector('.tab-content');
        const newPane = document.createElement('div');
        newPane.className = 'tab-pane';
        newPane.id = `chat-${sessionId}`;
        newPane.innerHTML = `
            <div class="chat-messages" id="messages-${sessionId}"></div>
            <div class="chat-input-area p-3 border-top">
                <div class="input-group">
                    <input type="text" id="input-${sessionId}" class="form-control" placeholder="Type your message...">
                    <button class="btn btn-primary" type="button" onclick="radegastClient.sendMessage('${sessionId}')">
                        <i class="fas fa-paper-plane"></i>
                    </button>
                </div>
            </div>
        `;
        contentContainer.appendChild(newPane);
        
        // Store session info
        this.chatSessions[sessionId] = session;
        
        // Update count
        this.updateTabCounts();
        
        // Activate the new tab
        this.setActiveTab(`chat-${sessionId}`);
    }

    createGroupTab(session) {
        const sessionId = session.sessionId;
        
        // Check if this group was previously closed during this session
        if (this.closedGroupSessions.has(sessionId)) {
            console.log(`Group ${sessionId} was previously closed, not reopening`);
            return;
        }
        
        const existingTab = document.getElementById(`chat-${sessionId}`);
        
        if (existingTab) {
            // Tab already exists, just activate it
            this.setActiveTab(`chat-${sessionId}`);
            return;
        }
        
        // Create tab in the Group dropdown
        const groupDropdown = document.getElementById('groupTabsList');
        const newTabItem = document.createElement('li');
        newTabItem.innerHTML = `
            <a class="dropdown-item d-flex justify-content-between align-items-center" href="#" data-tab="chat-${sessionId}" onclick="radegastClient.setActiveTab('chat-${sessionId}')">
                <span>
                    ${session.sessionName}
                    <span class="badge bg-success ms-2" id="badge-${sessionId}" style="display: none;">0</span>
                </span>
                <button class="btn btn-sm btn-outline-secondary ms-2" onclick="event.stopPropagation(); radegastClient.closeTab('${sessionId}', 'Group')" title="Close">
                    <i class="fas fa-times"></i>
                </button>
            </a>
        `;
        groupDropdown.appendChild(newTabItem);
        
        // Create content pane
        const contentContainer = document.querySelector('.tab-content');
        const newPane = document.createElement('div');
        newPane.className = 'tab-pane';
        newPane.id = `chat-${sessionId}`;
        newPane.innerHTML = `
            <div class="chat-messages" id="messages-${sessionId}"></div>
            <div class="chat-input-area p-3 border-top">
                <div class="input-group">
                    <input type="text" id="input-${sessionId}" class="form-control" placeholder="Type your message...">
                    <button class="btn btn-primary" type="button" onclick="radegastClient.sendMessage('${sessionId}')">
                        <i class="fas fa-paper-plane"></i>
                    </button>
                </div>
            </div>
        `;
        contentContainer.appendChild(newPane);
        
        // Store session info
        this.chatSessions[sessionId] = session;
        
        // Update count
        this.updateTabCounts();
        
        // Activate the new tab
        this.setActiveTab(`chat-${sessionId}`);
    }

    closeTab(sessionId, chatType) {
        console.log(`Closing ${chatType} tab: ${sessionId}`);
        
        // Remove the tab from the dropdown
        const tabElement = document.querySelector(`[data-tab="chat-${sessionId}"]`);
        if (tabElement && tabElement.parentNode) {
            tabElement.parentNode.remove();
        }
        
        // Remove the content pane
        const contentPane = document.getElementById(`chat-${sessionId}`);
        if (contentPane) {
            contentPane.remove();
        }
        
        // Handle different behavior for IMs vs Groups
        if (chatType === 'Group') {
            // For groups, mark as closed so they don't reopen during this session
            this.closedGroupSessions.add(sessionId);
            console.log(`Group ${sessionId} marked as closed for this session`);
        } else if (chatType === 'IM') {
            // For IMs, just remove from sessions but allow reopening on new messages
            console.log(`IM ${sessionId} closed but can reopen on new messages`);
        }
        
        // Remove from active sessions
        delete this.chatSessions[sessionId];
        
        // Update tab counts
        this.updateTabCounts();
        
        // If we were on this tab, switch to local chat
        if (this.currentChatSession === `chat-${sessionId}`) {
            this.setActiveTab('local-chat');
        }
    }

    async startIM(targetId, targetName) {
        // Check if we already have an IM session with this person
        const existingSessionId = `im-${targetId}`;
        const existingSession = this.chatSessions[existingSessionId];
        
        if (existingSession) {
            this.setActiveTab(`chat-${existingSessionId}`);
            return;
        }

        // Create a new IM session
        const session = {
            sessionId: existingSessionId,
            sessionName: targetName,
            chatType: 'IM',
            targetId: targetId,
            unreadCount: 0,
            lastActivity: new Date(),
            accountId: this.currentAccountId,
            isActive: true
        };

        this.createIMTab(session);
    }

    updateNearbyAvatars(avatars) {
        console.log('Updating nearby avatars:', avatars);
        this.nearbyAvatars = avatars;
        this.renderPeopleList();
    }

    renderPeopleList() {
        const peopleList = document.getElementById('peopleList');
        
        if (this.nearbyAvatars.length === 0) {
            peopleList.innerHTML = '<div class="text-muted p-2">No people nearby</div>';
            return;
        }

        // Sort avatars by distance (closest first)
        const sortedAvatars = [...this.nearbyAvatars].sort((a, b) => a.distance - b.distance);

        peopleList.innerHTML = sortedAvatars.map(avatar => `
            <div class="people-item d-flex justify-content-between align-items-center" data-avatar-id="${avatar.id}">
                <div class="people-info">
                    <div class="people-name">${avatar.displayName || avatar.name}</div>
                    <div class="people-distance">${avatar.distance.toFixed(1)}m</div>
                </div>
                <div class="people-actions">
                    <button class="btn btn-sm btn-outline-primary" onclick="radegastClient.startIM('${avatar.id}', '${avatar.displayName || avatar.name}')">
                        <i class="fas fa-comment"></i>
                    </button>
                </div>
            </div>
        `).join('');
    }

    async refreshNearbyAvatars() {
        if (!this.currentAccountId) return;

        try {
            if (this.connection) {
                await this.connection.invoke("GetNearbyAvatars", this.currentAccountId);
            }
        } catch (error) {
            console.error("Error refreshing nearby avatars:", error);
        }
    }

    startAvatarRefresh() {
        // Clear any existing interval
        this.stopAvatarRefresh();
        
        console.log('Starting avatar refresh timer');
        
        // Start periodic refresh every 10 seconds
        this.avatarRefreshInterval = setInterval(() => {
            console.log('Periodic avatar refresh triggered');
            this.refreshNearbyAvatars();
        }, 10000);
        
        // Also refresh immediately
        this.refreshNearbyAvatars();
    }

    stopAvatarRefresh() {
        if (this.avatarRefreshInterval) {
            console.log('Stopping avatar refresh timer');
            clearInterval(this.avatarRefreshInterval);
            this.avatarRefreshInterval = null;
        }
    }

    bindEvents() {
        // Grid URL selection
        document.getElementById('gridUrl').addEventListener('change', (e) => {
            const customDiv = document.getElementById('customGridDiv');
            if (e.target.value === 'custom') {
                customDiv.classList.remove('d-none');
            } else {
                customDiv.classList.add('d-none');
            }
        });

        // Save account button
        document.getElementById('saveAccountBtn').addEventListener('click', () => {
            this.saveAccount();
        });

        // Chat input enter key
        document.getElementById('localChatInput').addEventListener('keypress', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                this.sendChat();
            }
        });

        // Send chat button
        document.getElementById('sendLocalChatBtn').addEventListener('click', () => {
            this.sendChat();
        });

        // Keyboard shortcuts
        document.addEventListener('keydown', (e) => {
            // Ctrl+W to close current tab (if not local chat)
            if (e.ctrlKey && e.key === 'w' && this.currentChatSession !== 'local') {
                e.preventDefault();
                const sessionId = this.currentChatSession.replace('chat-', '');
                const session = this.chatSessions[sessionId];
                if (session) {
                    this.closeTab(sessionId, session.chatType);
                }
            }
            
            // F5 or Ctrl+R to refresh account status
            if (e.key === 'F5' || (e.ctrlKey && e.key === 'r')) {
                e.preventDefault();
                this.forceRefreshAccountStatus();
                this.showAlert("Refreshing account status...", "info");
            }
        });

        // Login/Logout buttons
        document.getElementById('loginBtn').addEventListener('click', () => {
            this.loginAccount();
        });

        document.getElementById('logoutBtn').addEventListener('click', () => {
            this.logoutAccount();
        });

        // Region Info button
        document.getElementById('regionInfoBtn').addEventListener('click', () => {
            this.showRegionInfo();
        });

        // Movement control event handlers
        document.getElementById('sitBtn').addEventListener('click', () => {
            this.sitOnObject();
        });

        document.getElementById('standBtn').addEventListener('click', () => {
            this.standUp();
        });

        document.getElementById('validateObjectBtn').addEventListener('click', () => {
            this.validateObject();
        });

        // Enter key support for object ID input
        document.getElementById('objectIdInput').addEventListener('keypress', (e) => {
            if (e.key === 'Enter') {
                this.sitOnObject();
            }
        });
    }

    async loadAccounts() {
        try {
            // Add cache-busting parameter to ensure fresh data
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts?_t=${Date.now()}`);
            if (response.ok) {
                this.accounts = await response.json();
                console.log('Loaded accounts:', this.accounts.map(a => ({ 
                    id: a.accountId, 
                    name: a.firstName + ' ' + a.lastName, 
                    connected: a.isConnected,
                    status: a.status
                })));
                
                this.renderAccountsList();
                
                // If we have a currently selected account, update its UI
                if (this.currentAccountId) {
                    const currentAccount = this.accounts.find(a => a.accountId === this.currentAccountId);
                    if (currentAccount) {
                        // Update the chat interface to reflect current status
                        this.updateChatInterfaceForAccount(currentAccount);
                    } else {
                        console.warn(`Current account ${this.currentAccountId} not found in loaded accounts`);
                        // Reset UI if current account no longer exists
                        this.currentAccountId = null;
                        document.getElementById('chatInterface').classList.add('d-none');
                        document.getElementById('peoplePanel').classList.add('d-none');
                        document.getElementById('welcomeMessage').classList.remove('d-none');
                    }
                }
                
                // Auto-select the first connected account if none is currently selected
                if (!this.currentAccountId && this.accounts.length > 0) {
                    const connectedAccount = this.accounts.find(a => a.isConnected);
                    if (connectedAccount) {
                        await this.selectAccount(connectedAccount.accountId);
                    }
                }
            } else {
                console.error("Failed to load accounts, status:", response.status);
                this.showAlert("Failed to load accounts", "danger");
            }
        } catch (error) {
            console.error("Error loading accounts:", error);
            this.showAlert("Error loading accounts", "danger");
        }
    }

    // Force refresh account status from server (bypass any caching)
    async forceRefreshAccountStatus() {
        console.log("Forcing account status refresh...");
        await this.loadAccounts();
    }

    updateChatInterfaceForAccount(account) {
        if (!account || this.currentAccountId !== account.accountId) return;
        
        // Update the account name and status
        document.getElementById('chatAccountName').textContent = 
            account.displayName || `${account.firstName} ${account.lastName}`;
        document.getElementById('chatAccountStatus').textContent = 
            `${account.status}${account.currentRegion ? ' • ' + account.currentRegion : ''}`;
        
        // Update the login/logout buttons
        const loginBtn = document.getElementById('loginBtn');
        const logoutBtn = document.getElementById('logoutBtn');
        const regionInfoBtn = document.getElementById('regionInfoBtn');
        
        if (account.isConnected) {
            loginBtn.classList.add('d-none');
            logoutBtn.classList.remove('d-none');
            regionInfoBtn.classList.remove('d-none');
        } else {
            loginBtn.classList.remove('d-none');
            logoutBtn.classList.add('d-none');
            regionInfoBtn.classList.add('d-none');
        }
        
        // Update movement controls visibility
        this.updateMovementControlsVisibility(account);
        
        console.log(`Updated chat interface for account ${account.accountId}: connected=${account.isConnected}`);
    }

    renderAccountsList() {
        const accountsList = document.getElementById('accountsList');
        
        if (this.accounts.length === 0) {
            accountsList.innerHTML = `
                <div class="text-center p-3 text-muted">
                    <i class="fas fa-user-plus fa-2x mb-2"></i>
                    <p>No accounts added yet</p>
                </div>
            `;
            return;
        }

        console.log('Rendering accounts list:', this.accounts.map(a => ({
            id: a.accountId,
            name: `${a.firstName} ${a.lastName}`,
            connected: a.isConnected,
            status: a.status
        })));

        accountsList.innerHTML = this.accounts.map(account => {
            const statusClass = account.isConnected ? 'online' : 'offline';
            const actionButton = account.isConnected ? 
                `<button class="btn btn-sm btn-outline-danger me-2" onclick="event.stopPropagation(); radegastClient.logoutAccount('${account.accountId}')" title="Logout">
                    <i class="fas fa-sign-out-alt"></i>
                </button>` :
                `<button class="btn btn-sm btn-outline-success me-2" onclick="event.stopPropagation(); radegastClient.loginAccount('${account.accountId}')" title="Login">
                    <i class="fas fa-sign-in-alt"></i>
                </button>`;

            console.log(`Account ${account.accountId}: connected=${account.isConnected}, statusClass=${statusClass}`);
            
            return `
                <div class="list-group-item account-item ${account.accountId === this.currentAccountId ? 'active' : ''}" 
                     onclick="radegastClient.selectAccount('${account.accountId}')">
                    <div class="d-flex align-items-center">
                        <span class="account-status ${statusClass}" title="Status: ${account.isConnected ? 'Online' : 'Offline'}"></span>
                        <div class="account-info flex-grow-1">
                            <div class="account-name">${account.displayName || account.firstName + ' ' + account.lastName}</div>
                            <div class="account-details">
                                ${account.status}${account.currentRegion ? ' • ' + account.currentRegion : ''}
                            </div>
                        </div>
                        <div class="account-actions">
                            ${actionButton}
                            <div class="dropdown">
                                <button class="btn btn-sm btn-outline-secondary dropdown-toggle" type="button" 
                                        data-bs-toggle="dropdown" onclick="event.stopPropagation()">
                                    <i class="fas fa-ellipsis-v"></i>
                                </button>
                                <ul class="dropdown-menu">
                                    <li><a class="dropdown-item" href="#" onclick="event.stopPropagation(); radegastClient.forceRefreshAccountStatus()">
                                        <i class="fas fa-sync me-2"></i>Refresh Status
                                    </a></li>
                                    <li><hr class="dropdown-divider"></li>
                                    <li><a class="dropdown-item" href="#" onclick="event.stopPropagation(); radegastClient.deleteAccount('${account.accountId}')">
                                        <i class="fas fa-trash me-2"></i>Delete
                                    </a></li>
                                </ul>
                            </div>
                        </div>
                    </div>
                </div>
            `;
        }).join('');
    }

    clearAllChatTabs() {
        // Clear all chat sessions data
        this.chatSessions = {};
        
        // Clear closed group sessions tracking
        this.closedGroupSessions.clear();
        
        // Remove all IM tabs from the dropdown
        const imDropdown = document.getElementById('imTabsList');
        if (imDropdown) {
            imDropdown.innerHTML = '';
        }
        
        // Remove all Group tabs from the dropdown
        const groupDropdown = document.getElementById('groupTabsList');
        if (groupDropdown) {
            groupDropdown.innerHTML = '';
        }
        
        // Remove all chat content panes except local chat
        const contentContainer = document.querySelector('.tab-content');
        if (contentContainer) {
            const chatPanes = contentContainer.querySelectorAll('.tab-pane[id^="chat-"]');
            chatPanes.forEach(pane => {
                pane.remove();
            });
        }
        
        // Reset tab counts
        this.updateTabCounts();
        
        console.log('Cleared all chat tabs and sessions');
    }

    async selectAccount(accountId) {
        this.currentAccountId = accountId;
        const account = this.accounts.find(a => a.accountId === accountId);
        
        if (!account) {
            console.error(`Account ${accountId} not found`);
            return;
        }

        console.log(`Selecting account ${accountId}: connected=${account.isConnected}, status=${account.status}`);

        // Clear all existing chat sessions and tabs when switching accounts
        this.clearAllChatTabs();
        
        // Clear nearby avatars list
        this.nearbyAvatars = [];
        this.renderPeopleList();

        // Update UI
        this.renderAccountsList();
        document.getElementById('welcomeMessage').classList.add('d-none');
        document.getElementById('chatInterface').classList.remove('d-none');
        
        // Show people panel when account is selected
        document.getElementById('peoplePanel').classList.remove('d-none');
        // Stop any existing avatar refresh
        this.stopAvatarRefresh();
        
        // Update chat interface
        document.getElementById('chatAccountName').textContent = 
            account.displayName || `${account.firstName} ${account.lastName}`;
        document.getElementById('chatAccountStatus').textContent = 
            `${account.status}${account.currentRegion ? ' • ' + account.currentRegion : ''}`;
        
        // Update buttons based on current connection status
        const loginBtn = document.getElementById('loginBtn');
        const logoutBtn = document.getElementById('logoutBtn');
        const regionInfoBtn = document.getElementById('regionInfoBtn');
        
        if (account.isConnected) {
            loginBtn.classList.add('d-none');
            logoutBtn.classList.remove('d-none');
            regionInfoBtn.classList.remove('d-none');
            // Start avatar refresh for connected accounts
            this.startAvatarRefresh();
        } else {
            loginBtn.classList.remove('d-none');
            logoutBtn.classList.add('d-none');
            regionInfoBtn.classList.add('d-none');
        }

        // Clear chat messages in all remaining tabs (mainly local chat)
        document.querySelectorAll('.chat-messages').forEach(container => {
            container.innerHTML = '';
        });

        // Reset to local chat tab
        this.setActiveTab('local-chat');

        // Join SignalR group for this account
        if (this.connection) {
            try {
                await this.connection.invoke("JoinAccountGroup", accountId);
                // Load recent chat sessions for this account
                await this.connection.invoke("GetRecentSessions", accountId);
                // Load local chat history
                await this.connection.invoke("GetChatHistory", accountId, "local-chat", 50, 0);
                // Refresh nearby avatars for the new account (only if connected)
                if (account.isConnected) {
                    this.refreshNearbyAvatars();
                }
            } catch (error) {
                console.error("Error joining account group:", error);
            }
        }
    }

    async saveAccount() {
        const form = document.getElementById('addAccountForm');
        const formData = new FormData(form);
        
        let gridUrl = document.getElementById('gridUrl').value;
        if (gridUrl === 'custom') {
            gridUrl = document.getElementById('customGridUrl').value;
            if (!gridUrl) {
                this.showAlert("Please enter a custom grid URL", "danger");
                return;
            }
        }

        const account = {
            firstName: document.getElementById('firstName').value,
            lastName: document.getElementById('lastName').value,
            password: document.getElementById('password').value,
            displayName: document.getElementById('displayName').value,
            gridUrl: gridUrl
        };

        if (!account.firstName || !account.lastName || !account.password) {
            this.showAlert("Please fill in all required fields", "danger");
            return;
        }

        try {
            this.showLoading(true);
            const response = await window.authManager.makeAuthenticatedRequest('/api/accounts', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(account)
            });

            if (response.ok) {
                this.showAlert("Account saved successfully", "success");
                form.reset();
                document.getElementById('customGridDiv').classList.add('d-none');
                bootstrap.Modal.getInstance(document.getElementById('addAccountModal')).hide();
                await this.loadAccounts();
            } else {
                const error = await response.text();
                this.showAlert("Failed to save account: " + error, "danger");
            }
        } catch (error) {
            console.error("Error saving account:", error);
            this.showAlert("Error saving account", "danger");
        } finally {
            this.showLoading(false);
        }
    }

    async deleteAccount(accountId) {
        if (!confirm("Are you sure you want to delete this account? This action cannot be undone.")) {
            return;
        }

        try {
            this.showLoading(true);
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${accountId}`, {
                method: 'DELETE'
            });

            if (response.ok) {
                this.showAlert("Account deleted successfully", "success");
                if (this.currentAccountId === accountId) {
                    this.currentAccountId = null;
                    document.getElementById('chatInterface').classList.add('d-none');
                    document.getElementById('peoplePanel').classList.add('d-none');
                    document.getElementById('welcomeMessage').classList.remove('d-none');
                }
                await this.loadAccounts();
            } else {
                this.showAlert("Failed to delete account", "danger");
            }
        } catch (error) {
            console.error("Error deleting account:", error);
            this.showAlert("Error deleting account", "danger");
        } finally {
            this.showLoading(false);
        }
    }

    async loginAccount(accountId = null) {
        const targetAccountId = accountId || this.currentAccountId;
        if (!targetAccountId) return;

        // Check current account status before attempting login
        const account = this.accounts.find(a => a.accountId === targetAccountId);
        if (account && account.isConnected) {
            this.showAlert("Account is already online", "warning");
            // Force refresh to sync UI with actual status
            await this.loadAccounts();
            return;
        }

        try {
            this.showLoading(true);
            
            // Ensure we're joined to this account's SignalR group
            if (this.connection && targetAccountId !== this.currentAccountId) {
                try {
                    await this.connection.invoke("JoinAccountGroup", targetAccountId);
                } catch (error) {
                    console.error("Error joining account group for login:", error);
                }
            }
            
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${targetAccountId}/login`, {
                method: 'POST'
            });

            if (response.ok) {
                this.showAlert("Login initiated", "info");
                
                // Refresh the account list immediately and after a delay
                await this.loadAccounts();
                
                setTimeout(async () => {
                    await this.loadAccounts();
                }, 2000); // Give more time for the status to propagate
                
                // If this is the current account, the UI will be updated via loadAccounts
                console.log("Login request successful for account", targetAccountId);
            } else {
                const errorText = await response.text();
                console.error("Login failed:", errorText);
                this.showAlert(`Login failed: ${errorText}`, "danger");
                
                // Force refresh to sync UI with actual status
                await this.loadAccounts();
            }
        } catch (error) {
            console.error("Error logging in:", error);
            this.showAlert("Error logging in", "danger");
            
            // Force refresh to sync UI with actual status
            await this.loadAccounts();
        } finally {
            this.showLoading(false);
        }
    }

    async logoutAccount(accountId = null) {
        const targetAccountId = accountId || this.currentAccountId;
        if (!targetAccountId) return;

        // Check current account status before attempting logout
        const account = this.accounts.find(a => a.accountId === targetAccountId);
        if (account && !account.isConnected) {
            this.showAlert("Account is already offline", "warning");
            // Force refresh to sync UI with actual status
            await this.loadAccounts();
            return;
        }

        try {
            this.showLoading(true);
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${targetAccountId}/logout`, {
                method: 'POST'
            });

            if (response.ok) {
                this.showAlert("Logout successful", "info");
                
                // Refresh the account list immediately and after a delay
                await this.loadAccounts();
                
                setTimeout(async () => {
                    await this.loadAccounts();
                }, 2000); // Give more time for the status to propagate
                
                // If this is the current account, stop avatar refresh
                if (targetAccountId === this.currentAccountId) {
                    this.stopAvatarRefresh();
                    console.log("Logout successful for current account");
                }
            } else {
                const errorText = await response.text();
                console.error("Logout failed:", errorText);
                this.showAlert(`Logout failed: ${errorText}`, "danger");
                
                // Force refresh to sync UI with actual status
                await this.loadAccounts();
            }
        } catch (error) {
            console.error("Error logging out:", error);
            this.showAlert("Error logging out", "danger");
            
            // Force refresh to sync UI with actual status
            await this.loadAccounts();
        } finally {
            this.showLoading(false);
        }
    }

    showRegionInfo() {
        console.log('showRegionInfo called - currentAccountId:', this.currentAccountId);
        
        if (!this.currentAccountId) {
            this.showAlert("Please select and connect an account first", "warning");
            return;
        }

        // Check if account is connected by looking at the button visibility first
        const regionInfoBtn = document.getElementById('regionInfoBtn');
        const buttonVisible = regionInfoBtn && !regionInfoBtn.classList.contains('d-none');
        console.log('Region info button visible:', buttonVisible);

        // Check the account object
        const account = this.accounts.find(acc => acc.accountId === this.currentAccountId);
        console.log('Account found:', !!account, 'Account connected:', account?.isConnected);
        console.log('Current account ID:', this.currentAccountId);
        console.log('Available accounts:', this.accounts.map(a => ({ accountId: a.accountId, isConnected: a.isConnected })));
        
        if (!buttonVisible) {
            this.showAlert("Account must be connected to view region information 1", "warning");
            return;
        }

        if (!account || !account.isConnected) {
            // Try to refresh the account status before failing
            console.warn("Account status mismatch - button visible but account marked as disconnected, attempting refresh");
            this.loadAccounts().then(() => {
                const refreshedAccount = this.accounts.find(acc => acc.accountId === this.currentAccountId);
                console.log('After refresh - Account found:', !!refreshedAccount, 'Account connected:', refreshedAccount?.isConnected);
                if (refreshedAccount && refreshedAccount.isConnected) {
                    this.showRegionInfoInternal();
                } else {
                    this.showAlert("Account must be connected to view region information 2", "warning");
                }
            });
            return;
        }

        this.showRegionInfoInternal();
    }

    showRegionInfoInternal() {
        // Show the region info panel
        if (window.regionInfoPanel) {
            window.regionInfoPanel.show(this.currentAccountId);
        } else {
            console.error("Region info panel not initialized");
            this.showAlert("Region info panel not available", "danger");
        }
    }

    async sendChat() {
        if (!this.currentAccountId) return;

        const chatInput = document.getElementById('localChatInput');
        const message = chatInput.value.trim();

        if (!message) return;

        try {
            if (this.currentChatSession === 'local') {
                // Send to local chat
                const chatType = document.getElementById('localChatType').value;
                if (this.connection) {
                    await this.connection.invoke("SendChat", {
                        accountId: this.currentAccountId,
                        message: message,
                        chatType: chatType,
                        channel: 0
                    });
                }
            } else {
                // This should use the new sendMessage method for other sessions
                await this.sendMessage(this.currentChatSession.replace('chat-', ''));
                return; // Don't clear the input here as sendMessage will handle it
            }
            
            chatInput.value = '';
        } catch (error) {
            console.error("Error sending chat:", error);
            this.showAlert("Error sending message", "danger");
        }
    }

    async sendMessage(sessionId) {
        if (!this.currentAccountId) return;

        const inputElement = document.getElementById(`input-${sessionId}`);
        if (!inputElement) return;

        const message = inputElement.value.trim();
        if (!message) return;

        try {
            const session = this.chatSessions[sessionId];
            if (!session) return;

            if (session.chatType === 'IM') {
                if (this.connection) {
                    await this.connection.invoke("SendIM", this.currentAccountId, session.targetId, message);
                }
            } else if (session.chatType === 'Group') {
                if (this.connection) {
                    await this.connection.invoke("SendGroupIM", this.currentAccountId, session.targetId, message);
                }
            }
            
            inputElement.value = '';
        } catch (error) {
            console.error("Error sending message:", error);
            this.showAlert("Error sending message", "danger");
        }
    }

    displayChatMessage(chatMessage) {
        if (chatMessage.accountId !== this.currentAccountId) return;

        let messagesContainer;
        
        // Determine which tab this message belongs to
        if (chatMessage.sessionId && chatMessage.sessionId !== 'local-chat') {
            // Check if this is a group message and the group was closed
            if (chatMessage.chatType === 'Group' && this.closedGroupSessions.has(chatMessage.sessionId)) {
                console.log(`Ignoring message for closed group: ${chatMessage.sessionId}`);
                return;
            }
            
            // IM or Group message
            messagesContainer = document.getElementById(`messages-${chatMessage.sessionId}`);
            
            // If the tab doesn't exist, create it
            if (!messagesContainer) {
                const session = {
                    sessionId: chatMessage.sessionId,
                    sessionName: chatMessage.sessionName || chatMessage.senderName,
                    chatType: chatMessage.chatType,
                    targetId: chatMessage.targetId || chatMessage.senderId,
                    unreadCount: 0,
                    lastActivity: new Date(),
                    accountId: this.currentAccountId,
                    isActive: true
                };
                
                if (chatMessage.chatType === 'Group') {
                    this.createGroupTab(session);
                } else {
                    // For IMs, always create the tab (reopening behavior)
                    this.createIMTab(session);
                }
                
                messagesContainer = document.getElementById(`messages-${chatMessage.sessionId}`);
            }
        } else {
            // Local chat message
            messagesContainer = document.getElementById('localChatMessages');
        }
        
        if (!messagesContainer) return;

        const messageDiv = document.createElement('div');
        messageDiv.className = `chat-message ${(chatMessage.chatType || 'normal').toLowerCase()} mb-2`;
        
        const timestamp = new Date(chatMessage.timestamp).toLocaleTimeString();
        
        // Check if this is a /me command (personal thought)
        const isPersonalThought = chatMessage.message.startsWith('/me ');
        const displayMessage = isPersonalThought ? chatMessage.message.substring(4) : chatMessage.message;
        const nameFormat = isPersonalThought ? '' : ':';
        
        messageDiv.innerHTML = `
            <div class="d-flex">
                <span class="text-muted me-2 small">${timestamp}</span>
                <span class="fw-bold me-2">${this.escapeHtml(chatMessage.senderName)}${nameFormat}</span>
                <span>${this.renderMessageContent(displayMessage)}</span>
            </div>
        `;

        messagesContainer.appendChild(messageDiv);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
        
        // Add event listeners for SLURL links in this message
        this.attachSlUrlLinkHandlers(messageDiv);
        
        // Update unread count if not on active tab
        if (chatMessage.sessionId && this.currentChatSession !== `chat-${chatMessage.sessionId}`) {
            const session = this.chatSessions[chatMessage.sessionId];
            if (session) {
                session.unreadCount++;
                this.updateTabUnreadCount(chatMessage.sessionId, session.unreadCount);
            }
        }
    }

    updateTabUnreadCount(sessionId, count) {
        const badge = document.getElementById(`badge-${sessionId}`);
        if (badge) {
            if (count > 0) {
                badge.textContent = count;
                badge.style.display = 'inline';
            } else {
                badge.style.display = 'none';
            }
        }
        this.updateTabCounts();
    }

    updateTabCounts() {
        let totalIMCount = 0;
        let totalGroupCount = 0;
        
        Object.values(this.chatSessions).forEach(session => {
            if (session.chatType === 'IM') {
                totalIMCount += session.unreadCount || 0;
            } else if (session.chatType === 'Group') {
                totalGroupCount += session.unreadCount || 0;
            }
        });
        
        const imCountBadge = document.getElementById('total-im-count');
        const groupCountBadge = document.getElementById('total-group-count');
        
        if (imCountBadge) {
            imCountBadge.textContent = totalIMCount;
        }
        
        if (groupCountBadge) {
            groupCountBadge.textContent = totalGroupCount;
        }
    }

    updateIMSession(session) {
        this.chatSessions[session.sessionId] = session;
        
        // If tab doesn't exist, create it
        const tabExists = document.getElementById(`chat-${session.sessionId}`);
        if (!tabExists) {
            this.createIMTab(session);
        }
        
        // Reset unread count when switching to this tab
        if (this.currentChatSession === `chat-${session.sessionId}`) {
            this.updateTabUnreadCount(session.sessionId, 0);
        }
    }

    updateGroupSession(session) {
        // Check if this group was previously closed during this session
        if (this.closedGroupSessions.has(session.sessionId)) {
            console.log(`Group ${session.sessionId} was previously closed, not updating`);
            return;
        }
        
        this.chatSessions[session.sessionId] = session;
        
        // If tab doesn't exist, create it
        const tabExists = document.getElementById(`chat-${session.sessionId}`);
        if (!tabExists) {
            this.createGroupTab(session);
        }
        
        // Reset unread count when switching to this tab
        if (this.currentChatSession === `chat-${session.sessionId}`) {
            this.updateTabUnreadCount(session.sessionId, 0);
        }
    }

    updateRegionInfo(regionInfo) {
        // Update region info in the UI if needed
        const account = this.accounts.find(a => a.accountId === regionInfo.accountId);
        if (account) {
            account.currentRegion = regionInfo.name;
            if (this.currentAccountId === regionInfo.accountId) {
                document.getElementById('chatAccountStatus').textContent = 
                    `${account.status} • ${regionInfo.name} (${regionInfo.avatarCount} people)`;
            }
        }
    }

    updateAccountStatus(status) {
        const accountIndex = this.accounts.findIndex(a => a.accountId === status.accountId);
        if (accountIndex !== -1) {
            // Update the account data
            this.accounts[accountIndex] = status;
            
            // Re-render the accounts list to reflect the updated status
            this.renderAccountsList();

            // Update chat interface if this is the current account
            if (this.currentAccountId === status.accountId) {
                // Update the account display name in case it changed
                document.getElementById('chatAccountName').textContent = 
                    status.displayName || `${status.firstName} ${status.lastName}`;
                
                // Update the status text
                document.getElementById('chatAccountStatus').textContent = 
                    `${status.status}${status.currentRegion ? ' • ' + status.currentRegion : ''}`;

                // Update the login/logout buttons based on connection status
                const loginBtn = document.getElementById('loginBtn');
                const logoutBtn = document.getElementById('logoutBtn');
                const regionInfoBtn = document.getElementById('regionInfoBtn');
                
                if (status.isConnected) {
                    loginBtn.classList.add('d-none');
                    logoutBtn.classList.remove('d-none');
                    regionInfoBtn.classList.remove('d-none');
                    // Start avatar refresh when account becomes connected
                    this.startAvatarRefresh();
                    
                    // Load recent notices for this account
                    this.loadAccountNotices(status.accountId);
                } else {
                    loginBtn.classList.remove('d-none');
                    logoutBtn.classList.add('d-none');
                    regionInfoBtn.classList.add('d-none');
                    // Stop avatar refresh when account disconnects
                    this.stopAvatarRefresh();
                    
                    // Clear nearby avatars when disconnected
                    this.nearbyAvatars = [];
                    this.renderPeopleList();
                    
                    // Clear closed group sessions when account disconnects
                    // This allows groups to reopen on next login
                    this.closedGroupSessions.clear();
                    console.log('Cleared closed group sessions due to account disconnect');
                }
                
                console.log(`Updated UI for account ${status.accountId}: connected=${status.isConnected}, status=${status.status}`);
            }
        } else {
            console.warn(`Received status update for unknown account: ${status.accountId}`);
        }
    }

    showAlert(message, type) {
        // Remove existing alerts
        const existingAlerts = document.querySelectorAll('.temp-alert');
        existingAlerts.forEach(alert => alert.remove());

        const alertDiv = document.createElement('div');
        alertDiv.className = `alert alert-${type} alert-dismissible fade show temp-alert`;
        alertDiv.innerHTML = `
            ${message}
            <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
        `;

        document.body.insertBefore(alertDiv, document.body.firstChild);

        // Auto-remove after 5 seconds
        setTimeout(() => {
            if (alertDiv.parentNode) {
                alertDiv.remove();
            }
        }, 5000);
    }

    showLoading(show) {
        const spinner = document.getElementById('loadingSpinner');
        if (show) {
            spinner.classList.remove('d-none');
        } else {
            spinner.classList.add('d-none');
        }
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // New method to safely render message content that may contain SLURL links
    renderMessageContent(message) {
        // If the message contains HTML-like content (our SLURL links), render it as HTML
        if (message && message.includes('<a href=') && message.includes('class="slurl-link')) {
            return message; // Already processed and safe HTML from server
        }
        // Otherwise, escape HTML to prevent XSS
        return this.escapeHtml(message);
    }

    loadChatHistory(accountId, sessionId, messages) {
        if (!messages || messages.length === 0) return;
        
        // Only load history for the current account
        if (accountId !== this.currentAccountId) return;
        
        // Find the chat container for this session
        let chatContainer;
        if (sessionId === 'local-chat') {
            chatContainer = document.getElementById('localChatMessages');
        } else {
            chatContainer = document.getElementById(`messages-${sessionId}`);
        }
        
        if (chatContainer) {
            // Clear existing messages to avoid duplicates
            chatContainer.innerHTML = '';
            
            // Display historical messages
            messages.forEach(message => {
                this.appendChatMessage(chatContainer, message);
            });
            
            // Scroll to bottom
            chatContainer.scrollTop = chatContainer.scrollHeight;
        }
    }

    loadRecentSessions(accountId, sessions) {
        if (!sessions || sessions.length === 0) return;
        
        // Only load sessions for the current account
        if (accountId !== this.currentAccountId) return;
        
        // Create tabs for recent sessions that don't already exist
        sessions.forEach(session => {
            const existingTab = document.getElementById(`chat-${session.sessionId}`);
            if (!existingTab) {
                if (session.chatType === 'IM') {
                    this.createIMTab(session);
                } else if (session.chatType === 'Group') {
                    this.createGroupTab(session);
                }
            }
        });
    }

    appendChatMessage(container, message) {
        const messageDiv = document.createElement('div');
        messageDiv.className = 'chat-message mb-2';
        
        const timestamp = new Date(message.timestamp).toLocaleTimeString();
        const senderName = this.escapeHtml(message.senderName);
        
        // Check if this is a /me command (personal thought)
        const isPersonalThought = message.message.startsWith('/me ');
        const displayMessage = isPersonalThought ? message.message.substring(4) : message.message;
        const nameFormat = isPersonalThought ? '' : ':';
        
        messageDiv.innerHTML = `
            <div class="d-flex">
                <span class="text-muted me-2 small">${timestamp}</span>
                <span class="fw-bold me-2">${senderName}${nameFormat}</span>
                <span>${this.renderMessageContent(displayMessage)}</span>
            </div>
        `;
        
        container.appendChild(messageDiv);
        
        // Add event listeners for SLURL links in this message
        this.attachSlUrlLinkHandlers(messageDiv);
    }

    handleNoticeReceived(noticeEvent) {
        // The notice will be displayed as a chat message with special styling
        // The backend already formats it and sends it as a chat message
        console.log("Notice received:", noticeEvent);
        
        // For now, notices are handled by the regular chat display since
        // the backend formats them and sends them as ChatMessageDto with chatType "Notice"
        // We could add additional UI elements here if needed (like notification popups)
        
        if (noticeEvent.notice.type === 'Group' && noticeEvent.notice.hasAttachment) {
            // Show a special notification for group notices with attachments
            this.showNoticeWithAttachment(noticeEvent.notice);
        }
    }

    showNoticeWithAttachment(notice) {
        // Create a modal or alert for notices that require acknowledgment
        const modalHtml = `
            <div class="modal fade" id="noticeModal" tabindex="-1">
                <div class="modal-dialog">
                    <div class="modal-content">
                        <div class="modal-header" style="background-color: #164482; color: white;">
                            <h5 class="modal-title">Group Notice: ${this.escapeHtml(notice.title)}</h5>
                            <button type="button" class="btn-close btn-close-white" data-bs-dismiss="modal"></button>
                        </div>
                        <div class="modal-body">
                            <p><strong>From:</strong> ${this.escapeHtml(notice.fromName)}</p>
                            <p><strong>Group:</strong> ${this.escapeHtml(notice.groupName || 'Unknown Group')}</p>
                            <hr>
                            <p>${this.escapeHtml(notice.message)}</p>
                            ${notice.hasAttachment ? `
                                <hr>
                                <p><strong>📎 Attachment:</strong> ${this.escapeHtml(notice.attachmentName || 'Unknown Item')}</p>
                                <p><em>Type:</em> ${this.escapeHtml(notice.attachmentType || 'Unknown')}</p>
                            ` : ''}
                        </div>
                        <div class="modal-footer">
                            ${notice.requiresAcknowledgment && notice.hasAttachment ? `
                                <button type="button" class="btn btn-primary" onclick="radegastClient.acknowledgeNotice('${notice.id}')">
                                    Accept Attachment
                                </button>
                            ` : ''}
                            <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Close</button>
                        </div>
                    </div>
                </div>
            </div>
        `;
        
        // Remove existing modal if any
        const existingModal = document.getElementById('noticeModal');
        if (existingModal) {
            existingModal.remove();
        }
        
        // Add modal to body
        document.body.insertAdjacentHTML('beforeend', modalHtml);
        
        // Show the modal
        const modal = new bootstrap.Modal(document.getElementById('noticeModal'));
        modal.show();
    }

    async acknowledgeNotice(noticeId) {
        try {
            if (this.connection && this.currentAccountId) {
                await this.connection.invoke("AcknowledgeNotice", this.currentAccountId, noticeId);
                console.log("Notice acknowledged:", noticeId);
                
                // Close the modal
                const modal = bootstrap.Modal.getInstance(document.getElementById('noticeModal'));
                if (modal) {
                    modal.hide();
                }
                
                this.showAlert("Notice acknowledged successfully", "success");
            }
        } catch (error) {
            console.error("Error acknowledging notice:", error);
            this.showAlert("Failed to acknowledge notice", "danger");
        }
    }

    async loadRecentNotices(accountId, notices) {
        // This could be used to populate a notices panel or history
        console.log("Recent notices loaded:", notices);
        // For now, we'll just log them, but this could be used to show
        // a notices history panel in the UI
        
        // Display unread notices as notifications
        const unreadNotices = notices.filter(n => !n.isRead);
        unreadNotices.forEach(notice => {
            const sessionId = notice.type === 'Group' && notice.groupId ? 
                `group-${notice.groupId}` : 'local-chat';
            const timestamp = new Date(notice.timestamp).toLocaleTimeString([], {hour: '2-digit', minute:'2-digit'});
            const displayMessage = `[${timestamp}] ${notice.fromName} ${notice.title}\n${notice.message}`;
            
            this.displayNotice({
                notice: notice,
                sessionId: sessionId,
                displayMessage: displayMessage
            });
        });
    }

    // Load recent notices for an account when it connects
    async loadAccountNotices(accountId) {
        try {
            await this.connection.invoke("GetRecentNotices", accountId, 20);
        } catch (err) {
            console.error("Error loading account notices:", err);
        }
    }

    // Sit/Stand Methods
    async sitOnObject() {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            const objectIdInput = document.getElementById('objectIdInput');
            const objectId = objectIdInput.value.trim();
            
            // Validate UUID format if provided
            if (objectId && !this.isValidUUID(objectId)) {
                this.showAlert("Invalid UUID format", "danger");
                return;
            }

            if (this.connection) {
                await this.connection.invoke("SitOnObject", this.currentAccountId, objectId || null);
            }
        } catch (error) {
            console.error("Error sitting on object:", error);
            this.showAlert("Failed to sit: " + error.message, "danger");
        }
    }

    async standUp() {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            if (this.connection) {
                await this.connection.invoke("StandUp", this.currentAccountId);
            }
        } catch (error) {
            console.error("Error standing up:", error);
            this.showAlert("Failed to stand up: " + error.message, "danger");
        }
    }

    async validateObject() {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            const objectIdInput = document.getElementById('objectIdInput');
            const objectId = objectIdInput.value.trim();
            
            if (!objectId) {
                this.showAlert("Please enter an object UUID", "warning");
                return;
            }

            if (!this.isValidUUID(objectId)) {
                this.showAlert("Invalid UUID format", "danger");
                return;
            }

            if (this.connection) {
                await this.connection.invoke("GetObjectInfo", this.currentAccountId, objectId);
            }
        } catch (error) {
            console.error("Error validating object:", error);
            this.showAlert("Failed to validate object: " + error.message, "danger");
        }
    }

    async refreshSittingStatus() {
        if (!this.currentAccountId) return;

        try {
            if (this.connection) {
                await this.connection.invoke("GetSittingStatus", this.currentAccountId);
            }
        } catch (error) {
            console.error("Error refreshing sitting status:", error);
        }
    }

    updateSittingStatus(status) {
        const statusBadge = document.getElementById('sittingStatus');
        const objectInfo = document.getElementById('sittingObjectInfo');
        
        if (status.isSitting) {
            if (status.sittingOnGround) {
                statusBadge.textContent = 'Sitting (Ground)';
                statusBadge.className = 'badge sitting-status-sitting-ground';
                objectInfo.textContent = 'Ground';
            } else {
                statusBadge.textContent = 'Sitting';
                statusBadge.className = 'badge sitting-status-sitting';
                objectInfo.textContent = status.sittingOnLocalId ? `LocalID: ${status.sittingOnLocalId}` : 'Unknown';
            }
        } else {
            statusBadge.textContent = 'Standing';
            statusBadge.className = 'badge sitting-status-standing';
            objectInfo.textContent = 'None';
        }
    }

    displayObjectInfo(objectInfo) {
        const message = `Object Found: ${objectInfo.name}\nDescription: ${objectInfo.description}\nPosition: (${objectInfo.position.x.toFixed(1)}, ${objectInfo.position.y.toFixed(1)}, ${objectInfo.position.z.toFixed(1)})\nCan Sit: ${objectInfo.canSit ? 'Yes' : 'No'}`;
        this.showAlert(message, objectInfo.canSit ? "success" : "warning");
    }

    isValidUUID(str) {
        const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
        return uuidRegex.test(str);
    }

    // Show/hide movement controls based on account connection status
    updateMovementControlsVisibility(account) {
        const movementControls = document.getElementById('movementControls');
        if (account && account.isConnected) {
            movementControls.style.display = 'block';
            // Refresh sitting status when account becomes active
            this.refreshSittingStatus();
        } else {
            movementControls.style.display = 'none';
        }
    }

    // Handle SLURL link clicks
    attachSlUrlLinkHandlers(container) {
        const slUrlLinks = container.querySelectorAll('a.slurl-link');
        slUrlLinks.forEach(link => {
            link.addEventListener('click', (e) => {
                e.preventDefault();
                this.handleSlUrlClick(link);
            });
        });
    }

    handleSlUrlClick(link) {
        const href = link.getAttribute('href');
        const action = link.getAttribute('data-action');
        const linkText = link.textContent;
        
        console.log('SLURL clicked:', { href, action, linkText });
        
        // Handle different types of SLURL actions
        switch (action) {
            case 'agent':
                this.showAlert(`Agent profile: ${linkText}`, 'info');
                // TODO: Open agent profile
                break;
            case 'group':
                this.showAlert(`Group: ${linkText}`, 'info');
                // TODO: Open group profile
                break;
            case 'teleport':
                this.showAlert(`Teleport to: ${linkText}`, 'info');
                // TODO: Implement teleport
                break;
            case 'map':
                this.showAlert(`Show map: ${linkText}`, 'info');
                // TODO: Open map
                break;
            case 'inventory':
                this.showAlert(`Inventory item: ${linkText}`, 'info');
                // TODO: Open inventory
                break;
            default:
                // For unknown actions or external links, open in new tab
                window.open(href, '_blank');
                break;
        }
    }
}

// Initialize the client when the page loads
let radegastClient;
document.addEventListener('DOMContentLoaded', () => {
    radegastClient = new RadegastWebClient();
});