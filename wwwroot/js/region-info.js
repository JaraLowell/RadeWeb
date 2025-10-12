// Region Info Panel - shows detailed simulation statistics similar to Radegast
class RegionInfoPanel {
    constructor(containerId) {
        this.container = document.getElementById(containerId);
        this.currentAccountId = null;
        this.updateInterval = null;
        this.isVisible = false;
        
        this.createUI();
        this.bindEvents();
    }

    createUI() {
        this.container.innerHTML = `
            <div class="region-info-panel" style="display: none;">
                <div class="region-header">
                    <h3>Region Information</h3>
                    <button class="close-btn" aria-label="Close">&times;</button>
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
                </div>
            </div>
        `;
    }

    bindEvents() {
        // Close button
        const closeBtn = this.container.querySelector('.close-btn');
        closeBtn.addEventListener('click', () => this.hide());

        // Subscribe to SignalR events
        if (window.radegastConnection) {
            window.radegastConnection.on('RegionStatsUpdated', (stats) => {
                if (this.isVisible && stats.accountId === this.currentAccountId) {
                    this.updateDisplay(stats);
                }
            });
        }
    }

    async show(accountId) {
        this.currentAccountId = accountId;
        this.isVisible = true;
        
        const panel = this.container.querySelector('.region-info-panel');
        panel.style.display = 'block';

        // Request initial data
        await this.refreshData();

        // Start periodic refresh for REST API fallback (every 5 seconds)
        this.updateInterval = setInterval(() => {
            if (this.isVisible) {
                this.refreshData();
            }
        }, 5000);
    }

    hide() {
        this.isVisible = false;
        const panel = this.container.querySelector('.region-info-panel');
        panel.style.display = 'none';

        if (this.updateInterval) {
            clearInterval(this.updateInterval);
            this.updateInterval = null;
        }
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