class RadegastWebClient {
    constructor() {
        this.connection = null;
        this.accounts = [];
        this.currentAccountId = null;
        this.previousAccountId = null; // Track previous account for SignalR group management
        this.isConnecting = false;
        this.nearbyAvatars = [];
        this.chatSessions = {};
        this.currentChatSession = 'local';
        this.avatarRefreshInterval = null;
        this.heartbeatInterval = null;
        this.connectionValidationInterval = null;
        this.lastHeartbeatTime = null;
        this.closedGroupSessions = new Set(); // Track closed group sessions during this account session
        this.groups = []; // Store groups for the current account
        this.notices = []; // Store notices for the current account
        this.unreadNoticesCount = 0; // Track unread notices count
        this.scriptDialogQueue = []; // Queue for script dialogs
        this.scriptPermissionQueue = []; // Queue for script permissions
        this.teleportRequestQueue = []; // Queue for teleport requests
        this.isShowingScriptDialog = false; // Track if a script dialog is currently being shown
        this.isShowingScriptPermission = false; // Track if a script permission is currently being shown
        this.isShowingTeleportRequest = false; // Track if a teleport request is currently being shown
        this.currentDialogId = null; // Track the currently displayed dialog ID
        this.currentPermissionId = null; // Track the currently displayed permission ID
        this.currentTeleportRequestId = null; // Track the currently displayed teleport request ID
        this.isInitialized = false; // Track if the client is fully initialized
        this.presenceStates = new Map(); // Track presence for each account
        this.isSwitchingAccounts = false; // Flag to suppress notifications during account switching
        
        this.initializeSignalR();
        this.bindEvents();
        this.setupTabs();
        this.initializeDarkMode();
        this.initializeGroupsToggleState();
        this.initializeRadarToggleState();
        this.initializeUIState();
        
        // Load accounts after initialization and set up periodic refresh
        this.loadAccounts().catch(error => {
            console.error("Failed to load accounts on startup:", error);
        }).finally(() => {
            // Mark as initialized after accounts are loaded
            this.isInitialized = true;
            console.log('RadegastWebClient fully initialized');
            
            // Notify other components that the client is ready
            if (window.miniMap) {
                console.log('Notifying minimap that client is ready');
                window.miniMap.onClientReady();
            }
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

            // Handle reconnection events to fix the avatar events issue
            this.connection.onreconnected(async (connectionId) => {
                console.log('SignalR reconnected with connection ID:', connectionId);
                
                try {
                    // Perform comprehensive connection recovery
                    console.log('Performing connection recovery...');
                    await this.connection.invoke("RecoverConnection");
                    console.log('✓ Connection recovery completed');
                    
                    // If we have a current account, rejoin its group and refresh data
                    if (this.currentAccountId) {
                        console.log(`Re-establishing connection for account ${this.currentAccountId}...`);
                        
                        // Rejoin the account group
                        await this.connection.invoke("JoinAccountGroup", this.currentAccountId);
                        console.log(`✓ Rejoined account group for ${this.currentAccountId}`);
                        
                        // Refresh all data for the current account
                        await this.refreshAllAccountData();
                        console.log(`✓ Refreshed all data for account ${this.currentAccountId}`);
                        
                        this.showAlert("Connection restored and data refreshed", "success");
                    }
                } catch (error) {
                    console.error('Error during SignalR reconnection recovery:', error);
                    this.showAlert("Connection restored but data refresh failed - try switching accounts", "warning");
                }
            });

            this.connection.onreconnecting((error) => {
                console.log('SignalR reconnecting due to error:', error);
                this.showAlert("Connection lost, reconnecting...", "warning");
            });

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

            this.connection.on("AvatarUpdated", (avatar) => {
                this.updateSingleAvatar(avatar);
            });

            this.connection.on("RegionInfoUpdated", (regionInfo) => {
                this.updateRegionInfo(regionInfo);
            });

            this.connection.on("GroupsUpdated", (accountId, groups) => {
                if (accountId === this.currentAccountId) {
                    this.groups = groups;
                    this.renderGroupsList();
                    console.log('Groups updated via SignalR:', groups.length, 'groups');
                }
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

            this.connection.on("ChatHistoryCleared", (accountId, sessionId) => {
                this.handleChatHistoryCleared(accountId, sessionId);
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

            this.connection.on("UnreadNoticesCountLoaded", (accountId, count) => {
                if (accountId === this.currentAccountId) {
                    this.unreadNoticesCount = count;
                    this.updateTabCounts();
                }
            });

            // Sit/Stand event handlers
            this.connection.on("SitStandSuccess", (message) => {
                this.showAlert(message, "success");
                this.refreshSittingStatus();
                
                // Only refresh auto-sit status if sitting on object AND auto-sit is enabled
                if (message.includes("Sitting on object")) {
                    this.refreshAutoSitStatusIfEnabled();
                }
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

            this.connection.on("PresenceStatusChanged", (accountId, status, statusText) => {
                this.onPresenceStatusChanged(accountId, status, statusText);
            });

            // Script dialog event handlers
            this.connection.on("ScriptDialogReceived", (dialog) => {
                this.handleScriptDialogReceived(dialog);
            });

            this.connection.on("ScriptDialogClosed", (accountId, dialogId) => {
                this.handleScriptDialogClosed(accountId, dialogId);
            });

            this.connection.on("ScriptPermissionReceived", (permission) => {
                this.handleScriptPermissionReceived(permission);
            });

            this.connection.on("ScriptPermissionClosed", (accountId, requestId) => {
                this.handleScriptPermissionClosed(accountId, requestId);
            });

            this.connection.on("ScriptDialogError", (error) => {
                this.showAlert("Script Dialog Error: " + error, "danger");
            });

            // Teleport request event handlers
            this.connection.on("TeleportRequestReceived", (request) => {
                this.handleTeleportRequestReceived(request);
            });

            this.connection.on("TeleportRequestClosed", (accountId, requestId) => {
                this.handleTeleportRequestClosed(accountId, requestId);
            });

            this.connection.on("TeleportRequestError", (error) => {
                this.showAlert("Teleport Request Error: " + error, "danger");
            });

            // Interactive notice event handlers
            this.connection.on("InteractiveFriendshipRequestReceived", (request) => {
                this.handleInteractiveFriendshipRequestReceived(request);
            });

            this.connection.on("InteractiveGroupInvitationReceived", (invitation) => {
                this.handleInteractiveGroupInvitationReceived(invitation);
            });

            this.connection.on("InteractiveNoticeResponded", (noticeId, response) => {
                this.handleInteractiveNoticeResponse(noticeId, response);
            });

            // Add reconnection event handlers
            this.connection.onreconnecting((error) => {
                console.warn("SignalR connection lost, attempting to reconnect...", error);
                this.showAlert("Connection lost, reconnecting...", "warning");
            });

            this.connection.onreconnected((connectionId) => {
                console.log("SignalR reconnected with connection ID:", connectionId);
                this.showAlert("Connection restored", "success");
                
                // Rejoin the current account group after reconnection
                if (this.currentAccountId) {
                    this.ensureAccountGroupMembership(this.currentAccountId)
                        .then(() => {
                            console.log(`Rejoined account group ${this.currentAccountId} after reconnection`);
                            // Also refresh nearby avatars to ensure we get updates
                            if (this.accounts.find(a => a.accountId === this.currentAccountId)?.isConnected) {
                                this.refreshNearbyAvatars();
                            }
                        })
                        .catch(error => {
                            console.error("Failed to rejoin account group after reconnection:", error);
                        });
                }
            });

            this.connection.onclose((error) => {
                console.error("SignalR connection closed:", error);
                this.showAlert("Real-time connection lost", "danger");
            });

            await this.connection.start();
            
            // Perform deep cleanup on connection - especially important for long-running servers
            try {
                console.log("Performing deep connection cleanup on initial connect...");
                await this.connection.invoke("PerformDeepConnectionCleanup");
                console.log("✓ Deep connection cleanup completed");
            } catch (cleanupError) {
                console.warn("Deep connection cleanup failed (may not be supported by server):", cleanupError);
            }

            // If we have a current account, validate and fix its connection state
            if (this.currentAccountId) {
                try {
                    console.log(`Validating connection state for current account ${this.currentAccountId}...`);
                    await this.connection.invoke("ValidateAndFixConnectionState", this.currentAccountId);
                    console.log(`✓ Connection state validated for account ${this.currentAccountId}`);
                } catch (validateError) {
                    console.warn("Connection state validation failed:", validateError);
                }
            }
            
            // Start periodic connection health check and heartbeat
            this.startConnectionHealthCheck();
            this.startHeartbeat();
        } catch (err) {
            console.error("SignalR Connection Error:", err);
            this.showAlert("Failed to connect to real-time service", "warning");
        }
    }

    startConnectionHealthCheck() {
        let healthCheckCount = 0;
        
        // Check connection health every 30 seconds
        setInterval(() => {
            healthCheckCount++;
            
            if (this.connection && this.connection.state === 'Connected' && this.currentAccountId) {
                // Verify we're still in the correct account group by checking if we need to rejoin
                // This is a safety measure to ensure proper group membership
                console.debug(`Connection health check #${healthCheckCount}: state=${this.connection.state}, accountId=${this.currentAccountId}`);
                
                // Perform deep cleanup every 10 minutes (20 checks * 30 seconds = 600 seconds = 10 minutes)
                // This helps prevent connection state drift in long-running sessions
                if (healthCheckCount % 20 === 0) {
                    console.log(`Performing periodic deep cleanup (check #${healthCheckCount})...`);
                    this.connection.invoke("PerformDeepConnectionCleanup")
                        .then(() => {
                            console.log("✓ Periodic deep cleanup completed");
                        })
                        .catch(error => {
                            console.warn("Periodic deep cleanup failed:", error);
                        });
                }
            } else if (this.connection && this.connection.state !== 'Connected') {
                console.warn(`SignalR connection state issue: ${this.connection.state}`);
            }
        }, 30000); // 30 seconds
    }

    async ensureAccountGroupMembership(accountId) {
        if (!this.connection || this.connection.state !== 'Connected') {
            console.warn('Cannot ensure group membership: SignalR not connected');
            return false;
        }

        try {
            console.log(`Ensuring group membership for account ${accountId}`);
            await this.connection.invoke("JoinAccountGroup", accountId);
            console.log(`✓ Ensured group membership for account ${accountId}`);
            return true;
        } catch (error) {
            console.error(`Failed to ensure group membership for account ${accountId}:`, error);
            return false;
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
        
        // Setup tab click handlers for existing tabs with a small delay to ensure DOM is ready
        setTimeout(() => {
            const localChatTab = document.getElementById('local-chat-tab');
            if (localChatTab) {
                localChatTab.addEventListener('click', (e) => {
                    e.preventDefault();
                    this.setActiveTab('local-chat');
                });
            }
            
            // Setup notices tab click handler
            const noticesTab = document.getElementById('notices-tab');
            if (noticesTab) {
                noticesTab.addEventListener('click', (e) => {
                    e.preventDefault();
                    this.setActiveTab('notices');
                    this.markAllNoticesAsRead();
                });
            }
        }, 100);
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
            
            // Load chat history for this session if it's not local chat or notices
            if (tabId !== 'local-chat' && tabId !== 'notices' && this.currentAccountId && this.connection) {
                const sessionId = tabId.startsWith('chat-') ? tabId.replace('chat-', '') : tabId;
                this.connection.invoke("GetChatHistory", this.currentAccountId, sessionId, 50, 0)
                    .catch(err => console.error("Failed to load chat history:", err));
            }
            
            // Load notices if notices tab is selected
            if (tabId === 'notices' && this.currentAccountId && this.connection) {
                this.connection.invoke("GetRecentNotices", this.currentAccountId, 50)
                    .catch(err => console.error("Failed to load notices:", err));
            }
            
            // Clear unread count for this session
            if (tabId !== 'local-chat' && tabId !== 'notices') {
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
        
        // Special handling for notices
        if (tabId === 'notices') {
            const noticesTab = document.getElementById('notices-tab');
            if (noticesTab) {
                noticesTab.classList.add('active');
            }
            this.currentChatSession = 'notices';
        }
        
        // Update monitoring indicator
        this.updateMonitoringIndicator(tabId);
        
        console.log(`Active tab set to: ${tabId}, currentChatSession: ${this.currentChatSession}`);
    }

    updateMonitoringIndicator(tabId) {
        const indicator = document.getElementById('chatMonitoringIndicator');
        if (!indicator) return;
        
        let indicatorText = '';
        let iconClass = 'fas fa-eye';
        
        if (tabId === 'local-chat') {
            indicatorText = 'Monitoring Local Chat';
            iconClass = 'fas fa-comments';
        } else if (tabId === 'notices') {
            indicatorText = 'Monitoring Notices';
            iconClass = 'fas fa-bell';
        } else if (tabId.startsWith('chat-')) {
            const sessionId = tabId.replace('chat-', '');
            const session = this.chatSessions[sessionId];
            if (session) {
                if (session.chatType === 'IM') {
                    indicatorText = `Monitoring IM: ${session.sessionName}`;
                    iconClass = 'fas fa-envelope';
                } else if (session.chatType === 'Group') {
                    indicatorText = `Monitoring Group: ${session.sessionName}`;
                    iconClass = 'fas fa-users';
                } else {
                    indicatorText = `Monitoring ${session.chatType}: ${session.sessionName}`;
                }
            } else {
                indicatorText = 'Monitoring Chat Session';
            }
        } else {
            indicatorText = 'Monitoring Chat';
        }
        
        indicator.innerHTML = `<small class="text-muted"><i class="${iconClass} me-1"></i>${this.escapeHtml(indicatorText)}</small>`;
    }

    scrollChatToBottom(tabId, smooth = false) {
        let messagesContainer;
        
        if (tabId === 'local-chat') {
            messagesContainer = document.getElementById('localChatMessages');
        } else if (tabId === 'notices') {
            messagesContainer = document.getElementById('noticesMessages');
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
            // Tab already exists, don't switch to it automatically
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
                <div class="btn-group btn-group-sm ms-2" role="group">
                    <button class="btn btn-outline-secondary chat-close-btn" data-session-id="${sessionId}" data-chat-type="IM" title="Close">
                        <i class="fas fa-times"></i>
                    </button>
                    <button class="btn btn-outline-danger chat-clear-btn" data-session-id="${sessionId}" data-chat-type="IM" title="Close and Clear History">
                        <i class="fas fa-trash-alt"></i>
                    </button>
                </div>
            </a>
        `;
        
        // Add event listeners for the buttons
        const closeBtn = newTabItem.querySelector('.chat-close-btn');
        const clearBtn = newTabItem.querySelector('.chat-clear-btn');
        
        closeBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.closeTab(sessionId, 'IM');
        });
        
        clearBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.closeAndClearTab(sessionId, 'IM');
        });
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
        
        // Add Enter key listener to the input field
        const inputElement = document.getElementById(`input-${sessionId}`);
        if (inputElement) {
            inputElement.addEventListener('keypress', (e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    this.sendMessage(sessionId);
                }
            });
        }
        
        // Store session info
        this.chatSessions[sessionId] = session;
        
        // Update count
        this.updateTabCounts();
        
        // Don't automatically activate the new tab - let user choose
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
            // Tab already exists, don't switch to it automatically
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
                <div class="btn-group btn-group-sm ms-2" role="group">
                    <button class="btn btn-outline-secondary chat-close-btn" data-session-id="${sessionId}" data-chat-type="Group" title="Close">
                        <i class="fas fa-times"></i>
                    </button>
                    <button class="btn btn-outline-danger chat-clear-btn" data-session-id="${sessionId}" data-chat-type="Group" title="Close and Clear History">
                        <i class="fas fa-trash-alt"></i>
                    </button>
                </div>
            </a>
        `;
        
        // Add event listeners for the buttons
        const closeBtn = newTabItem.querySelector('.chat-close-btn');
        const clearBtn = newTabItem.querySelector('.chat-clear-btn');
        
        closeBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.closeTab(sessionId, 'Group');
        });
        
        clearBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.closeAndClearTab(sessionId, 'Group');
        });
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
        
        // Add Enter key listener to the input field
        const inputElement = document.getElementById(`input-${sessionId}`);
        if (inputElement) {
            inputElement.addEventListener('keypress', (e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    this.sendMessage(sessionId);
                }
            });
        }
        
        // Store session info
        this.chatSessions[sessionId] = session;
        
        // Update count
        this.updateTabCounts();
        
        // Don't automatically activate the new tab - let user choose
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

    async closeAndClearTab(sessionId, chatType) {
        console.log(`Closing and clearing ${chatType} tab: ${sessionId}`);
        
        if (!sessionId) {
            console.error("SessionId is null or undefined");
            this.showAlert("Invalid session ID", "danger");
            return;
        }
        
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }
    }

    async closeAndClearTab(sessionId, chatType) {
        console.log(`Closing and clearing ${chatType} tab: ${sessionId}`);
        
        if (!sessionId) {
            console.error("SessionId is null or undefined");
            this.showAlert("Invalid session ID", "danger");
            return;
        }
        
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        console.log(`SignalR connection state: ${this.connection ? this.connection.state : 'null'}`);
        
        // Check connection and attempt to reconnect if needed
        if (!this.connection) {
            console.error("SignalR connection object is null");
            this.showAlert("Connection error - please refresh the page", "danger");
            return;
        }
        
        if (this.connection.state !== "Connected") {
            console.log(`Connection state is ${this.connection.state}, attempting to reconnect...`);
            try {
                if (this.connection.state === "Disconnected") {
                    await this.connection.start();
                    console.log("Reconnected to SignalR hub");
                } else {
                    // Wait a bit for connection to establish
                    await new Promise(resolve => setTimeout(resolve, 1000));
                    if (this.connection.state !== "Connected") {
                        console.error("Still not connected after waiting");
                        this.showAlert("Not connected to server - please try again", "danger");
                        return;
                    }
                }
            } catch (error) {
                console.error("Failed to reconnect:", error);
                this.showAlert("Failed to reconnect - please refresh the page", "danger");
                return;
            }
        }

        console.log(`Account ID: ${this.currentAccountId}, Session ID: ${sessionId}`);

        try {
            // Clear the chat history from the database
            console.log(`Calling ClearChatHistory with accountId: ${this.currentAccountId}, sessionId: ${sessionId}`);
            await this.connection.invoke('ClearChatHistory', this.currentAccountId, sessionId);
            
            // Clear the messages from the UI immediately
            const messagesContainer = document.getElementById(`messages-${sessionId}`);
            if (messagesContainer) {
                messagesContainer.innerHTML = '';
                console.log(`UI cleared for session: ${sessionId}`);
            }
            
            console.log(`Chat history cleared for session: ${sessionId}`);
            this.showAlert(`Chat history cleared for ${chatType.toLowerCase()}`, "success");
            
            // Then close the tab normally
            this.closeTab(sessionId, chatType);
            
        } catch (error) {
            console.error("Error clearing chat history:", error);
            this.showAlert("Failed to clear chat history", "danger");
        }
    }

    async startIM(targetId, targetName) {
        // Check if we already have an IM session with this person
        const existingSessionId = `im-${targetId}`;
        const existingSession = this.chatSessions[existingSessionId];
        
        if (existingSession) {
            // Session already exists, but don't automatically switch to it
            // Just show a brief message that the tab exists
            this.showAlert(`IM tab with ${targetName} already open`, "info");
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
        console.log('Current account ID:', this.currentAccountId);
        console.log('Is switching accounts:', this.isSwitchingAccounts);
        
        // Ignore updates during account switching to prevent stale data
        if (this.isSwitchingAccounts) {
            console.log(`Ignoring avatar update during account switching`);
            return;
        }
        
        // Add timestamp for debugging
        const timestamp = new Date().toLocaleTimeString();
        console.log(`[${timestamp}] Avatar update received: ${avatars.length} avatars for account ${this.currentAccountId}`);
        
        // Only update if avatars belong to the currently selected account
        if (!this.currentAccountId || avatars.length === 0) {
            this.nearbyAvatars = avatars;
            this.renderPeopleList();
            
            // Notify minimap to redraw (will clear yellow dots if no avatars)
            if (window.miniMap && typeof window.miniMap.safeRedraw === 'function') {
                console.log('Main: Triggering minimap redraw (clearing avatars)');
                window.miniMap.safeRedraw();
            }
            return;
        }
        
        // Log avatar details for debugging
        console.log('Avatar filtering debug:');
        avatars.forEach((avatar, index) => {
            console.log(`  Avatar ${index}:`, {
                name: avatar.name || avatar.Name,
                id: avatar.id || avatar.Id,
                accountId: avatar.accountId || avatar.AccountId,
                distance: avatar.distance || avatar.Distance
            });
            console.log(`    Avatar accountId "${avatar.accountId}" === current "${this.currentAccountId}": ${avatar.accountId === this.currentAccountId}`);
        });
        
        // Filter avatars to only include those from the current account
        // Handle both uppercase (AccountId) and lowercase (accountId) property names
        const currentAccountAvatars = avatars.filter(avatar => {
            const avatarAccountId = avatar.accountId || avatar.AccountId;
            return avatarAccountId === this.currentAccountId;
        });
        
        console.log(`Filtered ${avatars.length} avatars down to ${currentAccountAvatars.length} for current account`);
        
        // Only update if these avatars are for the current account
        if (currentAccountAvatars.length > 0) {
            this.nearbyAvatars = currentAccountAvatars;
            this.renderPeopleList();
            
            // Notify minimap to redraw with updated avatar positions (only if changed)
            if (window.miniMap && typeof window.miniMap.safeRedraw === 'function') {
                console.log('Main: Checking if minimap needs redraw after avatar update');
                window.miniMap.safeRedraw();
            }
        } else if (avatars.length > 0) {
            // Don't update if avatars are for a different account
            const firstAvatarAccountId = avatars[0].accountId || avatars[0].AccountId;
            console.log(`Ignoring avatar update for account ${firstAvatarAccountId}, current account is ${this.currentAccountId}`);
        }
    }

    updateSingleAvatar(avatar) {
        console.log('Updating single avatar:', avatar);
        
        // Ignore updates during account switching to prevent stale data
        if (this.isSwitchingAccounts) {
            console.log(`Ignoring single avatar update during account switching`);
            return;
        }
        
        // Handle both uppercase and lowercase property names
        const avatarAccountId = avatar.accountId || avatar.AccountId;
        const avatarId = avatar.id || avatar.Id;
        
        // Only update if avatar belongs to the currently selected account
        if (!this.currentAccountId || avatarAccountId !== this.currentAccountId) {
            console.log(`Ignoring single avatar update for account ${avatarAccountId}, current account is ${this.currentAccountId}`);
            return;
        }
        
        // Find and update the avatar in our list
        const existingIndex = this.nearbyAvatars.findIndex(a => (a.id || a.Id) === avatarId);
        if (existingIndex >= 0) {
            this.nearbyAvatars[existingIndex] = avatar;
        } else {
            this.nearbyAvatars.push(avatar);
        }
        
        this.renderPeopleList();
        
        // Notify minimap to redraw with updated avatar positions
        if (window.miniMap && typeof window.miniMap.safeRedraw === 'function') {
            console.log('Main: Triggering minimap redraw after single avatar update');
            window.miniMap.safeRedraw();
        }
    }

    renderPeopleList() {
        const peopleList = document.getElementById('peopleList');
        
        if (this.nearbyAvatars.length === 0) {
            peopleList.innerHTML = '<div class="text-muted p-2">No people nearby</div>';
            return;
        }

        // Sort avatars by distance (closest first) - handle both property name cases
        const sortedAvatars = [...this.nearbyAvatars].sort((a, b) => {
            const distanceA = a.distance || a.Distance || 0;
            const distanceB = b.distance || b.Distance || 0;
            return distanceA - distanceB;
        });

        peopleList.innerHTML = sortedAvatars.map(avatar => {
            const avatarId = avatar.id || avatar.Id;
            const avatarName = avatar.displayName || avatar.DisplayName || avatar.name || avatar.Name;
            const avatarDistance = avatar.distance || avatar.Distance || 0;
            
            return `
                <div class="people-item d-flex justify-content-between align-items-center" data-avatar-id="${avatarId}">
                    <div class="people-info">
                        <div class="people-name">${avatarName}</div>
                        <div class="people-distance">${avatarDistance.toFixed(1)}m</div>
                    </div>
                    <div class="people-actions">
                        <button class="btn btn-sm btn-outline-primary" onclick="radegastClient.startIM('${avatarId}', '${avatarName}')">
                            <i class="fas fa-comment"></i>
                        </button>
                    </div>
                </div>
            `;
        }).join('');
        
        // Update radar statistics if the debug panel is visible
        this.updateRadarStats();
    }

    async updateRadarStats() {
        if (!this.currentAccountId) return;
        
        const radarStatsElement = document.getElementById('radarStats');
        if (!radarStatsElement) return; // Only update if stats element exists
        
        try {
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/radar-stats`);
            if (response.ok) {
                const stats = await response.json();
                radarStatsElement.innerHTML = `
                    <div class="small">
                        <div>Detailed Avatars: ${stats.detailedAvatarCount}</div>
                        <div>Coarse Location Avatars: ${stats.coarseLocationAvatarCount}</div>
                        <div>Total Unique Avatars: ${stats.totalUniqueAvatars}</div>
                        <div>Sim Total: ${stats.simAvatarCount}</div>
                        <div>Max Range: ${stats.maxDetectionRange}m</div>
                    </div>
                `;
            }
        } catch (error) {
            console.debug("Error fetching radar stats:", error);
        }
    }

    async loadGroups() {
        if (!this.currentAccountId) return;

        try {
            const response = await window.authManager.makeAuthenticatedRequest(`/api/groups/${this.currentAccountId}`);
            if (response.ok) {
                this.groups = await response.json();
                console.log('Loaded groups for account:', this.currentAccountId, this.groups.length);
                this.renderGroupsList();
            } else {
                console.error("Failed to load groups, status:", response.status);
            }
        } catch (error) {
            console.error("Error loading groups:", error);
        }
    }

    renderGroupsList() {
        const groupsList = document.getElementById('groupsList');
        const groupCount = document.getElementById('groupCount');
        
        if (groupCount) {
            groupCount.textContent = this.groups.length;
        }
        
        if (this.groups.length === 0) {
            groupsList.innerHTML = '<div class="text-muted p-2 small">No groups loaded</div>';
            return;
        }

        // Sort groups alphabetically by name
        const sortedGroups = [...this.groups].sort((a, b) => a.name.localeCompare(b.name));

        groupsList.innerHTML = sortedGroups.map(group => `
            <div class="group-item d-flex justify-content-between align-items-center p-2 border-bottom" data-group-id="${group.id}">
                <div class="group-info flex-grow-1">
                    <div class="group-name small fw-bold text-truncate ${group.isIgnored ? 'text-muted text-decoration-line-through' : ''}">${this.escapeHtml(group.name)}</div>
                    <div class="group-details text-muted" style="font-size: 0.75rem;">
                        ${group.memberTitle || 'Member'}${group.isIgnored ? ' (Ignored)' : ''}
                    </div>
                </div>
                <div class="group-actions">
                    <button class="btn btn-sm ${group.isIgnored ? 'btn-outline-secondary' : 'btn-outline-primary'}" 
                            onclick="radegastClient.openGroupChat('${group.id}', '${this.escapeHtml(group.name)}')" 
                            title="Open Group Chat"
                            ${group.isIgnored ? 'disabled' : ''}>
                        <i class="fas fa-comments"></i>
                    </button>
                    <button class="btn btn-sm ${group.isIgnored ? 'btn-warning' : 'btn-outline-warning'}" 
                            onclick="radegastClient.toggleGroupIgnore('${group.id}', ${!group.isIgnored})" 
                            title="${group.isIgnored ? 'Unignore Group' : 'Ignore Group'}">
                        <i class="fas ${group.isIgnored ? 'fa-bell' : 'fa-bell-slash'}"></i>
                    </button>
                </div>
            </div>
        `).join('');
    }

    async openGroupChat(groupId, groupName) {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            // Check if we already have a group chat session
            const existingSessionId = `group-${groupId}`;
            const existingSession = this.chatSessions[existingSessionId];
            
            if (existingSession) {
                // Session already exists, but don't automatically switch to it
                this.showAlert(`Group chat with ${groupName} already open`, "info");
                return;
            }

            // Create a new group session
            const session = {
                sessionId: existingSessionId,
                sessionName: groupName,
                chatType: 'Group',
                targetId: groupId,
                unreadCount: 0,
                lastActivity: new Date(),
                accountId: this.currentAccountId,
                isActive: true
            };

            this.createGroupTab(session);
            this.showAlert(`Opened group chat: ${groupName}`, "success");
        } catch (error) {
            console.error("Error opening group chat:", error);
            this.showAlert("Failed to open group chat", "danger");
        }
    }

    async toggleGroupIgnore(groupId, setIgnored) {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            const response = await fetch(`/api/groups/${this.currentAccountId}/group/${groupId}/ignore`, {
                method: 'PUT',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ isIgnored: setIgnored })
            });

            if (!response.ok) {
                throw new Error('Failed to update group ignore status');
            }

            const result = await response.json();
            
            // Update the local groups list
            const group = this.groups.find(g => g.id === groupId);
            if (group) {
                group.isIgnored = setIgnored;
                this.renderGroupsList();
            }

            const statusText = setIgnored ? 'ignored' : 'unignored';
            this.showAlert(`Group ${statusText} successfully`, "success");
            
            console.log(`Group ${groupId} ${statusText}`);
        } catch (error) {
            console.error("Error toggling group ignore status:", error);
            this.showAlert("Failed to update group ignore status", "danger");
        }
    }

    async refreshNearbyAvatars() {
        if (!this.currentAccountId) {
            console.log("refreshNearbyAvatars: No current account ID");
            return;
        }

        if (this.isSwitchingAccounts) {
            console.log("refreshNearbyAvatars: Skipping refresh during account switching");
            return;
        }

        try {
            if (this.connection) {
                console.log(`refreshNearbyAvatars: Requesting nearby avatars for account ${this.currentAccountId}`);
                await this.connection.invoke("GetNearbyAvatars", this.currentAccountId);
            } else {
                console.warn("refreshNearbyAvatars: No SignalR connection available");
            }
        } catch (error) {
            console.error(`Error refreshing nearby avatars for account ${this.currentAccountId}:`, error);
        }
    }

    async forceRefreshAvatarEvents(accountId) {
        if (!this.connection || !accountId) {
            console.warn("forceRefreshAvatarEvents: No connection or account ID");
            return;
        }

        try {
            console.log(`forceRefreshAvatarEvents: Forcing avatar event refresh for account ${accountId}`);
            await this.connection.invoke("RefreshAvatarEvents", accountId);
        } catch (error) {
            console.warn(`Failed to force refresh avatar events for account ${accountId}:`, error);
        }
    }

    // Debug method - can be called from browser console
    async debugAccountSwitching(accountId) {
        console.log("=== ACCOUNT SWITCHING DEBUG ===");
        console.log(`Current account ID: ${this.currentAccountId}`);
        console.log(`Target account ID: ${accountId}`);
        console.log(`Is switching accounts: ${this.isSwitchingAccounts}`);
        console.log(`SignalR connection state: ${this.connection?.state}`);
        
        if (this.connection && accountId) {
            try {
                console.log("Step 1: Validating connection state...");
                await this.connection.invoke("ValidateAndFixConnectionState", accountId);
                
                console.log("Step 2: Force refreshing avatar events...");
                await this.connection.invoke("RefreshAvatarEvents", accountId);
                
                console.log("Step 3: Getting nearby avatars...");
                await this.connection.invoke("GetNearbyAvatars", accountId);
                
                console.log("Step 4: Diagnosing and fixing avatar events...");
                await this.connection.invoke("DiagnoseAndFixAvatarEvents", accountId);
                
                console.log("Debug steps completed - check nearby avatars list");
            } catch (error) {
                console.error("Error during debug:", error);
            }
        }
    }

    async refreshAllAccountData() {
        if (!this.currentAccountId || !this.connection) {
            console.log("refreshAllAccountData: No current account ID or SignalR connection");
            return;
        }

        try {
            console.log(`Refreshing all data for account ${this.currentAccountId}...`);
            
            // Refresh nearby avatars
            await this.connection.invoke("GetNearbyAvatars", this.currentAccountId);
            
            // Refresh recent chat sessions
            await this.connection.invoke("GetRecentSessions", this.currentAccountId);
            
            // Refresh local chat history
            await this.connection.invoke("GetChatHistory", this.currentAccountId, "local-chat", 50, 0);
            
            // Refresh presence status
            await this.connection.invoke("GetCurrentPresenceStatus", this.currentAccountId);
            
            // Refresh notices
            await this.loadAccountNotices(this.currentAccountId);
            
            console.log(`✓ Completed data refresh for account ${this.currentAccountId}`);
        } catch (error) {
            console.error(`Error refreshing all account data for ${this.currentAccountId}:`, error);
        }
    }

    // Diagnostic methods for troubleshooting - can be called from browser console
    async diagnoseProblem() {
        if (!this.currentAccountId) {
            console.log("❌ No account selected - please select an account first");
            return;
        }

        console.log("🔍 RadegastWeb Connection Diagnostics");
        console.log("=====================================");
        
        // Check SignalR connection
        if (this.connection) {
            console.log(`✅ SignalR Connection State: ${this.connection.state}`);
            console.log(`✅ SignalR Connection ID: ${this.connection.connectionId || 'Unknown'}`);
        } else {
            console.log("❌ SignalR connection is null");
        }

        // Check current account
        console.log(`✅ Current Account ID: ${this.currentAccountId}`);
        const account = this.accounts.find(a => a.accountId === this.currentAccountId);
        if (account) {
            console.log(`✅ Account Status: ${account.status} (Connected: ${account.isConnected})`);
            console.log(`✅ Account Region: ${account.currentRegion || 'Unknown'}`);
        } else {
            console.log("❌ Current account not found in accounts list");
        }

        // Check nearby avatars
        console.log(`📊 Nearby Avatars Count: ${this.nearbyAvatars.length}`);
        if (this.nearbyAvatars.length > 0) {
            console.log("Sample avatars:", this.nearbyAvatars.slice(0, 3).map(a => a.name));
        }

        // Test SignalR communication
        if (this.connection && this.connection.state === 'Connected') {
            try {
                console.log("🔄 Testing SignalR communication...");
                await this.connection.invoke("Heartbeat");
                console.log("✅ SignalR communication working");

                // Test avatar events fix
                console.log("🔄 Running comprehensive avatar events diagnosis...");
                await this.connection.invoke("DiagnoseAndFixAvatarEvents", this.currentAccountId);
                console.log("✅ Avatar events diagnosis completed");

            } catch (error) {
                console.log("❌ SignalR communication failed:", error);
            }
        }

        console.log("=====================================");
        console.log("If problems persist, try: window.radegastClient.forceRecovery()");
    }

    async forceRecovery() {
        console.log("🚨 Forcing complete connection recovery...");
        
        try {
            if (this.connection && this.connection.state === 'Connected') {
                // Force complete recovery
                await this.connection.invoke("RecoverConnection");
                console.log("✅ Server-side recovery completed");

                // Rejoin current account group
                if (this.currentAccountId) {
                    await this.connection.invoke("JoinAccountGroup", this.currentAccountId);
                    console.log("✅ Rejoined account group");

                    // Refresh all data
                    await this.refreshAllAccountData();
                    console.log("✅ Refreshed all account data");

                    this.showAlert("Complete recovery performed - please check if issues are resolved", "success");
                } else {
                    console.log("⚠️ No account selected for recovery");
                    this.showAlert("Recovery completed but no account selected", "warning");
                }
            } else {
                console.log("❌ SignalR connection not ready for recovery");
                this.showAlert("Cannot perform recovery - connection not ready", "danger");
            }
        } catch (error) {
            console.error("❌ Recovery failed:", error);
            this.showAlert(`Recovery failed: ${error.message}`, "danger");
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

    startHeartbeat() {
        // Stop existing heartbeat if any
        this.stopHeartbeat();
        
        if (!this.connection || this.connection.state !== 'Connected') {
            console.log('Heartbeat not started - no active connection');
            return;
        }
        
        console.log('Starting SignalR heartbeat');
        this.lastHeartbeatTime = Date.now();
        
        // Send heartbeat every 2 minutes (reduced frequency)
        this.heartbeatInterval = setInterval(async () => {
            if (this.connection && this.connection.state === 'Connected') {
                try {
                    await this.connection.invoke("Heartbeat");
                    this.lastHeartbeatTime = Date.now();
                    console.log('Heartbeat sent');
                } catch (error) {
                    console.warn('Heartbeat failed:', error);
                }
            }
        }, 120000);
        
        // Start connection validation timer - checks every 10 minutes (reduced frequency)
        this.connectionValidationInterval = setInterval(() => {
            this.validateConnection();
        }, 600000);
    }
    
    stopHeartbeat() {
        if (this.heartbeatInterval) {
            console.log('Stopping SignalR heartbeat');
            clearInterval(this.heartbeatInterval);
            this.heartbeatInterval = null;
        }
        
        if (this.connectionValidationInterval) {
            clearInterval(this.connectionValidationInterval);
            this.connectionValidationInterval = null;
        }
    }
    
    async validateConnection() {
        if (!this.connection || this.connection.state !== 'Connected') {
            console.log('Connection validation skipped - no active connection');
            return;
        }
        
        if (!this.currentAccountId) {
            console.log('Connection validation skipped - no current account');
            return;
        }
        
        // Check if heartbeat is working (should have received response within last 5 minutes)
        const now = Date.now();
        const timeSinceLastHeartbeat = now - (this.lastHeartbeatTime || 0);
        
        if (timeSinceLastHeartbeat > 300000) { // 5 minutes
            console.warn(`Heartbeat appears stale (${Math.round(timeSinceLastHeartbeat/1000)}s ago), forcing connection validation`);
        }
        
        try {
            console.log('Validating connection state and group membership...');
            await this.connection.invoke("ValidateAndFixConnectionState", this.currentAccountId);
            console.log('✓ Connection validation completed successfully');
            
            // Also force a state synchronization to ensure we have current data
            await this.connection.invoke("ForceStateSynchronization", this.currentAccountId);
            console.log('✓ State synchronization completed');
            
        } catch (error) {
            console.error('Connection validation failed:', error);
            
            // If validation fails completely, try to recover by rejoining the account group
            try {
                console.log('Attempting connection recovery...');
                await this.connection.invoke("JoinAccountGroup", this.currentAccountId);
                console.log('✓ Connection recovery successful');
            } catch (recoveryError) {
                console.error('Connection recovery also failed:', recoveryError);
            }
        }
    }

    bindEvents() {
        // Page unload cleanup
        window.addEventListener('beforeunload', () => {
            this.cleanup();
        });

        // Page visibility change handling (tab switching, minimizing, etc.)
        document.addEventListener('visibilitychange', () => {
            if (this.connection && this.connection.state === 'Connected') {
                if (document.hidden) {
                    // Page is hidden (user switched tabs or minimized)
                    try {
                        this.connection.invoke("HandleBrowserClose").catch(error => {
                            console.warn("Failed to notify server of browser hide:", error);
                        });
                    } catch (error) {
                        console.warn("Error invoking HandleBrowserClose:", error);
                    }
                } else {
                    // Page is visible again
                    try {
                        this.connection.invoke("HandleBrowserReturn").catch(error => {
                            console.warn("Failed to notify server of browser return:", error);
                        });
                    } catch (error) {
                        console.warn("Error invoking HandleBrowserReturn:", error);
                    }
                }
            }
        });

        // Handle page hide event (more reliable than beforeunload in some browsers)
        window.addEventListener('pagehide', () => {
            if (this.connection && this.connection.state === 'Connected') {
                try {
                    // Use sendBeacon for more reliable delivery during page unload
                    if (navigator.sendBeacon && this.currentAccountId) {
                        navigator.sendBeacon('/api/presence/browser-close', JSON.stringify({
                            accountId: this.currentAccountId
                        }));
                    }
                } catch (error) {
                    console.warn("Failed to send beacon on page hide:", error);
                }
            }
        });

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

        // Update account button
        document.getElementById('updateAccountBtn').addEventListener('click', () => {
            this.updateAccount();
        });

        // Edit grid URL change (for completeness, though grid is disabled in edit mode)
        document.getElementById('editGridUrl').addEventListener('change', (e) => {
            const customDiv = document.getElementById('editCustomGridDiv');
            if (e.target.value === 'custom') {
                customDiv.classList.remove('d-none');
            } else {
                customDiv.classList.add('d-none');
            }
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
            
            // Escape key to clean up modal backdrops as a user-accessible fix
            if (e.key === 'Escape') {
                // Small delay to allow normal modal close behavior first
                setTimeout(() => {
                    this.cleanupModalBackdrops();
                }, 100);
            }
            
            // Ctrl+Shift+C to manually clean up modal backdrops (debug shortcut)
            if (e.ctrlKey && e.shiftKey && e.key === 'C') {
                e.preventDefault();
                console.log("Manual modal backdrop cleanup triggered");
                this.cleanupModalBackdrops();
                this.showAlert("Modal backdrops cleaned up", "info");
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

        // Auto-sit controls
        document.getElementById('autoSitEnabled').addEventListener('change', (e) => {
            this.toggleAutoSit(e.target.checked);
        });

        document.getElementById('autoSitDelay').addEventListener('change', (e) => {
            this.updateAutoSitDelay(parseInt(e.target.value));
        });

        document.getElementById('autoSitRestorePresence').addEventListener('change', (e) => {
            this.updateAutoSitPresenceRestore(e.target.checked);
        });

        document.getElementById('autoSitResitBtn').addEventListener('click', () => {
            this.resitNow();
        });

        // Groups toggle button
        document.getElementById('groupsToggleBtn').addEventListener('click', () => {
            this.toggleGroupsList();
        });

        // Radar stats toggle button
        document.getElementById('radarToggleBtn').addEventListener('click', () => {
            this.toggleRadarStats();
        });

        // Manual presence control buttons
        document.getElementById('setAwayBtn').addEventListener('click', () => {
            this.toggleAwayStatus();
        });

        document.getElementById('setBusyBtn').addEventListener('click', () => {
            this.toggleBusyStatus();
        });

        document.getElementById('setOnlineBtn').addEventListener('click', () => {
            this.setOnlineStatus();
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
                        
                        // Sync presence status for the current account if connected
                        if (currentAccount.isConnected && this.connection && this.connection.state === "Connected") {
                            try {
                                console.log('Syncing presence status for current account after load');
                                await this.connection.invoke("GetCurrentPresenceStatus", this.currentAccountId);
                            } catch (error) {
                                console.error(`Error syncing presence for current account ${this.currentAccountId}:`, error);
                            }
                        }
                    } else {
                        console.warn(`Current account ${this.currentAccountId} not found in loaded accounts`);
                        // Reset UI if current account no longer exists
                        this.currentAccountId = null;
                        document.getElementById('chatInterface').classList.add('d-none');
                        document.getElementById('peoplePanel').classList.add('d-none');
                        document.getElementById('welcomeMessage').classList.remove('d-none');
                    }
                }
                
                // Only sync presence for other connected accounts if this is not during initialization
                if (this.isInitialized) {
                    console.log('Syncing presence for all connected accounts (selective)');
                    for (const account of this.accounts) {
                        if (account.isConnected && account.accountId !== this.currentAccountId && 
                            this.connection && this.connection.state === "Connected") {
                            try {
                                // Add a small delay to prevent overwhelming the server
                                setTimeout(async () => {
                                    try {
                                        await this.connection.invoke("GetCurrentPresenceStatus", account.accountId);
                                    } catch (error) {
                                        console.debug(`Error syncing presence for account ${account.accountId}:`, error);
                                    }
                                }, 100 * this.accounts.indexOf(account)); // Stagger the requests
                            } catch (error) {
                                console.debug(`Error setting up presence sync for account ${account.accountId}:`, error);
                            }
                        }
                    }
                }
                
                // Don't auto-select accounts - let user choose
                // Keep welcome message visible until user explicitly selects an account
                if (!this.currentAccountId) {
                    // Ensure welcome message is shown when no account is selected
                    document.getElementById('welcomeMessage').classList.remove('d-none');
                    document.getElementById('chatInterface').classList.add('d-none');
                    document.getElementById('peoplePanel').classList.add('d-none');
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
        const accountNameElement = document.getElementById('chatAccountName');
        const serviceIcons = `${(account.hasAiBotActive || account.HasAiBotActive) ? '<i class="fas fa-user-circle service-icon" title="AI Bot Active" style="color: #007bff;"></i>&nbsp;' : ''}${(account.hasCorradeActive || account.HasCorradeActive) ? '<i class="fas fa-server service-icon" title="Corrade Active" style="color: #28a745;"></i>&nbsp;' : ''}`;
        accountNameElement.innerHTML = serviceIcons + (account.displayName || `${account.firstName} ${account.lastName}`);
        document.getElementById('chatAccountStatus').textContent = 
            `${account.status}${account.currentRegion ? ' • ' + account.currentRegion : ''}`;
        
        // Initialize presence status display for this account
        this.initializePresenceStatusForAccount(account);
        
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
                            <div class="account-name">
                                ${(account.hasAiBotActive || account.HasAiBotActive) ? '<i class="fas fa-user-circle service-icon" title="AI Bot Active" style="color: #007bff;"></i>&nbsp;' : ''}${(account.hasCorradeActive || account.HasCorradeActive) ? '<i class="fas fa-server service-icon" title="Corrade Active" style="color: #28a745;"></i>&nbsp;' : ''}${account.displayName || account.firstName + ' ' + account.lastName}
                            </div>
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
                                    <li><a class="dropdown-item" href="#" onclick="event.stopPropagation(); radegastClient.editAccount('${account.accountId}')">
                                        <i class="fas fa-edit me-2"></i>Details
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
        
        // Clear notices
        this.notices = [];
        this.unreadNoticesCount = 0;
        
        // Clear script dialog and permission queues
        this.scriptDialogQueue = [];
        this.scriptPermissionQueue = [];
        this.teleportRequestQueue = [];
        this.isShowingScriptDialog = false;
        this.isShowingScriptPermission = false;
        this.isShowingTeleportRequest = false;
        this.currentDialogId = null;
        this.currentPermissionId = null;
        
        // Hide any open modals
        const scriptDialogModal = document.getElementById('scriptDialogModal');
        if (scriptDialogModal) {
            const modalInstance = bootstrap.Modal.getInstance(scriptDialogModal);
            if (modalInstance) {
                modalInstance.hide();
            }
        }
        
        const scriptPermissionModal = document.getElementById('scriptPermissionModal');
        if (scriptPermissionModal) {
            const modalInstance = bootstrap.Modal.getInstance(scriptPermissionModal);
            if (modalInstance) {
                modalInstance.hide();
            }
        }
        
        // Clean up any modal backdrops
        this.cleanupModalBackdrops();
        
        // Clear notices content
        const noticesContainer = document.getElementById('noticesMessages');
        if (noticesContainer) {
            noticesContainer.innerHTML = '';
        }
        
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
        
        // Remove all chat content panes except local chat and notices
        const contentContainer = document.querySelector('.tab-content');
        if (contentContainer) {
            const chatPanes = contentContainer.querySelectorAll('.tab-pane[id^="chat-"]');
            chatPanes.forEach(pane => {
                pane.remove();
            });
        }
        
        // Reset tab counts
        this.updateTabCounts();
        
        // Ensure all tabs except local chat are deactivated
        document.querySelectorAll('.nav-link').forEach(tab => {
            tab.classList.remove('active');
        });
        
        // Ensure all tab panes are deactivated
        document.querySelectorAll('.tab-pane').forEach(pane => {
            pane.classList.remove('active', 'show');
        });
        
        // Explicitly activate local chat
        const localChatTab = document.getElementById('local-chat-tab');
        const localChatPane = document.getElementById('local-chat');
        
        if (localChatTab && localChatPane) {
            localChatTab.classList.add('active');
            localChatPane.classList.add('active', 'show');
        }
        
        console.log('Cleared all chat tabs and sessions, set local chat as default');
    }

    async selectAccount(accountId) {
        // Set flag to suppress presence notifications during switching
        this.isSwitchingAccounts = true;
        
        // Store previous account for cleanup
        this.previousAccountId = this.currentAccountId;
        this.currentAccountId = accountId;
        const account = this.accounts.find(a => a.accountId === accountId);
        
        if (!account) {
            console.error(`Account ${accountId} not found`);
            this.isSwitchingAccounts = false; // Reset flag on error
            return;
        }

        console.log(`Selecting account ${accountId}: connected=${account.isConnected}, status=${account.status}`);

        // Clear all existing chat sessions and tabs when switching accounts
        this.clearAllChatTabs();
        
        // Clear nearby avatars list
        this.nearbyAvatars = [];
        this.renderPeopleList();

        // Clear groups for the previous account
        this.groups = [];
        this.renderGroupsList();

        // Update UI
        this.renderAccountsList();
        document.getElementById('welcomeMessage').classList.add('d-none');
        document.getElementById('chatInterface').classList.remove('d-none');
        
        // Show people panel when account is selected
        document.getElementById('peoplePanel').classList.remove('d-none');
        // Stop any existing avatar refresh to prevent data from previous account
        this.stopAvatarRefresh();
        
        // Update chat interface
        const accountNameElement = document.getElementById('chatAccountName');
        const serviceIcons = `${account.hasAiBotActive || account.HasAiBotActive ? '<i class="fas fa-user-circle service-icon" title="AI Bot Active" style="color: #007bff;"></i>&nbsp;' : ''}${account.hasCorradeActive || account.HasCorradeActive ? '<i class="fas fa-server service-icon" title="Corrade Active" style="color: #28a745;"></i>&nbsp;' : ''}`;
        accountNameElement.innerHTML = serviceIcons + (account.displayName || `${account.firstName} ${account.lastName}`);
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
            
            // Show minimap for connected accounts
            if (window.miniMap) {
                window.miniMap.show(accountId);
            }
        } else {
            loginBtn.classList.remove('d-none');
            logoutBtn.classList.add('d-none');
            regionInfoBtn.classList.add('d-none');
            
            // Hide minimap for disconnected accounts
            if (window.miniMap) {
                window.miniMap.hide();
            }
        }

        // Clear chat messages in all remaining tabs (mainly local chat)
        document.querySelectorAll('.chat-messages').forEach(container => {
            container.innerHTML = '';
        });

        // Reset to local chat tab - clearAllChatTabs already sets this as default
        this.currentChatSession = 'local';
        console.log('Account switched - local chat set as default');

        // Set this account as active on the server
        try {
            console.log(`Setting account ${accountId} as active on server`);
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${accountId}/presence/active`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                }
            });

            if (response.ok) {
                const result = await response.json();
                console.log(`Successfully set account ${accountId} as active on server:`, result);
            } else {
                console.error(`Failed to set account as active on server, status: ${response.status}`);
                const errorText = await response.text();
                console.error(`Error details: ${errorText}`);
                
                // Still show warning but don't prevent UI switch
                if (response.status === 400) {
                    console.warn("Account may not be connected, but UI switched anyway");
                } else {
                    this.showAlert("Warning: Server-side account switch may have failed", "warning");
                }
            }
        } catch (error) {
            console.error("Error setting active account on server:", error);
            this.showAlert("Warning: Could not notify server of account switch", "warning");
        }

        // Manage SignalR account groups
        if (this.connection) {
            console.log(`SignalR connection state: ${this.connection.state}`);
            console.log(`Previous account ID: ${this.previousAccountId}, New account ID: ${accountId}`);
            
            // Check if connection is in Connected state
            if (this.connection.state !== 'Connected') {
                console.warn(`SignalR connection not ready (state: ${this.connection.state}), will retry group management`);
                
                // Try to ensure group membership with a delay
                setTimeout(() => {
                    if (this.connection && this.connection.state === 'Connected') {
                        console.log('Retrying group management after connection state change');
                        this.ensureAccountGroupMembership(accountId);
                    }
                }, 2000);
                
                this.showAlert("Real-time connection not ready, retrying...", "warning");
            } else {
                try {
                    // Use the new atomic switch method if available, otherwise fall back to separate calls
                    if (this.previousAccountId && this.previousAccountId !== accountId) {
                        console.log(`Attempting to switch account groups from ${this.previousAccountId} to ${accountId}`);
                        try {
                            await this.connection.invoke("SwitchAccountGroup", this.previousAccountId, accountId);
                            console.log(`✓ Switched account groups from ${this.previousAccountId} to ${accountId}`);
                        } catch (switchError) {
                            console.warn("SwitchAccountGroup not available or failed, using separate calls:", switchError);
                            
                            // First try complete cleanup using LeaveAllAccountGroups
                            try {
                                console.log(`Attempting complete cleanup of all account groups for connection`);
                                await this.connection.invoke("LeaveAllAccountGroups");
                                console.log(`✓ Complete cleanup successful`);
                            } catch (cleanupError) {
                                console.warn("LeaveAllAccountGroups failed, falling back to individual leave:", cleanupError);
                                
                                // Fallback to individual leave
                                try {
                                    console.log(`Attempting to leave account group for ${this.previousAccountId}`);
                                    await this.connection.invoke("LeaveAccountGroup", this.previousAccountId);
                                    console.log(`✓ Left account group for ${this.previousAccountId}`);
                                } catch (leaveError) {
                                    console.error("Failed to leave previous account group:", leaveError);
                                    // Continue anyway, the server will clean up on reconnection
                                }
                            }
                            
                            try {
                                console.log(`Attempting to join account group for ${accountId}`);
                                await this.connection.invoke("JoinAccountGroup", accountId);
                                console.log(`✓ Joined account group for ${accountId}`);
                                
                                // Validate connection state after fallback join
                                try {
                                    await this.connection.invoke("ValidateAndFixConnectionState", accountId);
                                    console.log(`✓ Connection state validated after fallback join for account ${accountId}`);
                                } catch (validateError) {
                                    console.warn("Connection state validation failed after fallback join:", validateError);
                                }
                            } catch (joinError) {
                                console.error("Failed to join new account group:", joinError);
                                throw joinError; // This is critical, so re-throw
                            }
                        }
                    } else {
                        // First time joining or same account
                        console.log(`Attempting to join account group for ${accountId} (first time or same account)`);
                        await this.connection.invoke("JoinAccountGroup", accountId);
                        console.log(`✓ Joined account group for ${accountId}`);
                    }
                    
                    // Validate and fix connection state to handle browser refresh scenarios
                    try {
                        console.log(`Validating connection state for account ${accountId}...`);
                        await this.connection.invoke("ValidateAndFixConnectionState", accountId);
                        console.log(`✓ Connection state validated for account ${accountId}`);
                    } catch (validateError) {
                        console.warn("Connection state validation failed:", validateError);
                    }
                    
                    // Also call SetActiveAccount via SignalR for additional server-side notification
                    try {
                        await this.connection.invoke("SetActiveAccount", accountId);
                        console.log(`✓ Successfully notified server via SignalR of active account: ${accountId}`);
                    } catch (signalRError) {
                        console.warn("SignalR SetActiveAccount failed (this is okay, REST API call should have worked):", signalRError);
                    }

                    // Wait for SignalR group membership changes to propagate and validate them
                    console.log("Waiting for SignalR group membership changes to complete...");
                    await new Promise(resolve => setTimeout(resolve, 500));
                    
                    // Simple validation - just ensure we're properly joined (reduced complexity)
                    try {
                        console.log("Performing simple validation after account switch...");
                        await this.connection.invoke("ValidateAndFixConnectionState", accountId);
                        console.log("✓ Account switch validation completed");
                    } catch (validationError) {
                        console.warn("Account switch validation failed (but continuing):", validationError);
                    }
                
                    // Load recent chat sessions for this account
                    await this.connection.invoke("GetRecentSessions", accountId);
                    // Load local chat history
                    await this.connection.invoke("GetChatHistory", accountId, "local-chat", 50, 0);
                    // Load recent notices for this account
                    await this.loadAccountNotices(accountId);
                    
                    // Load auto-sit configuration for this account
                    await this.loadAutoSitConfig();
                    
                    // Sync presence status for the newly active account
                    if (account.isConnected) {
                        try {
                            await this.connection.invoke("GetCurrentPresenceStatus", accountId);
                            console.log(`Synced presence status for newly active account: ${accountId}`);
                        } catch (presenceError) {
                            console.warn("Failed to sync presence status for new account:", presenceError);
                        }
                    }
                    
                    // Refresh nearby avatars for the new account (only if connected)
                    // Wait a bit more to ensure all SignalR group changes are complete
                    if (account.isConnected) {
                        console.log("Starting coordinated data refresh for new account...");
                        
                        // Use async/await to ensure proper sequencing
                        const refreshAccountData = async () => {
                            // Double-check we're still on the same account before each step
                            if (this.currentAccountId !== accountId || this.isSwitchingAccounts) {
                                console.log("Account changed during refresh, aborting");
                                return;
                            }
                            
                            try {
                                // Simplified refresh approach to reduce interference
                                console.log("Step 1: Requesting current avatar data...");
                                await this.refreshNearbyAvatars();
                                
                                console.log("Step 2: Loading groups and other account data...");
                                this.loadGroups();
                                
                                console.log("Step 3: Starting periodic refresh timer...");
                                this.startAvatarRefresh();
                                
                                console.log("✓ Simplified data refresh completed successfully");
                                
                            } catch (refreshError) {
                                console.error("Error during data refresh:", refreshError);
                                // Minimal fallback
                                this.refreshNearbyAvatars();
                                this.startAvatarRefresh();
                            }
                        };
                        
                        // Start the refresh process after a short delay to ensure group membership is stable
                        setTimeout(refreshAccountData, 800);
                    }
                } catch (error) {
                    console.error("Error managing account groups:", error);
                }
            }
        } else {
            console.warn("SignalR connection not available, skipping group management");
        }

        // Initialize presence status display with current account status
        this.initializePresenceStatusForAccount(account);
        
        // Reset the switching flag after a short delay to allow all status updates to complete
        setTimeout(() => {
            this.isSwitchingAccounts = false;
            console.log(`✓ Account switch completed: ${accountId} - All operations finished and switching flag cleared`);
        }, 1500); // Give 1.5 seconds for all presence updates and data refreshes to settle
    }

    // Initialize the presence status display based on current account status
    initializePresenceStatusForAccount(account) {
        // Use the actual account status from the loaded data
        let status = 'Online';
        let statusText = 'Online';
        
        if (account.status) {
            // Map the account status to presence status
            if (account.status === 'Away') {
                status = 'Away';
                statusText = 'Away';
            } else if (account.status === 'Busy') {
                status = 'Busy';
                statusText = 'Busy';
            } else if (account.status === 'Online') {
                status = 'Online';
                statusText = 'Online';
            } else if (account.status === 'Offline') {
                status = 'Offline';
                statusText = 'Offline';
            } else {
                // For any other status, use it as-is
                status = account.status;
                statusText = account.status;
            }
        }
        
        console.log(`Initializing presence status for account ${account.accountId}: ${status} (${statusText})`);
        this.updatePresenceStatusDisplay(account.accountId, status, statusText);
        
        // Also request the current status from the server to ensure we have the most up-to-date info
        if (this.connection && account.isConnected) {
            try {
                console.log(`Requesting current presence status for account ${account.accountId}`);
                this.connection.invoke("GetCurrentPresenceStatus", account.accountId);
            } catch (error) {
                console.error(`Error requesting current presence status for account ${account.accountId}:`, error);
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
            avatarRelayUuid: document.getElementById('relayUuid').value,
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

    async editAccount(accountId) {
        try {
            this.showLoading(true);
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${accountId}`);
            
            if (response.ok) {
                const account = await response.json();
                
                // Populate the edit form
                document.getElementById('editAccountId').value = account.id;
                document.getElementById('editFirstName').value = account.firstName;
                document.getElementById('editLastName').value = account.lastName;
                document.getElementById('editPassword').value = account.password;
                document.getElementById('editDisplayName').value = account.displayName || '';
                document.getElementById('editRelayUuid').value = account.avatarRelayUuid || '';
                
                // Handle grid URL selection
                const editGridUrl = document.getElementById('editGridUrl');
                const editCustomGridDiv = document.getElementById('editCustomGridDiv');
                const editCustomGridUrl = document.getElementById('editCustomGridUrl');
                
                if (account.gridUrl === 'https://login.agni.lindenlab.com/cgi-bin/login.cgi' || 
                    account.gridUrl === 'https://login.aditi.lindenlab.com/cgi-bin/login.cgi') {
                    editGridUrl.value = account.gridUrl;
                    editCustomGridDiv.classList.add('d-none');
                } else {
                    editGridUrl.value = 'custom';
                    editCustomGridUrl.value = account.gridUrl;
                    editCustomGridDiv.classList.remove('d-none');
                }
                
                // Show the modal
                const modal = new bootstrap.Modal(document.getElementById('editAccountModal'));
                modal.show();
            } else {
                this.showAlert("Failed to load account details", "danger");
            }
        } catch (error) {
            console.error("Error loading account details:", error);
            this.showAlert("Error loading account details", "danger");
        } finally {
            this.showLoading(false);
        }
    }

    async updateAccount() {
        const accountId = document.getElementById('editAccountId').value;
        
        const account = {
            id: accountId,
            firstName: document.getElementById('editFirstName').value,
            lastName: document.getElementById('editLastName').value,
            password: document.getElementById('editPassword').value,
            displayName: document.getElementById('editDisplayName').value,
            avatarRelayUuid: document.getElementById('editRelayUuid').value,
            gridUrl: document.getElementById('editGridUrl').value === 'custom' 
                ? document.getElementById('editCustomGridUrl').value 
                : document.getElementById('editGridUrl').value
        };

        if (!account.password) {
            this.showAlert("Password is required", "danger");
            return;
        }

        try {
            this.showLoading(true);
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${accountId}`, {
                method: 'PUT',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(account)
            });

            if (response.ok) {
                this.showAlert("Account updated successfully", "success");
                bootstrap.Modal.getInstance(document.getElementById('editAccountModal')).hide();
                await this.loadAccounts();
            } else {
                const error = await response.text();
                this.showAlert("Failed to update account: " + error, "danger");
            }
        } catch (error) {
            console.error("Error updating account:", error);
            this.showAlert("Error updating account", "danger");
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

        // Filter out notices - they should only appear in the notices tab
        if (chatMessage.chatType && chatMessage.chatType.toLowerCase() === 'notice') {
            console.log("Filtering out notice from regular chat display:", chatMessage);
            return;
        }

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
        
        // Use enhanced timestamp formatting with relative date information
        const timestamp = this.formatChatTimestamp(chatMessage.timestamp);
        
        // Check if this is a /me command (personal thought)
        const isPersonalThought = chatMessage.message.startsWith('/me ');
        const displayMessage = isPersonalThought ? chatMessage.message.substring(4) : chatMessage.message;
        const nameFormat = isPersonalThought ? '' : ':';
        
        messageDiv.innerHTML = `
            <div class="chat-message-layout d-flex">
                <div class="chat-message-time">
                    <span class="text-muted small" title="Second Life Time (SLT)">${timestamp}</span>
                </div>
                <div class="chat-message-right">
                    <div class="chat-message-header">
                        <span class="fw-bold">${this.escapeHtml(chatMessage.senderName)}${nameFormat}</span>
                    </div>
                    <div class="chat-message-content">
                        <span>${this.renderMessageContent(displayMessage)}</span>
                    </div>
                </div>
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
        const noticesCountBadge = document.getElementById('total-notices-count');
        
        if (imCountBadge) {
            imCountBadge.textContent = totalIMCount;
        }
        
        if (groupCountBadge) {
            groupCountBadge.textContent = totalGroupCount;
        }
        
        if (noticesCountBadge) {
            noticesCountBadge.textContent = this.unreadNoticesCount;
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
            // Update monitoring indicator with the updated session info
            this.updateMonitoringIndicator(`chat-${session.sessionId}`);
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
            // Update monitoring indicator with the updated session info
            this.updateMonitoringIndicator(`chat-${session.sessionId}`);
        }
    }

    updateRegionInfo(regionInfo) {
        // Update region info in the UI if needed
        const account = this.accounts.find(a => a.accountId === regionInfo.accountId);
        if (account) {
            const previousRegion = account.currentRegion;
            account.currentRegion = regionInfo.name;
            
            if (this.currentAccountId === regionInfo.accountId) {
                document.getElementById('chatAccountStatus').textContent = 
                    `${account.status} • ${regionInfo.name} (${regionInfo.avatarCount} people)`;
                
                // Refresh minimap if region changed (teleport/login to new region)
                if (window.miniMap && previousRegion !== regionInfo.name) {
                    window.miniMap.refresh();
                }
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
                const accountNameElement = document.getElementById('chatAccountName');
                const serviceIcons = `${(status.hasAiBotActive || status.HasAiBotActive) ? '<i class="fas fa-user-circle service-icon" title="AI Bot Active" style="color: #007bff;"></i>&nbsp;' : ''}${(status.hasCorradeActive || status.HasCorradeActive) ? '<i class="fas fa-server service-icon" title="Corrade Active" style="color: #28a745;"></i>&nbsp;' : ''}`;
                accountNameElement.innerHTML = serviceIcons + (status.displayName || `${status.firstName} ${status.lastName}`);
                
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
                    
                    // Show minimap for connected accounts
                    if (window.miniMap) {
                        window.miniMap.show(status.accountId);
                        // Force initial redraw to show any existing avatars
                        setTimeout(() => {
                            if (window.miniMap && typeof window.miniMap.forceRedraw === 'function') {
                                window.miniMap.forceRedraw();
                            }
                        }, 500); // Small delay to allow avatar data to load
                    }
                    
                    // Load recent notices for this account
                    this.loadAccountNotices(status.accountId);
                    
                    // Load groups when account connects
                    this.loadGroups();
                } else {
                    loginBtn.classList.remove('d-none');
                    logoutBtn.classList.add('d-none');
                    regionInfoBtn.classList.add('d-none');
                    // Stop avatar refresh when account disconnects
                    this.stopAvatarRefresh();
                    
                    // Hide minimap for disconnected accounts
                    if (window.miniMap) {
                        window.miniMap.hide();
                    }
                    
                    // Clear nearby avatars when disconnected
                    this.nearbyAvatars = [];
                    this.renderPeopleList();
                    
                    // Clear groups when disconnected
                    this.groups = [];
                    this.renderGroupsList();
                    
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
        alertDiv.style.cssText = `
            position: fixed;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            z-index: 10000;
            min-width: 300px;
            max-width: 500px;
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
        `;
        alertDiv.innerHTML = `
            ${message}
            <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
        `;

        document.body.appendChild(alertDiv);

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

    // Convert UTC timestamp to Second Life Time (Pacific Time - PST/PDT)
    convertToSLT(utcTimestamp) {
        try {
            const date = new Date(utcTimestamp);
            
            // Convert to Pacific Time (automatically handles PST/PDT)
            const options = {
                timeZone: 'America/Los_Angeles',
                hour12: false,
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit'
            };
            
            return date.toLocaleTimeString('en-US', options);
        } catch (error) {
            console.error('Error converting timestamp to SLT:', error);
            return new Date().toLocaleTimeString(); // Fallback to current time
        }
    }

    // Convert UTC timestamp to SLT with date (for longer format displays)
    convertToSLTDateTime(utcTimestamp) {
        try {
            const date = new Date(utcTimestamp);
            
            // Convert to Pacific Time with full date
            const options = {
                timeZone: 'America/Los_Angeles',
                year: 'numeric',
                month: 'short',
                day: '2-digit',
                hour12: false,
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit'
            };
            
            return date.toLocaleString('en-US', options);
        } catch (error) {
            console.error('Error converting timestamp to SLT DateTime:', error);
            return new Date().toLocaleString(); // Fallback to current time
        }
    }

    // Format relative time for interactive notice responses
    formatRelativeTime(timestamp) {
        try {
            const date = new Date(timestamp);
            const now = new Date();
            const diffMs = now - date;
            const diffMins = Math.floor(diffMs / 60000);
            const diffHours = Math.floor(diffMs / 3600000);
            const diffDays = Math.floor(diffMs / 86400000);

            if (diffMins < 1) {
                return 'just now';
            } else if (diffMins < 60) {
                return `${diffMins} min${diffMins !== 1 ? 's' : ''} ago`;
            } else if (diffHours < 24) {
                return `${diffHours} hour${diffHours !== 1 ? 's' : ''} ago`;
            } else if (diffDays < 7) {
                return `${diffDays} day${diffDays !== 1 ? 's' : ''} ago`;
            } else {
                return this.convertToSLT(timestamp);
            }
        } catch (error) {
            console.error('Error formatting relative time:', error);
            return 'some time ago';
        }
    }

    // Format chat timestamp with relative date information for older messages
    formatChatTimestamp(timestamp) {
        try {
            const msgDate = new Date(timestamp);
            const now = new Date();
            
            // Convert both to SLT for proper comparison
            const sltOptions = { timeZone: 'America/Los_Angeles' };
            const msgSltDate = new Date(msgDate.toLocaleString('en-US', sltOptions));
            const nowSltDate = new Date(now.toLocaleString('en-US', sltOptions));
            
            // Get the start of today in SLT
            const todayStart = new Date(nowSltDate);
            todayStart.setHours(0, 0, 0, 0);
            
            // Get the start of yesterday in SLT  
            const yesterdayStart = new Date(todayStart);
            yesterdayStart.setDate(todayStart.getDate() - 1);
            
            const msgDateStart = new Date(msgSltDate);
            msgDateStart.setHours(0, 0, 0, 0);
            
            // Get just the time portion
            const timeOnly = this.convertToSLT(timestamp);
            
            if (msgDateStart.getTime() === todayStart.getTime()) {
                // Today - just show time
                return timeOnly;
            } else if (msgDateStart.getTime() === yesterdayStart.getTime()) {
                // Yesterday - show "yesterday" + time
                return `yesterday<br>${timeOnly}`;
            } else {
                // Older - calculate days ago
                const diffTime = todayStart.getTime() - msgDateStart.getTime();
                const diffDays = Math.floor(diffTime / (1000 * 60 * 60 * 24));
                return `${diffDays} days ago<br>${timeOnly}`;
            }
        } catch (error) {
            console.error('Error formatting chat timestamp:', error);
            // Fallback to regular SLT time
            return this.convertToSLT(timestamp);
        }
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

    handleChatHistoryCleared(accountId, sessionId) {
        // Only handle for the current account
        if (accountId !== this.currentAccountId) return;
        
        console.log(`Chat history cleared for session: ${sessionId}`);
        
        // Clear the messages from the UI
        let chatContainer;
        if (sessionId === 'local-chat') {
            chatContainer = document.getElementById('localChatMessages');
        } else {
            chatContainer = document.getElementById(`messages-${sessionId}`);
        }
        
        if (chatContainer) {
            chatContainer.innerHTML = '';
            console.log(`UI cleared for session: ${sessionId}`);
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
        
        // Use enhanced timestamp formatting with relative date information
        const timestamp = this.formatChatTimestamp(message.timestamp);
        const senderName = this.escapeHtml(message.senderName);
        
        // Check if this is a /me command (personal thought)
        const isPersonalThought = message.message.startsWith('/me ');
        const displayMessage = isPersonalThought ? message.message.substring(4) : message.message;
        const nameFormat = isPersonalThought ? '' : ':';
        
        messageDiv.innerHTML = `
            <div class="chat-message-layout d-flex">
                <div class="chat-message-time">
                    <span class="text-muted small" title="Second Life Time (SLT)">${timestamp}</span>
                </div>
                <div class="chat-message-right">
                    <div class="chat-message-header">
                        <span class="fw-bold">${senderName}${nameFormat}</span>
                    </div>
                    <div class="chat-message-content">
                        <span>${this.renderMessageContent(displayMessage)}</span>
                    </div>
                </div>
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
        
        // Add notice to our notices array and increase unread count
        this.notices.unshift(noticeEvent.notice);
        if (!noticeEvent.notice.isRead) {
            this.unreadNoticesCount++;
            this.updateTabCounts();
        }
        
        // Display notice in the notices tab
        this.displayNoticeInTab(noticeEvent.notice);
        
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
        // Only load notices for the current account
        if (accountId !== this.currentAccountId) return;
        
        console.log("Recent notices loaded:", notices);
        
        // Update our notices array and calculate unread count
        this.notices = notices || [];
        this.unreadNoticesCount = this.notices.filter(n => !n.isRead).length;
        
        // Display all notices in the notices tab
        this.displayNoticesInTab();
        
        // Update the tab count
        this.updateTabCounts();
        
        // Note: We don't need to call handleNoticeReceived for each notice here
        // because displayNoticesInTab() already displays all notices.
        // The handleNoticeReceived is only for live/new notices coming in real-time.
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

    // Auto-Sit Methods
    async loadAutoSitConfig() {
        if (!this.currentAccountId) return;

        try {
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/auto-sit`);
            if (response.ok) {
                const config = await response.json();
                
                // Update UI elements
                document.getElementById('autoSitEnabled').checked = config.enabled || false;
                document.getElementById('autoSitDelay').value = config.delaySeconds || 180;
                document.getElementById('autoSitRestorePresence').checked = config.restorePresenceStatus !== false; // Default to true
                document.getElementById('autoSitLastTarget').textContent = config.targetUuid || 'None';
                document.getElementById('autoSitLastStatus').textContent = config.lastPresenceStatus || 'Online';
                
                // Show/hide settings based on enabled state
                this.toggleAutoSitSettings(config.enabled || false);
            }
        } catch (error) {
            console.error("Error loading auto-sit config:", error);
        }
    }

    async toggleAutoSit(enabled) {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/auto-sit/toggle`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify({ enabled: enabled })
            });

            if (response.ok) {
                const result = await response.json();
                this.showAlert(result.message, "success");
                this.toggleAutoSitSettings(enabled);
            } else {
                const error = await response.text();
                this.showAlert("Failed to toggle auto-sit: " + error, "danger");
                // Revert checkbox state
                document.getElementById('autoSitEnabled').checked = !enabled;
            }
        } catch (error) {
            console.error("Error toggling auto-sit:", error);
            this.showAlert("Failed to toggle auto-sit: " + error.message, "danger");
            // Revert checkbox state
            document.getElementById('autoSitEnabled').checked = !enabled;
        }
    }

    async updateAutoSitDelay(delaySeconds) {
        if (!this.currentAccountId) return;

        try {
            // Get current config first
            const getResponse = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/auto-sit`);
            if (!getResponse.ok) return;
            
            const config = await getResponse.json();
            config.delaySeconds = delaySeconds;

            // Update config
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/auto-sit`, {
                method: 'PUT',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(config)
            });

            if (response.ok) {
                console.log("Auto-sit delay updated to", delaySeconds, "seconds");
            }
        } catch (error) {
            console.error("Error updating auto-sit delay:", error);
        }
    }

    async updateAutoSitPresenceRestore(enabled) {
        if (!this.currentAccountId) return;

        try {
            // Get current config first
            const getResponse = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/auto-sit`);
            if (!getResponse.ok) return;
            
            const config = await getResponse.json();
            config.restorePresenceStatus = enabled;

            // Update config
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/auto-sit`, {
                method: 'PUT',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(config)
            });

            if (response.ok) {
                console.log("Auto-sit presence restore updated to", enabled);
            }
        } catch (error) {
            console.error("Error updating auto-sit presence restore:", error);
        }
    }

    async resitNow() {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            // Get current auto-sit config
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/auto-sit`);
            if (!response.ok) {
                this.showAlert("Failed to get auto-sit configuration", "danger");
                return;
            }
            
            const config = await response.json();
            
            if (!config.targetUuid) {
                this.showAlert("No last sit target available", "warning");
                return;
            }

            // Set the UUID in the input and sit
            document.getElementById('objectIdInput').value = config.targetUuid;
            await this.sitOnObject();
        } catch (error) {
            console.error("Error resitting:", error);
            this.showAlert("Failed to resit: " + error.message, "danger");
        }
    }

    toggleAutoSitSettings(show) {
        const settings = document.getElementById('autoSitSettings');
        settings.style.display = show ? 'block' : 'none';
    }

    async refreshAutoSitStatus() {
        // Reload auto-sit config to show updated presence status
        await this.loadAutoSitConfig();
    }

    async refreshAutoSitStatusIfEnabled() {
        // Only refresh if auto-sit is currently enabled
        try {
            const autoSitEnabled = document.getElementById('autoSitEnabled');
            if (autoSitEnabled && autoSitEnabled.checked) {
                setTimeout(() => this.refreshAutoSitStatus(), 1000); // Small delay to ensure backend processing is complete
            }
        } catch (error) {
            console.error("Error checking auto-sit enabled status:", error);
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

    toggleGroupsList() {
        const groupsCardBody = document.getElementById('groupsCardBody');
        const toggleIcon = document.getElementById('groupsToggleIcon');
        
        if (groupsCardBody.style.display === 'none') {
            // Show the groups list
            groupsCardBody.style.display = 'block';
            toggleIcon.className = 'fas fa-chevron-up';
            localStorage.setItem('groupsListCollapsed', 'false');
        } else {
            // Hide the groups list
            groupsCardBody.style.display = 'none';
            toggleIcon.className = 'fas fa-chevron-down';
            localStorage.setItem('groupsListCollapsed', 'true');
        }
    }

    initializeGroupsToggleState() {
        // Restore the toggle state from localStorage
        const isCollapsed = localStorage.getItem('groupsListCollapsed') === 'true';
        const groupsCardBody = document.getElementById('groupsCardBody');
        const toggleIcon = document.getElementById('groupsToggleIcon');
        
        if (isCollapsed) {
            groupsCardBody.style.display = 'none';
            toggleIcon.className = 'fas fa-chevron-down';
        } else {
            groupsCardBody.style.display = 'block';
            toggleIcon.className = 'fas fa-chevron-up';
        }
    }

    toggleRadarStats() {
        const radarStats = document.getElementById('radarStats');
        const toggleIcon = document.getElementById('radarToggleIcon');
        
        if (radarStats.style.display === 'none') {
            // Show the radar stats
            radarStats.style.display = 'block';
            toggleIcon.className = 'fas fa-chart-line';
            localStorage.setItem('radarStatsVisible', 'true');
            this.updateRadarStats(); // Update immediately when shown
        } else {
            // Hide the radar stats
            radarStats.style.display = 'none';
            toggleIcon.className = 'fas fa-chart-bar';
            localStorage.setItem('radarStatsVisible', 'false');
        }
    }

    initializeRadarToggleState() {
        // Restore the toggle state from localStorage
        const isVisible = localStorage.getItem('radarStatsVisible') === 'true';
        const radarStats = document.getElementById('radarStats');
        const toggleIcon = document.getElementById('radarToggleIcon');
        
        if (isVisible) {
            radarStats.style.display = 'block';
            toggleIcon.className = 'fas fa-chart-line';
        } else {
            radarStats.style.display = 'none';
            toggleIcon.className = 'fas fa-chart-bar';
        }
    }

    initializeUIState() {
        // Ensure welcome message is shown and chat interface is hidden by default
        // This prevents the chat interface from showing before a user selects an account
        document.getElementById('welcomeMessage').classList.remove('d-none');
        document.getElementById('chatInterface').classList.add('d-none');
        document.getElementById('peoplePanel').classList.add('d-none');
        console.log('UI state initialized - showing welcome message');
    }

    // Manual presence control methods
    async toggleAwayStatus() {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        const awayBtn = document.getElementById('setAwayBtn');
        const isCurrentlyAway = awayBtn.dataset.status === 'active';
        
        try {
            if (this.connection) {
                await this.connection.invoke("SetAwayStatus", this.currentAccountId, !isCurrentlyAway);
                this.showAlert(isCurrentlyAway ? "Away status cleared" : "Set to Away", "success");
            }
        } catch (error) {
            console.error('Error setting away status:', error);
            this.showAlert(`Failed to update away status: ${error.message}`, "danger");
        }
    }

    async toggleBusyStatus() {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        const busyBtn = document.getElementById('setBusyBtn');
        const isCurrentlyBusy = busyBtn.dataset.status === 'active';
        
        try {
            if (this.connection) {
                await this.connection.invoke("SetBusyStatus", this.currentAccountId, !isCurrentlyBusy);
                this.showAlert(isCurrentlyBusy ? "Busy status cleared" : "Set to Busy", "success");
            }
        } catch (error) {
            console.error('Error setting busy status:', error);
            this.showAlert(`Failed to update busy status: ${error.message}`, "danger");
        }
    }

    async setOnlineStatus() {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            if (this.connection) {
                // Clear both away and busy status
                await this.connection.invoke("SetAwayStatus", this.currentAccountId, false);
                await this.connection.invoke("SetBusyStatus", this.currentAccountId, false);
                this.showAlert("Set to Online", "success");
            }
        } catch (error) {
            console.error('Error setting online status:', error);
            this.showAlert(`Failed to set online status: ${error.message}`, "danger");
        }
    }

    // Handle presence status changes from SignalR
    onPresenceStatusChanged(accountId, status, statusText) {
        console.log(`Presence status update for ${accountId}: ${status} (${statusText}) [switching: ${this.isSwitchingAccounts}]`);
        
        // Check if this is actually a change or just a sync
        const previousState = this.presenceStates.get(accountId);
        const isActualChange = !previousState || 
            previousState.status !== status || 
            previousState.statusText !== statusText;
        
        // Update local presence state tracking
        this.presenceStates.set(accountId, { status, statusText });
        
        // Update the presence display for the current account
        this.updatePresenceStatusDisplay(accountId, status, statusText);
        
        // Update account list display if needed
        this.updateAccountPresenceDisplay(accountId, status, statusText);
        
        // Only show notification for actual changes (not routine syncing or during account switching)
        if (isActualChange && previousState && !this.isSwitchingAccounts && this.isInitialized) {
            const account = this.accounts.find(a => a.accountId === accountId);
            const accountName = account ? (account.displayName || `${account.firstName} ${account.lastName}`) : 'Unknown Account';
            
            // Only show notifications for significant status changes (Away/Busy) or manual changes to current account
            const isSignificantChange = (status === 'Away' || status === 'Busy') && previousState.status !== status;
            const isCurrentAccountChange = accountId === this.currentAccountId && previousState.statusText !== statusText;
            
            if (isSignificantChange || isCurrentAccountChange) {
                console.log(`Significant presence change for ${accountName}: ${previousState.statusText} → ${statusText}`);
                this.showAlert(`${accountName} is now ${statusText}`, "info");
            } else {
                console.debug(`Minor presence update for ${accountName}: ${previousState.statusText} → ${statusText} (no notification)`);
            }
        }
    }

    // Update presence display in the accounts list
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

    // Update presence states for all accounts (useful after reconnection)
    updateAccountPresenceStates() {
        this.accounts.forEach(account => {
            const presence = this.presenceStates.get(account.accountId) || { status: 'Online', statusText: 'Online' };
            this.updateAccountPresenceDisplay(account.accountId, presence.status, presence.statusText);
        });
    }

    // Update presence status display based on SignalR events
    updatePresenceStatusDisplay(accountId, status, statusText) {
        // Only update if this is for the current account
        if (accountId !== this.currentAccountId) {
            return;
        }

        const currentStatusSpan = document.getElementById('currentPresenceStatus');
        const awayBtn = document.getElementById('setAwayBtn');
        const busyBtn = document.getElementById('setBusyBtn');
        const awayBtnText = document.getElementById('awayBtnText');
        const busyBtnText = document.getElementById('busyBtnText');

        // Update current status display - this is the main element that should always exist
        if (currentStatusSpan) {
            currentStatusSpan.textContent = statusText;
            currentStatusSpan.className = `fw-bold status-${status.toLowerCase()}`;
        } else {
            console.warn('currentPresenceStatus element not found');
        }

        // Update button states - these are optional elements
        if (status === 'Away') {
            if (awayBtn) {
                awayBtn.className = 'btn btn-warning btn-sm w-100';
                awayBtn.dataset.status = 'active';
            }
            if (awayBtnText) awayBtnText.textContent = 'Clear Away';
            if (busyBtn) {
                busyBtn.className = 'btn btn-outline-danger btn-sm w-100';
                busyBtn.dataset.status = 'inactive';
            }
            if (busyBtnText) busyBtnText.textContent = 'Set Busy';
        } else if (status === 'Busy') {
            if (busyBtn) {
                busyBtn.className = 'btn btn-danger btn-sm w-100';
                busyBtn.dataset.status = 'active';
            }
            if (busyBtnText) busyBtnText.textContent = 'Clear Busy';
            if (awayBtn) {
                awayBtn.className = 'btn btn-outline-warning btn-sm w-100';
                awayBtn.dataset.status = 'inactive';
            }
            if (awayBtnText) awayBtnText.textContent = 'Set Away';
        } else { // Online
            if (awayBtn) {
                awayBtn.className = 'btn btn-outline-warning btn-sm w-100';
                awayBtn.dataset.status = 'inactive';
            }
            if (awayBtnText) awayBtnText.textContent = 'Set Away';
            if (busyBtn) {
                busyBtn.className = 'btn btn-outline-danger btn-sm w-100';
                busyBtn.dataset.status = 'inactive';
            }
            if (busyBtnText) busyBtnText.textContent = 'Set Busy';
        }
    }

    async cleanup() {
        console.log("Starting cleanup process...");
        
        // Stop heartbeat and connection validation
        this.stopHeartbeat();
        
        // Clean up any stray modal backdrops
        this.cleanupModalBackdrops();
        
        // Clean up SignalR connections and account groups
        if (this.connection) {
            try {
                // First try to clean up stale connections
                if (this.connection.state === 'Connected') {
                    try {
                        await this.connection.invoke("CleanupStaleConnections");
                        console.log("Cleaned up stale connections");
                    } catch (cleanupError) {
                        console.warn("Failed to cleanup stale connections:", cleanupError);
                    }
                    
                    // Leave current account group on page unload
                    if (this.currentAccountId) {
                        try {
                            await this.connection.invoke("LeaveAccountGroup", this.currentAccountId);
                            console.log(`Left account group for ${this.currentAccountId} during cleanup`);
                        } catch (error) {
                            console.error("Error leaving account group during cleanup:", error);
                        }
                    }
                    
                    // Notify server of browser close
                    try {
                        await this.connection.invoke("HandleBrowserClose");
                        console.log("Notified server of browser close");
                    } catch (error) {
                        console.warn("Failed to notify server of browser close:", error);
                    }
                }
            } catch (error) {
                console.error("Error during SignalR cleanup:", error);
            }
        }
        
        // Stop avatar refresh
        this.stopAvatarRefresh();
        
        console.log("Cleanup process completed");
    }

    // Display a notice in the notices tab
    displayNoticeInTab(notice) {
        const noticesContainer = document.getElementById('noticesMessages');
        if (!noticesContainer) return;

        const noticeDiv = document.createElement('div');
        noticeDiv.className = `chat-message notice mb-3 p-3 border rounded`;
        noticeDiv.id = `notice-${notice.id}`;
        // Notice.Type enum: 0=Group, 1=Region, 2=System, 5=FriendshipRequest, 6=GroupInvitation
        let backgroundColor = '#563838'; // Default
        if (notice.type === 0) backgroundColor = '#213c50'; // Group
        else if (notice.type === 5) backgroundColor = '#4a73a9'; // FriendshipRequest (primary blue)
        else if (notice.type === 6) backgroundColor = '#28a745'; // GroupInvitation (success green)
        
        noticeDiv.style.backgroundColor = backgroundColor;
        
        // Always use SLT time - prefer sltTime from server, fallback to converting timestamp to SLT
        const timestamp = notice.sltTime || this.convertToSLT(notice.timestamp);
        const typeNames = { 0: 'Group', 1: 'Region', 2: 'System', 5: 'Friendship', 6: 'Invitation' };
        const typeName = typeNames[notice.type] || 'Unknown';
        
        noticeDiv.innerHTML = `
            <div class="d-flex justify-content-between align-items-start mb-2">
                <div>
                    <strong class="notice-title">${this.escapeHtml(notice.title)}</strong>
                    <small class="text-muted ms-2" title="Second Life Time (SLT)">${timestamp}</small>
                </div>
                <span class="badge ${notice.type === 0 ? 'bg-primary' : notice.type === 5 ? 'bg-info' : notice.type === 6 ? 'bg-success' : 'bg-secondary'}">${typeName}</span>
            </div>
            <div class="notice-from mb-2">
                <strong>From:</strong> ${this.escapeHtml(notice.fromName)}
                ${notice.groupName ? `<br><strong>Group:</strong> ${this.escapeHtml(notice.groupName)}` : ''}
            </div>
            <div class="notice-message">${this.escapeHtml(notice.message)}</div>
            ${notice.hasAttachment ? `
                <div class="notice-attachment mt-2 p-2 bg-light rounded">
                    <i class="fas fa-paperclip me-1"></i>
                    <strong>Attachment:</strong> ${this.escapeHtml(notice.attachmentName || 'Unknown Item')}
                    <br><small class="text-muted">Type: ${this.escapeHtml(notice.attachmentType || 'Unknown')}</small>
                    ${notice.requiresAcknowledgment && !notice.isAcknowledged ? `
                        <br><button class="btn btn-sm btn-primary mt-1" onclick="radegastClient.acknowledgeNotice('${notice.id}')">
                            Accept Attachment
                        </button>
                    ` : ''}
                </div>
            ` : ''}
            ${notice.isInteractive && !notice.hasResponse ? `
                <div class="interactive-notice-controls mt-3 p-2 bg-info bg-opacity-10 border border-info rounded">
                    <div class="d-flex justify-content-center gap-2">
                        <button class="btn btn-success btn-sm" onclick="radegastClient.respondToInteractiveNotice('${notice.externalRequestId}', '${notice.type === 5 ? 'FriendshipRequest' : 'GroupInvitation'}', true)" title="Accept">
                            <i class="fas fa-check me-1"></i>Accept
                        </button>
                        <button class="btn btn-danger btn-sm" onclick="radegastClient.respondToInteractiveNotice('${notice.externalRequestId}', '${notice.type === 5 ? 'FriendshipRequest' : 'GroupInvitation'}', false)" title="Decline">
                            <i class="fas fa-times me-1"></i>Decline
                        </button>
                    </div>
                </div>
            ` : notice.isInteractive && notice.hasResponse ? `
                <div class="interactive-notice-response mt-3 p-2 ${notice.acceptedResponse ? 'bg-success' : 'bg-danger'} bg-opacity-10 border ${notice.acceptedResponse ? 'border-success' : 'border-danger'} rounded">
                    <div class="text-center">
                        <i class="fas ${notice.acceptedResponse ? 'fa-check-circle text-success' : 'fa-times-circle text-danger'} me-1"></i>
                        <strong>${notice.acceptedResponse ? 'Accepted' : 'Declined'}</strong>
                        <small class="text-muted ms-2">${this.formatRelativeTime(notice.respondedAt)}</small>
                    </div>
                </div>
            ` : ''}
            <div class="d-flex justify-content-end mt-2">
                <button class="btn btn-sm btn-outline-secondary" onclick="radegastClient.dismissNotice('${notice.id}')" title="Dismiss this notice">
                    OK
                </button>
            </div>
        `;
        
        noticesContainer.appendChild(noticeDiv);
        this.scrollChatToBottom('notices');
    }

    // Display all notices in the notices tab
    displayNoticesInTab() {
        const noticesContainer = document.getElementById('noticesMessages');
        if (!noticesContainer) return;

        noticesContainer.innerHTML = '';
        
        // Sort notices by timestamp, newest first
        const sortedNotices = [...this.notices].sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));
        
        sortedNotices.forEach(notice => {
            this.displayNoticeInTab(notice);
        });
    }

    // Mark all notices as read when the notices tab is activated
    async markAllNoticesAsRead() {
        if (!this.currentAccountId || this.unreadNoticesCount === 0) return;

        try {
            // Mark all unread notices as read in memory
            this.notices.forEach(notice => {
                if (!notice.isRead) {
                    notice.isRead = true;
                }
            });
            
            this.unreadNoticesCount = 0;
            this.updateTabCounts();

            // Refresh the display to show read status
            this.displayNoticesInTab();

            // Get updated count from backend
            if (this.connection) {
                await this.connection.invoke("GetUnreadNoticesCount", this.currentAccountId);
            }
            
            console.log("Marked all notices as read");
        } catch (error) {
            console.error("Error marking notices as read:", error);
        }
    }

    // Acknowledge a notice (for notices with attachments)
    async acknowledgeNotice(noticeId) {
        if (!this.currentAccountId) return;

        try {
            if (this.connection) {
                await this.connection.invoke("AcknowledgeNotice", this.currentAccountId, noticeId);
                
                // Update the notice in our local array
                const notice = this.notices.find(n => n.id === noticeId);
                if (notice) {
                    notice.isAcknowledged = true;
                    this.displayNoticesInTab(); // Refresh the display
                }
                
                this.showAlert("Notice acknowledged", "success");
            }
        } catch (error) {
            console.error("Error acknowledging notice:", error);
            this.showAlert("Error acknowledging notice", "danger");
        }
    }

    // Handle interactive friendship request received via SignalR
    handleInteractiveFriendshipRequestReceived(request) {
        console.log("Interactive friendship request received:", request);
        
        // Only handle requests for the current account
        if (request.accountId !== this.currentAccountId) {
            return;
        }
        
        // Create or update the interactive notice
        let notice = this.notices.find(n => n.externalRequestId === request.requestId);
        if (notice) {
            // Update existing notice
            notice.isInteractive = true;
            notice.hasResponse = false;
            notice.expiresAt = request.expiresAt;
        } else {
            // Create new notice for friendship request
            notice = {
                id: `friendship-${request.requestId}`,
                externalRequestId: request.requestId,
                type: 5, // FriendshipRequest type
                title: "Friendship Offer",
                message: `${request.fromName} is offering friendship.`,
                fromName: request.fromName,
                timestamp: request.timestamp,
                sltTime: request.sltTime,
                isRead: false,
                isInteractive: true,
                hasResponse: false,
                acceptedResponse: null,
                respondedAt: null,
                expiresAt: request.expiresAt,
                sessionId: request.sessionId
            };
            
            this.notices.unshift(notice);
            this.unreadNoticesCount++;
            
            // Display the notice
            this.displayNoticeInTab(notice);
        }
        
        this.updateTabCounts();
        
        // Show notification sound/alert if notices tab is not active
        if (this.currentChatSession !== 'notices') {
            this.showAlert(`Friendship offer from ${request.fromName}`, "info");
        }

        // Show modal for immediate user interaction
        this.showInteractiveNoticeModal(notice);
    }

    // Handle interactive group invitation received via SignalR
    handleInteractiveGroupInvitationReceived(invitation) {
        console.log("Interactive group invitation received:", invitation);
        
        // Only handle invitations for the current account
        if (invitation.accountId !== this.currentAccountId) {
            return;
        }
        
        // Create or update the interactive notice
        let notice = this.notices.find(n => n.externalRequestId === invitation.requestId);
        if (notice) {
            // Update existing notice
            notice.isInteractive = true;
            notice.hasResponse = false;
            notice.expiresAt = invitation.expiresAt;
        } else {
            // Create new notice for group invitation
            notice = {
                id: `group-invitation-${invitation.requestId}`,
                externalRequestId: invitation.requestId,
                type: 6, // GroupInvitation type
                title: "Group Invitation",
                message: `${invitation.fromName} has invited you to join the group "${invitation.groupName}".`,
                fromName: invitation.fromName,
                groupName: invitation.groupName,
                timestamp: invitation.timestamp,
                sltTime: invitation.sltTime,
                isRead: false,
                isInteractive: true,
                hasResponse: false,
                acceptedResponse: null,
                respondedAt: null,
                expiresAt: invitation.expiresAt,
                sessionId: invitation.sessionId
            };
            
            this.notices.unshift(notice);
            this.unreadNoticesCount++;
            
            // Display the notice
            this.displayNoticeInTab(notice);
        }
        
        this.updateTabCounts();
        
        // Show notification sound/alert if notices tab is not active
        if (this.currentChatSession !== 'notices') {
            this.showAlert(`Group invitation from ${invitation.fromName} for "${invitation.groupName}"`, "info");
        }

        // Show modal for immediate user interaction
        this.showInteractiveNoticeModal(notice);
    }

    // Respond to an interactive notice (friendship request or group invitation)
    async respondToInteractiveNotice(externalRequestId, requestType, accept) {
        if (!this.currentAccountId) {
            this.showAlert("No account selected", "warning");
            return;
        }

        try {
            if (!this.connection) {
                this.showAlert("Not connected to server", "warning");
                return;
            }

            console.log(`Responding to ${requestType} ${externalRequestId}: ${accept ? 'accept' : 'decline'}`);

            // Call appropriate SignalR method based on request type
            if (requestType === 'FriendshipRequest') {
                await this.connection.invoke("RespondToFriendshipRequest", this.currentAccountId, externalRequestId, accept);
            } else if (requestType === 'GroupInvitation') {
                await this.connection.invoke("RespondToGroupInvitation", this.currentAccountId, externalRequestId, accept);
            }

            // Update the notice in our local array
            const notice = this.notices.find(n => n.externalRequestId === externalRequestId);
            if (notice) {
                notice.hasResponse = true;
                notice.acceptedResponse = accept;
                notice.respondedAt = new Date().toISOString();
                
                // Refresh the display to show the response
                this.displayNoticesInTab();
            }

            const action = accept ? 'accepted' : 'declined';
            const typeText = requestType === 'FriendshipRequest' ? 'friendship request' : 'group invitation';
            this.showAlert(`${typeText} ${action}`, "success");

        } catch (error) {
            console.error(`Error responding to ${requestType}:`, error);
            this.showAlert(`Failed to respond to ${requestType.toLowerCase()}`, "danger");
        }
    }

    // Handle response confirmation from server
    handleInteractiveNoticeResponse(noticeId, response) {
        console.log(`Interactive notice response confirmed: ${noticeId} = ${response}`);
        
        // Find and update the notice
        const notice = this.notices.find(n => n.id === noticeId || n.externalRequestId === noticeId);
        if (notice) {
            notice.hasResponse = true;
            notice.acceptedResponse = response.accepted;
            notice.respondedAt = response.respondedAt;
            
            // Refresh the display
            this.displayNoticesInTab();
        }
    }

    // Show interactive notice modal for friendship requests and group invitations
    showInteractiveNoticeModal(notice) {
        // Set modal content based on notice type
        const modal = document.getElementById('interactiveNoticeModal');
        const titleElement = document.getElementById('interactiveNoticeTitle');
        const iconElement = document.getElementById('interactiveNoticeIcon');
        const fromNameElement = document.getElementById('interactiveNoticeFromName');
        const messageElement = document.getElementById('interactiveNoticeMessage');
        const detailsElement = document.getElementById('interactiveNoticeDetails');
        const timeElement = document.getElementById('interactiveNoticeTime');
        const expiryElement = document.getElementById('interactiveNoticeExpiry');
        const expiryTimeElement = document.getElementById('interactiveNoticeExpiryTime');
        const acceptBtn = document.getElementById('interactiveNoticeAccept');
        const declineBtn = document.getElementById('interactiveNoticeDecline');

        if (notice.type === 5) { // Friendship Request
            titleElement.textContent = 'Friendship Offer';
            iconElement.className = 'fas fa-user-friends me-2';
            messageElement.textContent = 'is offering friendship.';
            detailsElement.innerHTML = `
                <p>Do you want to become friends with <strong>${this.escapeHtml(notice.fromName)}</strong>?</p>
                <p class="text-muted small">Accepting will add them to your friends list and allow you to see when they're online.</p>
            `;
        } else if (notice.type === 6) { // Group Invitation
            titleElement.textContent = 'Group Invitation';
            iconElement.className = 'fas fa-users me-2';
            messageElement.textContent = `has invited you to join "${notice.groupName}".`;
            detailsElement.innerHTML = `
                <p>Do you want to join the group <strong>${this.escapeHtml(notice.groupName)}</strong>?</p>
                <p class="text-muted small">Joining will add you to the group and allow you to participate in group activities.</p>
            `;
        }

        fromNameElement.textContent = notice.fromName;
        timeElement.textContent = notice.sltTime || this.convertToSLT(notice.timestamp);

        // Show expiry if available
        if (notice.expiresAt) {
            expiryElement.classList.remove('d-none');
            expiryTimeElement.textContent = this.convertToSLT(notice.expiresAt);
        } else {
            expiryElement.classList.add('d-none');
        }

        // Clear any existing event listeners
        const newAcceptBtn = acceptBtn.cloneNode(true);
        const newDeclineBtn = declineBtn.cloneNode(true);
        acceptBtn.parentNode.replaceChild(newAcceptBtn, acceptBtn);
        declineBtn.parentNode.replaceChild(newDeclineBtn, declineBtn);

        // Add event listeners for the buttons
        newAcceptBtn.addEventListener('click', () => {
            this.respondToInteractiveNotice(
                notice.externalRequestId,
                notice.type === 5 ? 'FriendshipRequest' : 'GroupInvitation',
                true
            );
            const modalInstance = bootstrap.Modal.getInstance(modal);
            modalInstance.hide();
        });

        newDeclineBtn.addEventListener('click', () => {
            this.respondToInteractiveNotice(
                notice.externalRequestId,
                notice.type === 5 ? 'FriendshipRequest' : 'GroupInvitation',
                false
            );
            const modalInstance = bootstrap.Modal.getInstance(modal);
            modalInstance.hide();
        });

        // Show the modal
        const modalInstance = new bootstrap.Modal(modal);
        modalInstance.show();
    }

    // Test functions for interactive notices (can be called from browser console)
    testFriendshipRequest() {
        const testRequest = {
            accountId: this.currentAccountId,
            requestId: 'test-friendship-' + Date.now(),
            fromName: 'Test User',
            timestamp: new Date().toISOString(),
            sltTime: this.convertToSLT(new Date().toISOString()),
            expiresAt: new Date(Date.now() + 300000).toISOString(), // 5 minutes from now
            sessionId: 'test-session'
        };
        
        console.log('Testing friendship request:', testRequest);
        this.handleInteractiveFriendshipRequestReceived(testRequest);
    }
    
    // Test function for new timestamp formatting (can be called from browser console)
    testTimestampFormatting() {
        const now = new Date();
        
        // Create test timestamps for different scenarios
        const timestamps = [
            { 
                name: 'Now (today)', 
                timestamp: now 
            },
            { 
                name: 'Earlier today', 
                timestamp: new Date(now.getTime() - (3 * 60 * 60 * 1000)) // 3 hours ago
            },
            { 
                name: 'Yesterday', 
                timestamp: new Date(now.getTime() - (25 * 60 * 60 * 1000)) // 25 hours ago
            },
            { 
                name: '2 days ago', 
                timestamp: new Date(now.getTime() - (2 * 24 * 60 * 60 * 1000))
            },
            { 
                name: '5 days ago', 
                timestamp: new Date(now.getTime() - (5 * 24 * 60 * 60 * 1000))
            }
        ];
        
        console.log('Testing timestamp formatting:');
        timestamps.forEach(test => {
            const formatted = this.formatChatTimestamp(test.timestamp.toISOString());
            console.log(`${test.name}: "${formatted}"`);
        });
        
        return timestamps.map(test => ({
            scenario: test.name,
            original: test.timestamp.toISOString(),
            formatted: this.formatChatTimestamp(test.timestamp.toISOString())
        }));
    }

    testGroupInvitation() {
        const testInvitation = {
            accountId: this.currentAccountId,
            requestId: 'test-group-' + Date.now(),
            fromName: 'Group Admin',
            groupName: 'Test Group',
            timestamp: new Date().toISOString(),
            sltTime: this.convertToSLT(new Date().toISOString()),
            expiresAt: new Date(Date.now() + 600000).toISOString(), // 10 minutes from now
            sessionId: 'test-session'
        };
        
        console.log('Testing group invitation:', testInvitation);
        this.handleInteractiveGroupInvitationReceived(testInvitation);
    }

    // Dismiss a notice (removes it completely)
    async dismissNotice(noticeId) {
        if (!this.currentAccountId) return;

        try {
            if (this.connection) {
                await this.connection.invoke("DismissNotice", this.currentAccountId, noticeId);
                
                // Remove the notice from our local array
                const noticeIndex = this.notices.findIndex(n => n.id === noticeId);
                if (noticeIndex !== -1) {
                    const notice = this.notices[noticeIndex];
                    this.notices.splice(noticeIndex, 1);
                    
                    // Update unread count if the notice was unread
                    if (!notice.isRead) {
                        this.unreadNoticesCount = Math.max(0, this.unreadNoticesCount - 1);
                        this.updateTabCounts();
                    }
                }
                
                // Remove the notice element from the DOM
                const noticeElement = document.getElementById(`notice-${noticeId}`);
                if (noticeElement) {
                    noticeElement.remove();
                }
                
                this.showAlert("Notice dismissed", "success");
            }
        } catch (error) {
            console.error("Error dismissing notice:", error);
            this.showAlert("Error dismissing notice", "danger");
        }
    }

    // Script Dialog Methods
    handleScriptDialogReceived(dialog) {
        console.log("Script dialog received:", dialog);
        
        // Only show dialogs for the current active account
        if (dialog.accountId !== this.currentAccountId) {
            return;
        }
        
        // Check if this dialog is already in the queue to prevent duplicates
        const existingDialog = this.scriptDialogQueue.find(d => d.dialogId === dialog.dialogId);
        if (existingDialog) {
            console.log(`Dialog ${dialog.dialogId} already in queue, ignoring duplicate`);
            return;
        }
        
        // Add dialog to queue
        this.scriptDialogQueue.push(dialog);
        console.log("Script dialog added to queue. Queue length:", this.scriptDialogQueue.length);
        
        // Process queue if not already showing a dialog
        this.processScriptDialogQueue();
    }

    handleScriptDialogClosed(accountId, dialogId) {
        console.log(`Script dialog closed: ${dialogId}, current displayed: ${this.currentDialogId}`);
        
        // Remove any matching dialog from the queue to prevent it from showing again
        const initialQueueLength = this.scriptDialogQueue.length;
        this.scriptDialogQueue = this.scriptDialogQueue.filter(dialog => dialog.dialogId !== dialogId);
        const removedCount = initialQueueLength - this.scriptDialogQueue.length;
        
        if (removedCount > 0) {
            console.log(`Removed ${removedCount} dialog(s) with ID ${dialogId} from queue. Queue length now: ${this.scriptDialogQueue.length}`);
        }
        
        // Only reset the state if this is the currently displayed dialog
        if (this.currentDialogId === dialogId) {
            // Mark that we're no longer showing a script dialog
            this.isShowingScriptDialog = false;
            this.currentDialogId = null;
            
            // Hide any open dialog modals
            const dialogModal = document.getElementById('scriptDialogModal');
            if (dialogModal) {
                const modal = bootstrap.Modal.getInstance(dialogModal);
                if (modal) {
                    modal.hide();
                }
            }
            
            // Clean up any stray modal backdrops
            this.cleanupModalBackdrops();
            
            // Process next dialog in queue after a short delay
            setTimeout(() => {
                this.processScriptDialogQueue();
            }, 100);
        } else {
            console.log(`Dialog ${dialogId} was closed but it's not the current dialog (${this.currentDialogId}), not processing queue`);
        }
    }

    processScriptDialogQueue() {
        // Don't show new dialog if one is already being shown or queue is empty
        if (this.isShowingScriptDialog || this.scriptDialogQueue.length === 0) {
            console.log(`Cannot process dialog queue: isShowing=${this.isShowingScriptDialog}, queueLength=${this.scriptDialogQueue.length}, currentDialogId=${this.currentDialogId}`);
            return;
        }
        
        // Get the next dialog from the queue
        const dialog = this.scriptDialogQueue.shift();
        console.log(`Processing script dialog from queue: ${dialog.dialogId}. Remaining in queue: ${this.scriptDialogQueue.length}`);
        
        // Mark that we're showing a dialog
        this.isShowingScriptDialog = true;
        this.currentDialogId = dialog.dialogId;
        
        // Clean up any stray modal backdrops before showing new dialog
        this.cleanupModalBackdrops();
        
        this.showScriptDialog(dialog);
    }

    handleScriptPermissionReceived(permission) {
        console.log("Script permission received:", permission);
        
        // Only show permissions for the current active account
        if (permission.accountId !== this.currentAccountId) {
            return;
        }
        
        // Check if this permission is already in the queue to prevent duplicates
        const existingPermission = this.scriptPermissionQueue.find(p => p.requestId === permission.requestId);
        if (existingPermission) {
            console.log(`Permission ${permission.requestId} already in queue, ignoring duplicate`);
            return;
        }
        
        // Add permission to queue
        this.scriptPermissionQueue.push(permission);
        console.log("Script permission added to queue. Queue length:", this.scriptPermissionQueue.length);
        
        // Process queue if not already showing a permission dialog
        this.processScriptPermissionQueue();
    }

    handleScriptPermissionClosed(accountId, requestId) {
        console.log(`Script permission closed: ${requestId}, current displayed: ${this.currentPermissionId}`);
        
        // Remove any matching permission from the queue to prevent it from showing again
        const initialQueueLength = this.scriptPermissionQueue.length;
        this.scriptPermissionQueue = this.scriptPermissionQueue.filter(permission => permission.requestId !== requestId);
        const removedCount = initialQueueLength - this.scriptPermissionQueue.length;
        
        if (removedCount > 0) {
            console.log(`Removed ${removedCount} permission(s) with ID ${requestId} from queue. Queue length now: ${this.scriptPermissionQueue.length}`);
        }
        
        // Only reset the state if this is the currently displayed permission
        if (this.currentPermissionId === requestId) {
            // Mark that we're no longer showing a script permission
            this.isShowingScriptPermission = false;
            this.currentPermissionId = null;
            
            // Hide any open permission modals
            const permissionModal = document.getElementById('scriptPermissionModal');
            if (permissionModal) {
                const modal = bootstrap.Modal.getInstance(permissionModal);
                if (modal) {
                    modal.hide();
                }
            }
            
            // Clean up any stray modal backdrops
            this.cleanupModalBackdrops();
            
            // Process next permission in queue after a short delay
            setTimeout(() => {
                this.processScriptPermissionQueue();
            }, 100);
        } else {
            console.log(`Permission ${requestId} was closed but it's not the current permission (${this.currentPermissionId}), not processing queue`);
        }
    }

    processScriptPermissionQueue() {
        // Don't show new permission if one is already being shown or queue is empty
        if (this.isShowingScriptPermission || this.scriptPermissionQueue.length === 0) {
            console.log(`Cannot process permission queue: isShowing=${this.isShowingScriptPermission}, queueLength=${this.scriptPermissionQueue.length}, currentPermissionId=${this.currentPermissionId}`);
            return;
        }
        
        // Get the next permission from the queue
        const permission = this.scriptPermissionQueue.shift();
        console.log(`Processing script permission from queue: ${permission.requestId}. Remaining in queue: ${this.scriptPermissionQueue.length}`);
        
        // Mark that we're showing a permission
        this.isShowingScriptPermission = true;
        this.currentPermissionId = permission.requestId;
        
        // Clean up any stray modal backdrops before showing new permission dialog
        this.cleanupModalBackdrops();
        
        this.showScriptPermission(permission);
    }

    // Utility method to clean up any stray modal backdrops
    cleanupModalBackdrops() {
        // Remove any orphaned modal backdrops that might be left behind
        const backdrops = document.querySelectorAll('.modal-backdrop');
        backdrops.forEach(backdrop => {
            console.log("Removing stray modal backdrop");
            backdrop.remove();
        });
        
        // Also remove the modal-open class from body if no modals are actually open
        const openModals = document.querySelectorAll('.modal.show');
        if (openModals.length === 0) {
            document.body.classList.remove('modal-open');
            document.body.style.overflow = '';
            document.body.style.paddingRight = '';
        }
    }

    showScriptDialog(dialog) {
        const modal = document.getElementById('scriptDialogModal');
        const objectNameEl = document.getElementById('scriptDialogObjectName');
        const ownerNameEl = document.getElementById('scriptDialogOwnerName');
        const messageEl = document.getElementById('scriptDialogMessage');
        const textInputDiv = document.getElementById('scriptTextInputDiv');
        const textInput = document.getElementById('scriptTextInput');
        const buttonsDiv = document.getElementById('scriptDialogButtons');
        const sendBtn = document.getElementById('scriptDialogSend');
        const ignoreBtn = document.getElementById('scriptDialogIgnore');

        if (!modal || !objectNameEl || !ownerNameEl || !messageEl || !textInputDiv || !textInput || !buttonsDiv || !sendBtn || !ignoreBtn) {
            console.error("Script dialog modal elements not found");
            // Mark as not showing so queue can continue
            this.isShowingScriptDialog = false;
            return;
        }

        // Set dialog content
        objectNameEl.textContent = dialog.objectName || 'Unknown Object';
        ownerNameEl.textContent = `owned by ${dialog.ownerName || 'Unknown'}`;
        messageEl.innerHTML = this.escapeHtml(dialog.message || '').replace(/\n/g, '<br>');

        // Clear previous content completely to prevent stacking
        buttonsDiv.innerHTML = '';
        textInput.value = '';
        
        // Remove any existing script dialog grids (extra safety)
        const existingGrids = buttonsDiv.querySelectorAll('.script-dialog-grid');
        existingGrids.forEach(grid => grid.remove());
        
        // Remove any existing buttons (extra safety)
        const existingButtons = buttonsDiv.querySelectorAll('button');
        existingButtons.forEach(btn => btn.remove());

        if (dialog.isTextInput) {
            // Show text input for llTextBox dialogs
            textInputDiv.classList.remove('d-none');
            buttonsDiv.classList.add('d-none');
            sendBtn.classList.remove('d-none');
            textInput.focus();

            // Set up text input event handlers
            const sendTextResponse = () => {
                const responseText = textInput.value.trim();
                this.respondToScriptDialog(dialog.dialogId, -1, '', responseText);
            };

            // Remove any existing event listeners
            sendBtn.replaceWith(sendBtn.cloneNode(true));
            const newSendBtn = document.getElementById('scriptDialogSend');
            
            newSendBtn.addEventListener('click', sendTextResponse);
            textInput.addEventListener('keypress', (e) => {
                if (e.key === 'Enter') {
                    sendTextResponse();
                }
            });
        } else {
            // Show buttons for choice dialogs
            textInputDiv.classList.add('d-none');
            buttonsDiv.classList.remove('d-none');
            sendBtn.classList.add('d-none');

            // Create buttons in Second Life grid layout
            // SL Button Order:
            // 9  10  11
            // 6   7   8  
            // 3   4   5
            // 0   1   2
            this.createDialogButtonGrid(buttonsDiv, dialog.buttons, dialog.dialogId);
        }

        // Set up ignore button
        ignoreBtn.replaceWith(ignoreBtn.cloneNode(true));
        const newIgnoreBtn = document.getElementById('scriptDialogIgnore');
        newIgnoreBtn.addEventListener('click', () => {
            this.dismissScriptDialog(dialog.dialogId);
        });

        // Show the modal
        const modalInstance = new bootstrap.Modal(modal);
        modalInstance.show();

        // Store dialog data for later use
        modal.dataset.dialogId = dialog.dialogId;
        modal.dataset.accountId = dialog.accountId;
    }

    showScriptPermission(permission) {
        const modal = document.getElementById('scriptPermissionModal');
        const objectNameEl = document.getElementById('permissionObjectName');
        const objectOwnerEl = document.getElementById('permissionObjectOwner');
        const descriptionEl = document.getElementById('permissionDescription');
        const muteBtn = document.getElementById('scriptPermissionMute');
        const denyBtn = document.getElementById('scriptPermissionDeny');
        const grantBtn = document.getElementById('scriptPermissionGrant');

        if (!modal || !objectNameEl || !objectOwnerEl || !descriptionEl || !muteBtn || !denyBtn || !grantBtn) {
            console.error("Script permission modal elements not found");
            // Mark as not showing so queue can continue
            this.isShowingScriptPermission = false;
            return;
        }

        // Set permission content
        objectNameEl.textContent = permission.objectName || 'Unknown Object';
        objectOwnerEl.textContent = permission.objectOwner || 'Unknown';
        descriptionEl.innerHTML = `<strong>${this.escapeHtml(permission.permissionsDescription || 'unknown permissions')}</strong>`;

        // Set up button event handlers
        const setupButtonHandler = (button, action) => {
            button.replaceWith(button.cloneNode(true));
            const newButton = document.getElementById(button.id);
            newButton.addEventListener('click', () => {
                this.respondToScriptPermission(permission.requestId, action);
            });
        };

        setupButtonHandler(muteBtn, 'mute');
        setupButtonHandler(denyBtn, 'deny');
        setupButtonHandler(grantBtn, 'grant');

        // Show the modal
        const modalInstance = new bootstrap.Modal(modal);
        modalInstance.show();

        // Store permission data for later use
        modal.dataset.requestId = permission.requestId;
        modal.dataset.accountId = permission.accountId;
    }

    createDialogButtonGrid(container, buttons, dialogId) {
        // Clear the container completely to prevent stacking
        container.innerHTML = '';
        
        // Remove any existing grid containers (extra safety)
        const existingGrids = container.querySelectorAll('.script-dialog-grid');
        existingGrids.forEach(grid => grid.remove());
        
        // Second Life button grid layout mapping
        // Grid positions (row, col) to button indices:
        const gridMap = [
            [9, 10, 11],  // Top row
            [6, 7, 8],    // Second row
            [3, 4, 5],    // Third row
            [0, 1, 2]     // Bottom row
        ];
        
        // Create a 4x3 grid container
        const gridContainer = document.createElement('div');
        gridContainer.className = 'script-dialog-grid';
        gridContainer.style.cssText = `
            display: grid;
            grid-template-columns: repeat(3, 1fr);
            grid-template-rows: repeat(4, auto);
            gap: 8px;
            max-width: 300px;
            margin: 0 auto;
        `;
        
        // Create grid cells
        for (let row = 0; row < 4; row++) {
            let hasButtonsInRow = false;
            
            // Check if this row has any buttons
            for (let col = 0; col < 3; col++) {
                const buttonIndex = gridMap[row][col];
                if (buttonIndex < buttons.length) {
                    hasButtonsInRow = true;
                    break;
                }
            }
            
            // Only create row if it has buttons
            if (hasButtonsInRow) {
                for (let col = 0; col < 3; col++) {
                    const buttonIndex = gridMap[row][col];
                    const gridCell = document.createElement('div');
                    
                    if (buttonIndex < buttons.length) {
                        // Create button for this position
                        const button = document.createElement('button');
                        button.type = 'button';
                        button.className = 'btn btn-outline-primary btn-sm';
                        button.style.cssText = 'width: 100%; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;';
                        button.textContent = buttons[buttonIndex];
                        button.title = buttons[buttonIndex]; // Tooltip for long text
                        
                        button.addEventListener('click', () => {
                            this.respondToScriptDialog(dialogId, buttonIndex, buttons[buttonIndex]);
                        });
                        
                        gridCell.appendChild(button);
                    } else {
                        // Empty cell
                        gridCell.style.visibility = 'hidden';
                    }
                    
                    gridContainer.appendChild(gridCell);
                }
            }
        }
        
        container.appendChild(gridContainer);
    }

    async respondToScriptDialog(dialogId, buttonIndex, buttonText, textInput = null) {
        if (!this.currentAccountId) return;

        console.log(`Responding to script dialog: ${dialogId} with button ${buttonIndex} (${buttonText})`);

        try {
            const request = {
                accountId: this.currentAccountId,
                dialogId: dialogId,
                buttonIndex: buttonIndex,
                buttonText: buttonText || '',
                textInput: textInput
            };

            if (this.connection) {
                await this.connection.invoke("RespondToScriptDialog", request);
                
                // Mark that we're no longer showing a script dialog
                this.isShowingScriptDialog = false;
                this.currentDialogId = null;
                
                // Immediately hide the modal after sending the response
                const dialogModal = document.getElementById('scriptDialogModal');
                if (dialogModal) {
                    const modal = bootstrap.Modal.getInstance(dialogModal);
                    if (modal) {
                        modal.hide();
                    }
                }
                
                // Clean up any modal backdrops after a short delay
                setTimeout(() => {
                    this.cleanupModalBackdrops();
                }, 300);
            }
        } catch (error) {
            console.error("Error responding to script dialog:", error);
            this.showAlert("Error responding to script dialog", "danger");
        }
    }

    async dismissScriptDialog(dialogId) {
        if (!this.currentAccountId) return;

        console.log(`Dismissing script dialog: ${dialogId}`);

        try {
            const request = {
                accountId: this.currentAccountId,
                dialogId: dialogId
            };

            if (this.connection) {
                await this.connection.invoke("DismissScriptDialog", request);
                
                // Mark that we're no longer showing a script dialog
                this.isShowingScriptDialog = false;
                this.currentDialogId = null;
                
                // Immediately hide the modal after sending the dismiss request
                const dialogModal = document.getElementById('scriptDialogModal');
                if (dialogModal) {
                    const modal = bootstrap.Modal.getInstance(dialogModal);
                    if (modal) {
                        modal.hide();
                    }
                }
                
                // Clean up any modal backdrops after a short delay
                setTimeout(() => {
                    this.cleanupModalBackdrops();
                }, 300);
            }
        } catch (error) {
            console.error("Error dismissing script dialog:", error);
            this.showAlert("Error dismissing script dialog", "danger");
        }
    }

    async respondToScriptPermission(requestId, action) {
        if (!this.currentAccountId) return;

        console.log(`Responding to script permission: ${requestId} with action ${action}`);

        try {
            const request = {
                accountId: this.currentAccountId,
                requestId: requestId,
                grant: action === 'grant',
                mute: action === 'mute'
            };

            if (this.connection) {
                await this.connection.invoke("RespondToScriptPermission", request);
                
                // Mark that we're no longer showing a script permission
                this.isShowingScriptPermission = false;
                this.currentPermissionId = null;
                
                // Immediately hide the modal after sending the response
                const permissionModal = document.getElementById('scriptPermissionModal');
                if (permissionModal) {
                    const modal = bootstrap.Modal.getInstance(permissionModal);
                    if (modal) {
                        modal.hide();
                    }
                }
                
                // Clean up any modal backdrops after a short delay
                setTimeout(() => {
                    this.cleanupModalBackdrops();
                }, 300);
            }
        } catch (error) {
            console.error("Error responding to script permission:", error);
            this.showAlert("Error responding to script permission", "danger");
        }
    }

    // Teleport Request Methods
    handleTeleportRequestReceived(request) {
        console.log("Teleport request received:", request);
        
        // Only show requests for the current active account
        if (request.accountId !== this.currentAccountId) {
            return;
        }
        
        // Check if this request is already in the queue to prevent duplicates
        const existingRequest = this.teleportRequestQueue.find(r => r.requestId === request.requestId);
        if (existingRequest) {
            console.log(`Teleport request ${request.requestId} already in queue, ignoring duplicate`);
            return;
        }
        
        // Add request to queue
        this.teleportRequestQueue.push(request);
        console.log("Teleport request added to queue. Queue length:", this.teleportRequestQueue.length);
        
        // Process queue if not already showing a request
        this.processTeleportRequestQueue();
    }

    handleTeleportRequestClosed(accountId, requestId) {
        console.log(`Teleport request closed: ${requestId}, current displayed: ${this.currentTeleportRequestId}`);
        
        // Remove any matching request from the queue to prevent it from showing again
        const initialQueueLength = this.teleportRequestQueue.length;
        this.teleportRequestQueue = this.teleportRequestQueue.filter(request => request.requestId !== requestId);
        const removedCount = initialQueueLength - this.teleportRequestQueue.length;
        
        if (removedCount > 0) {
            console.log(`Removed ${removedCount} teleport request(s) from queue. Queue length now: ${this.teleportRequestQueue.length}`);
        }
        
        // If this is the currently displayed request, hide it and continue processing queue
        if (this.currentTeleportRequestId === requestId) {
            this.isShowingTeleportRequest = false;
            this.currentTeleportRequestId = null;
            
            // Hide the modal if it's open
            const teleportModal = document.getElementById('teleportRequestModal');
            if (teleportModal) {
                const modalInstance = bootstrap.Modal.getInstance(teleportModal);
                if (modalInstance) {
                    modalInstance.hide();
                }
                
                // Process next request in queue after modal is fully hidden
                setTimeout(() => {
                    this.processTeleportRequestQueue();
                }, 300);
            } else {
                // Process next request immediately if modal wasn't open
                this.processTeleportRequestQueue();
            }
        }
    }

    processTeleportRequestQueue() {
        // Don't show new request if one is already being shown or queue is empty
        if (this.isShowingTeleportRequest || this.teleportRequestQueue.length === 0) {
            console.log(`Cannot process teleport request queue: isShowing=${this.isShowingTeleportRequest}, queueLength=${this.teleportRequestQueue.length}, currentRequestId=${this.currentTeleportRequestId}`);
            return;
        }
        
        // Get the next request from the queue
        const request = this.teleportRequestQueue.shift();
        console.log(`Processing teleport request from queue: ${request.requestId}. Remaining in queue: ${this.teleportRequestQueue.length}`);
        
        // Mark that we're showing a request
        this.isShowingTeleportRequest = true;
        this.currentTeleportRequestId = request.requestId;
        
        // Clean up any stray modal backdrops before showing new request
        this.cleanupModalBackdrops();
        
        this.showTeleportRequest(request);
    }

    showTeleportRequest(request) {
        const modal = document.getElementById('teleportRequestModal');
        const fromAgentEl = document.getElementById('teleportFromAgent');
        const messageEl = document.getElementById('teleportMessage');
        const acceptBtn = document.getElementById('teleportAccept');
        const declineBtn = document.getElementById('teleportDecline');

        if (!modal || !fromAgentEl || !messageEl || !acceptBtn || !declineBtn) {
            console.error("Teleport request modal elements not found, creating modal...");
            this.createTeleportRequestModal();
            // Try again after creating modal
            setTimeout(() => this.showTeleportRequest(request), 100);
            return;
        }

        // Set request content
        fromAgentEl.textContent = request.fromAgentName || 'Unknown Avatar';
        messageEl.innerHTML = this.escapeHtml(request.message || 'No message provided').replace(/\n/g, '<br>');

        // Remove existing event listeners by cloning elements
        const newAcceptBtn = acceptBtn.cloneNode(true);
        const newDeclineBtn = declineBtn.cloneNode(true);
        acceptBtn.parentNode.replaceChild(newAcceptBtn, acceptBtn);
        declineBtn.parentNode.replaceChild(newDeclineBtn, declineBtn);

        // Add event listeners
        newAcceptBtn.addEventListener('click', () => {
            this.respondToTeleportRequest(request.requestId, true);
        });

        newDeclineBtn.addEventListener('click', () => {
            this.respondToTeleportRequest(request.requestId, false);
        });

        // Show the modal
        const modalInstance = new bootstrap.Modal(modal, {
            backdrop: 'static',
            keyboard: false
        });

        modalInstance.show();

        // Set focus to accept button by default
        modal.addEventListener('shown.bs.modal', () => {
            newAcceptBtn.focus();
        }, { once: true });
    }

    createTeleportRequestModal() {
        // Create the teleport request modal if it doesn't exist
        const existingModal = document.getElementById('teleportRequestModal');
        if (existingModal) {
            return; // Modal already exists
        }

        const modalHtml = `
            <div class="modal fade" id="teleportRequestModal" tabindex="-1" aria-labelledby="teleportRequestModalLabel" aria-hidden="true">
                <div class="modal-dialog modal-dialog-centered">
                    <div class="modal-content">
                        <div class="modal-header bg-primary text-white">
                            <h5 class="modal-title" id="teleportRequestModalLabel">
                                <i class="fas fa-rocket me-2"></i>Teleport Offer
                            </h5>
                        </div>
                        <div class="modal-body">
                            <div class="text-center mb-3">
                                <h6 class="fw-bold" id="teleportFromAgent">Loading...</h6>
                                <small class="text-muted">wants to teleport you to their location</small>
                            </div>
                            <div class="alert alert-info" role="alert">
                                <div id="teleportMessage">Loading message...</div>
                            </div>
                        </div>
                        <div class="modal-footer">
                            <button type="button" class="btn btn-success" id="teleportAccept">
                                <i class="fas fa-check me-2"></i>Teleport
                            </button>
                            <button type="button" class="btn btn-secondary" id="teleportDecline">
                                <i class="fas fa-times me-2"></i>Cancel
                            </button>
                        </div>
                    </div>
                </div>
            </div>
        `;

        // Add the modal to the page
        document.body.insertAdjacentHTML('beforeend', modalHtml);
        console.log("Teleport request modal created");
    }

    async respondToTeleportRequest(requestId, accept) {
        try {
            console.log(`Responding to teleport request ${requestId}: ${accept ? 'Accept' : 'Decline'}`);
            
            if (!this.currentAccountId) {
                console.warn("Cannot respond to teleport request - no active account");
                this.showAlert("Cannot respond to teleport request - no active account", "warning");
                return;
            }
            
            if (this.connection) {
                await this.connection.invoke("RespondToTeleportRequest", {
                    accountId: this.currentAccountId,
                    requestId: requestId,
                    accept: accept
                });
                
                console.log(`Teleport request response sent: ${accept ? 'Accepted' : 'Declined'}`);
                
                // Hide the modal
                const modal = document.getElementById('teleportRequestModal');
                if (modal) {
                    const modalInstance = bootstrap.Modal.getInstance(modal);
                    if (modalInstance) {
                        modalInstance.hide();
                    }
                }
                
                // Clean up any modal backdrops after a short delay
                setTimeout(() => {
                    this.cleanupModalBackdrops();
                }, 300);
            }
        } catch (error) {
            console.error("Error responding to teleport request:", error);
            this.showAlert("Error responding to teleport request", "danger");
        }
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // Load recent notices for an account when it connects
    async loadAccountNotices(accountId) {
        try {
            if (this.connection) {
                await this.connection.invoke("GetRecentNotices", accountId, 50);
                await this.connection.invoke("GetUnreadNoticesCount", accountId);
            }
        } catch (err) {
            console.error("Error loading account notices:", err);
        }
    }

    // Helper method to debug which account is currently active on the server
    async debugActiveAccount() {
        if (!this.currentAccountId) {
            console.log("No account currently selected in UI");
            return;
        }

        try {
            const response = await window.authManager.makeAuthenticatedRequest(`/api/accounts/${this.currentAccountId}/presence`);
            if (response.ok) {
                const result = await response.json();
                console.log(`Current UI account: ${this.currentAccountId}`);
                console.log(`Server response for account presence:`, result);
            } else {
                console.error("Failed to get account presence status");
            }
        } catch (error) {
            console.error("Error checking account presence:", error);
        }
    }

    // Debug method to check SignalR connection state and account group membership
    async debugConnectionState() {
        console.log("=== DEBUG CONNECTION STATE ===");
        console.log(`Current account ID: ${this.currentAccountId}`);
        console.log(`Is switching accounts: ${this.isSwitchingAccounts}`);
        console.log(`SignalR connection state: ${this.connection ? this.connection.state : 'No connection'}`);
        console.log(`Nearby avatars count: ${this.nearbyAvatars.length}`);
        console.log(`Avatar refresh interval active: ${this.avatarRefreshInterval ? 'Yes' : 'No'}`);
        
        if (this.connection && this.connection.state === 'Connected') {
            try {
                await this.connection.invoke("DebugConnectionState");
                console.log("Server-side connection debug info logged");
            } catch (error) {
                console.error("Failed to get server-side connection debug info:", error);
            }
        }
        
        console.log("=== END DEBUG INFO ===");
    }

    // Debug method specifically for radar sync issues
    async debugRadarSync() {
        console.log("=== DEBUG RADAR SYNC ===");
        console.log(`Current account ID: ${this.currentAccountId}`);
        console.log(`Nearby avatars count: ${this.nearbyAvatars.length}`);
        console.log(`Is switching accounts: ${this.isSwitchingAccounts}`);
        
        if (this.nearbyAvatars.length > 0) {
            console.log("Current nearby avatars:");
            this.nearbyAvatars.forEach((avatar, index) => {
                console.log(`  ${index + 1}. ${avatar.name || avatar.Name} (${avatar.id || avatar.Id}) - Account: ${avatar.accountId || avatar.AccountId}`);
            });
        } else {
            console.log("No avatars in local nearbyAvatars array");
        }
        
        if (this.connection && this.connection.state === 'Connected' && this.currentAccountId) {
            try {
                console.log("Requesting server-side radar debug...");
                await this.connection.invoke("DebugRadarSync", this.currentAccountId);
                console.log("Server-side radar debug completed - avatars should have been broadcast to client");
                console.log("Check console for 'Updating nearby avatars:' messages");
            } catch (error) {
                console.error("Failed to get server-side radar debug info:", error);
            }
        } else {
            console.warn("Cannot debug radar: connection not ready or no account selected");
        }
        
        console.log("=== END RADAR DEBUG ===");
    }

    // Debug method to manually request nearby avatars
    async debugForceAvatarUpdate() {
        if (this.connection && this.connection.state === 'Connected' && this.currentAccountId) {
            try {
                console.log("Manually requesting nearby avatars...");
                await this.connection.invoke("GetNearbyAvatars", this.currentAccountId);
                console.log("Manual avatar request sent");
            } catch (error) {
                console.error("Failed to manually request avatars:", error);
            }
        } else {
            console.warn("Cannot request avatars: connection not ready or no account selected");
        }
    }
}

// Initialize the client when the page loads
let radegastClient;
document.addEventListener('DOMContentLoaded', () => {
    radegastClient = new RadegastWebClient();
    // Make it globally available for other components
    window.radegastClient = radegastClient;
    
    // Expose debug methods globally for console debugging
    window.debugAccountSwitching = (accountId) => radegastClient.debugAccountSwitching(accountId);
    window.forceRefreshAvatarEvents = (accountId) => radegastClient.forceRefreshAvatarEvents(accountId);
    window.refreshNearbyAvatars = () => radegastClient.refreshNearbyAvatars();
    
    console.log('RadegastWebClient initialized and made globally available');
    console.log('Debug methods available: window.debugAccountSwitching(accountId), window.forceRefreshAvatarEvents(accountId), window.refreshNearbyAvatars()');
});