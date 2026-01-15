// Region Info Panel - shows detailed simulation statistics similar to Radegast
class RegionInfoPanel {
    constructor(containerId) {
        this.container = document.getElementById(containerId);
        this.currentAccountId = null;
        this.updateInterval = null;
        this.isVisible = false;
        this.signalrSubscribed = false;
        
        // Store SignalR event handler references so we can clean them up
        this.regionStatsHandler = null;
        this.musicUrlHandler = null;
        
        this.createUI();
        this.bindEvents();
    }

    createUI() {
        this.container.innerHTML = `
            <div class="region-info-panel" style="display: none;">
                <div class="region-header">
                    <h3>Region Information</h3>
                    <div class="header-buttons">
                        <button class="btn btn-sm btn-warning restart-region-btn" type="button" title="Restart Region (requires Estate Manager permissions)">
                            <i class="fas fa-sync-alt me-1"></i>Restart Region
                        </button>
                        <button class="close-btn" aria-label="Close">&times;</button>
                    </div>
                </div>
                
                <div class="region-content">
                    <!-- Basic Region Info -->
                    <div class="region-section">
                        <h4>Region Details</h4>
                        <div class="region-grid">
                            <div class="info-item">
                                <label>Name:</label>
                                <span class="region-name">-</span>
                            </div>
                            <div class="info-item">
                                <label>Position:</label>
                                <span class="region-coordinates">-</span>
                            </div>
                            <div class="info-item">
                                <label>My Position:</label>
                                <span class="my-position">-</span>
                            </div>
                            <div class="info-item">
                                <label>Maturity:</label>
                                <span class="maturity-level">-</span>
                            </div>
                            <div class="info-item">
                                <label>Product:</label>
                                <span class="product-name">-</span>
                            </div>
                            <div class="info-item">
                                <label>Version:</label>
                                <span class="sim-version">-</span>
                            </div>
                        </div>
                    </div>

                    <!-- Performance Statistics -->
                    <div class="region-section">
                        <h4>Performance</h4>
                        <div class="performance-summary">
                            <div class="performance-indicator">
                                <div class="performance-bar">
                                    <div class="performance-fill" style="width: 0%"></div>
                                </div>
                                <span class="performance-text">Unknown</span>
                            </div>
                        </div>
                        <div class="region-grid">
                            <div class="info-item">
                                <label>Time Dilation:</label>
                                <span class="time-dilation">-</span>
                            </div>
                            <div class="info-item">
                                <label>FPS:</label>
                                <span class="fps">-</span>
                            </div>
                            <div class="info-item">
                                <label>Spare Time:</label>
                                <span class="spare-time">- ms</span>
                            </div>
                        </div>
                    </div>

                    <!-- Agent & Object Statistics -->
                    <div class="region-section">
                        <h4>Population & Objects</h4>
                        <div class="region-grid">
                            <div class="info-item">
                                <label>Main Agents:</label>
                                <span class="main-agents">-</span>
                            </div>
                            <div class="info-item">
                                <label>Child Agents:</label>
                                <span class="child-agents">-</span>
                            </div>
                            <div class="info-item">
                                <label>Objects:</label>
                                <span class="objects">-</span>
                            </div>
                            <div class="info-item">
                                <label>Active Objects:</label>
                                <span class="active-objects">-</span>
                            </div>
                            <div class="info-item">
                                <label>Active Scripts:</label>
                                <span class="active-scripts">-</span>
                            </div>
                        </div>
                    </div>

                    <!-- Processing Times -->
                    <div class="region-section">
                        <h4>Processing Times</h4>
                        <div class="region-grid">
                            <div class="info-item">
                                <label>Total Frame:</label>
                                <span class="total-frame-time">- ms</span>
                            </div>
                            <div class="info-item">
                                <label>Net Time:</label>
                                <span class="net-time">- ms</span>
                            </div>
                            <div class="info-item">
                                <label>Physics:</label>
                                <span class="physics-time">- ms</span>
                            </div>
                            <div class="info-item">
                                <label>Simulation:</label>
                                <span class="sim-time">- ms</span>
                            </div>
                            <div class="info-item">
                                <label>Agent Time:</label>
                                <span class="agent-time">- ms</span>
                            </div>
                            <div class="info-item">
                                <label>Images:</label>
                                <span class="images-time">- ms</span>
                            </div>
                            <div class="info-item">
                                <label>Scripts:</label>
                                <span class="script-time">- ms</span>
                            </div>
                        </div>
                    </div>

                    <!-- Network Statistics -->
                    <div class="region-section">
                        <h4>Network</h4>
                        <div class="region-grid">
                            <div class="info-item">
                                <label>Pending Downloads:</label>
                                <span class="pending-downloads">-</span>
                            </div>
                            <div class="info-item">
                                <label>Pending Uploads:</label>
                                <span class="pending-uploads">-</span>
                            </div>
                            <div class="info-item">
                                <label>Data Center:</label>
                                <span class="data-center">-</span>
                            </div>
                            <div class="info-item">
                                <label>CPU Class:</label>
                                <span class="cpu-class">-</span>
                            </div>
                        </div>
                    </div>

                    <!-- Media Section -->
                    <div class="region-section">
                        <h4>Parcel Media</h4>
                        <div class="media-controls">
                            <div class="form-group mb-2">
                                <label for="currentMusicUrl" class="form-label small">Current Music URL:</label>
                                <div class="input-group input-group-sm">
                                    <input type="text" class="form-control music-url" id="currentMusicUrl" readonly />
                                    <button class="btn btn-outline-secondary btn-sm refresh-music-btn" type="button" title="Refresh">
                                        <i class="fas fa-sync-alt"></i>
                                    </button>
                                </div>
                            </div>
                            <div class="form-group">
                                <label for="newMusicUrl" class="form-label small">Set New Music URL:</label>
                                <div class="input-group input-group-sm">
                                    <input type="text" class="form-control" id="newMusicUrl" placeholder="Enter music stream URL" />
                                    <button class="btn btn-primary btn-sm set-music-btn" type="button">
                                        <i class="fas fa-music me-1"></i>Set URL
                                    </button>
                                </div>
                                <small class="text-muted">Note: You need appropriate parcel permissions to set the music URL</small>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        `;
    }

    bindEvents() {
        // Close button
        const closeBtn = this.container.querySelector('.close-btn');
        closeBtn.addEventListener('click', () => this.hide());

        // Restart region button
        const restartBtn = this.container.querySelector('.restart-region-btn');
        restartBtn.addEventListener('click', () => this.restartRegion());

        // Music URL refresh button
        const refreshMusicBtn = this.container.querySelector('.refresh-music-btn');
        refreshMusicBtn.addEventListener('click', () => this.refreshMusicUrl());

        // Set music URL button
        const setMusicBtn = this.container.querySelector('.set-music-btn');
        setMusicBtn.addEventListener('click', () => this.setMusicUrl());

        // Enter key in music URL input
        const newMusicUrlInput = this.container.querySelector('#newMusicUrl');
        newMusicUrlInput.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') {
                this.setMusicUrl();
            }
        });

        // Subscribe to SignalR events if connection is ready
        this.subscribeToSignalREvents();
    }

    subscribeToSignalREvents() {
        // Only subscribe once
        if (this.signalrSubscribed) return;
        
        // Wait for connection to be available
        if (!window.radegastConnection) {
            console.log('RegionInfoPanel: SignalR connection not yet available, will retry...');
            return;
        }

        console.log('RegionInfoPanel: Subscribing to SignalR events');
        this.signalrSubscribed = true;

        // Store handler references for cleanup
        this.regionStatsHandler = (stats) => {
            if (this.isVisible && stats.accountId === this.currentAccountId) {
                this.updateDisplay(stats);
            }
        };

        this.musicUrlHandler = (data) => {
            if (this.isVisible && data.accountId === this.currentAccountId) {
                this.updateMusicUrl(data.musicUrl);
            }
        };

        window.radegastConnection.on('RegionStatsUpdated', this.regionStatsHandler);
        window.radegastConnection.on('ParcelMusicUrlUpdated', this.musicUrlHandler);
    }

    unsubscribeFromSignalREvents() {
        if (!this.signalrSubscribed || !window.radegastConnection) return;

        console.log('RegionInfoPanel: Unsubscribing from SignalR events to prevent memory leaks');
        
        // Remove event handlers to prevent memory leaks
        if (this.regionStatsHandler) {
            window.radegastConnection.off('RegionStatsUpdated', this.regionStatsHandler);
            this.regionStatsHandler = null;
        }

        if (this.musicUrlHandler) {
            window.radegastConnection.off('ParcelMusicUrlUpdated', this.musicUrlHandler);
            this.musicUrlHandler = null;
        }

        this.signalrSubscribed = false;
    }

    async show(accountId) {
        this.currentAccountId = accountId;
        this.isVisible = true;
        
        const panel = this.container.querySelector('.region-info-panel');
        panel.style.display = 'block';

        // Ensure SignalR events are subscribed (in case connection wasn't available during initialization)
        this.subscribeToSignalREvents();

        // Request initial data
        await this.refreshData();
        await this.refreshMusicUrl();

        // Start periodic refresh for REST API fallback (every 5 seconds)
        this.updateInterval = setInterval(() => {
            if (this.isVisible) {
                this.refreshData();
                this.refreshMusicUrl();
            }
        }, 5000);
    }

    hide() {
        this.isVisible = false;
        const panel = this.container.querySelector('.region-info-panel');
        panel.style.display = 'none';

        // Clean up interval timer
        if (this.updateInterval) {
            clearInterval(this.updateInterval);
            this.updateInterval = null;
        }

        // MEMORY FIX: Unsubscribe from SignalR events to prevent memory leaks
        // Without this, event handlers accumulate every time the panel is shown
        this.unsubscribeFromSignalREvents();
    }

    cleanup() {
        console.log('RegionInfoPanel: Cleaning up resources');
        this.hide();
        this.unsubscribeFromSignalREvents();
        this.currentAccountId = null;
    }

    async refreshData() {
        if (!this.currentAccountId) return;

        try {
            // Try SignalR first
            if (window.radegastConnection && window.radegastConnection.state === 'Connected') {
                await window.radegastConnection.invoke('GetRegionStats', this.currentAccountId);
            } else {
                // Fallback to REST API
                const response = await window.authManager.makeAuthenticatedRequest(`/api/region/${this.currentAccountId}/stats`);
                if (response.ok) {
                    const stats = await response.json();
                    this.updateDisplay(stats);
                }
            }
        } catch (error) {
            console.error('Error refreshing region stats:', error);
        }
    }

    updateDisplay(stats) {
        // Basic region information
        this.setElementText('.region-name', stats.regionName || '-');
        this.setElementText('.region-coordinates', stats.regionCoordinates || '-');
        this.setElementText('.my-position', stats.myPositionText || '-');
        this.setElementText('.maturity-level', stats.maturityLevel || '-');
        this.setElementText('.product-name', stats.productName || '-');
        this.setElementText('.sim-version', stats.simVersion || '-');

        // Performance metrics
        this.setElementText('.time-dilation', this.formatNumber(stats.timeDilation, 3));
        this.setElementText('.fps', this.formatNumber(stats.fps, 1));
        this.setElementText('.spare-time', this.formatNumber(stats.spareTime, 1) + ' ms');

        // Update performance indicator
        this.updatePerformanceIndicator(stats.performancePercentage, stats.performanceStatus);

        // Population & objects
        this.setElementText('.main-agents', stats.mainAgents || '0');
        this.setElementText('.child-agents', stats.childAgents || '0');
        this.setElementText('.objects', stats.objects || '0');
        this.setElementText('.active-objects', stats.activeObjects || '0');
        this.setElementText('.active-scripts', stats.activeScripts || '0');

        // Processing times
        this.setElementText('.total-frame-time', this.formatNumber(stats.totalFrameTime, 1) + ' ms');
        this.setElementText('.net-time', this.formatNumber(stats.netTime, 1) + ' ms');
        this.setElementText('.physics-time', this.formatNumber(stats.physicsTime, 1) + ' ms');
        this.setElementText('.sim-time', this.formatNumber(stats.simTime, 1) + ' ms');
        this.setElementText('.agent-time', this.formatNumber(stats.agentTime, 1) + ' ms');
        this.setElementText('.images-time', this.formatNumber(stats.imagesTime, 1) + ' ms');
        this.setElementText('.script-time', this.formatNumber(stats.scriptTime, 1) + ' ms');

        // Network statistics
        this.setElementText('.pending-downloads', stats.pendingDownloads || '0');
        this.setElementText('.pending-uploads', (stats.pendingUploads + stats.pendingLocalUploads) || '0');
        this.setElementText('.data-center', stats.dataCenter || '-');
        this.setElementText('.cpu-class', stats.cpuClass || '-');
    }

    updatePerformanceIndicator(percentage, status) {
        const fill = this.container.querySelector('.performance-fill');
        const text = this.container.querySelector('.performance-text');
        
        if (fill && text) {
            percentage = Math.max(0, Math.min(100, percentage || 0));
            fill.style.width = percentage + '%';
            text.textContent = status || 'Unknown';

            // Color coding
            fill.className = 'performance-fill';
            if (percentage >= 80) {
                fill.classList.add('excellent');
            } else if (percentage >= 60) {
                fill.classList.add('good');
            } else if (percentage >= 40) {
                fill.classList.add('fair');
            } else if (percentage >= 20) {
                fill.classList.add('poor');
            } else {
                fill.classList.add('critical');
            }
        }
    }

    setElementText(selector, text) {
        const element = this.container.querySelector(selector);
        if (element) {
            element.textContent = text;
        }
    }

    formatNumber(value, decimals = 0) {
        if (value === null || value === undefined || isNaN(value)) {
            return '-';
        }
        return Number(value).toFixed(decimals);
    }

    async refreshMusicUrl() {
        if (!this.currentAccountId) return;

        try {
            const response = await window.authManager.makeAuthenticatedRequest(`/api/region/${this.currentAccountId}/music`);
            if (response.ok) {
                const data = await response.json();
                this.updateMusicUrl(data.musicUrl);
            } else {
                console.error('Failed to fetch music URL');
            }
        } catch (error) {
            console.error('Error fetching music URL:', error);
        }
    }

    async setMusicUrl() {
        if (!this.currentAccountId) return;

        const newMusicUrlInput = this.container.querySelector('#newMusicUrl');
        const newUrl = newMusicUrlInput.value.trim();

        if (!newUrl) {
            alert('Please enter a music URL');
            return;
        }

        try {
            const response = await window.authManager.makeAuthenticatedRequest(
                `/api/region/${this.currentAccountId}/music`,
                {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ musicUrl: newUrl })
                }
            );

            if (response.ok) {
                const result = await response.json();
                if (result.success) {
                    alert('Music URL set successfully');
                    this.updateMusicUrl(newUrl);
                    newMusicUrlInput.value = '';
                    // Refresh to get the actual current URL from server
                    await this.refreshMusicUrl();
                } else {
                    alert(result.message || 'Failed to set music URL. You may not have permissions.');
                }
            } else {
                const error = await response.json();
                alert(error.error || 'Failed to set music URL');
            }
        } catch (error) {
            console.error('Error setting music URL:', error);
            alert('Error setting music URL: ' + error.message);
        }
    }

    updateMusicUrl(url) {
        const musicUrlInput = this.container.querySelector('#currentMusicUrl');
        if (musicUrlInput) {
            musicUrlInput.value = url || '(no music stream)';
        }
    }

    async restartRegion() {
        if (!this.currentAccountId) return;

        const regionName = this.container.querySelector('.region-name')?.textContent || 'this region';
        
        if (!confirm(`Do you want to restart region ${regionName}?\n\nNote: This requires Estate Manager or higher permissions.`)) {
            return;
        }

        try {
            const response = await window.authManager.makeAuthenticatedRequest(
                `/api/region/${this.currentAccountId}/restart`,
                {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    }
                }
            );

            if (response.ok) {
                const result = await response.json();
                if (result.success) {
                    alert('Region restart request sent successfully. The region will restart shortly.');
                } else {
                    alert(result.message || 'Failed to restart region. You may not have the required permissions.');
                }
            } else {
                const error = await response.json();
                alert(error.error || 'Failed to restart region');
            }
        } catch (error) {
            console.error('Error restarting region:', error);
            alert('Error restarting region: ' + error.message);
        }
    }
}

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', function() {
    // Create region info panel container if it doesn't exist
    if (!document.getElementById('region-info-container')) {
        const container = document.createElement('div');
        container.id = 'region-info-container';
        container.className = 'region-info-container';
        document.body.appendChild(container);
    }

    // Initialize the region info panel
    window.regionInfoPanel = new RegionInfoPanel('region-info-container');
});

// Export for module usage
if (typeof module !== 'undefined' && module.exports) {
    module.exports = RegionInfoPanel;
}