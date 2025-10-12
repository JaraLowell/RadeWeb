// Mini Map Component - displays the region map image similar to Radegast
class MiniMap {
    constructor(containerId) {
        this.container = document.getElementById(containerId);
        this.currentAccountId = null;
        this.currentMapImageUrl = null;
        this.updateInterval = null;
        this.canvas = null;
        this.ctx = null;
        this.mapImage = null;
        this.avatarPosition = { x: 128, y: 128, z: 0 };
        this.regionName = '';
        this.isVisible = false;
        
        this.createUI();
        this.bindEvents();
    }

    createUI() {
        // The minimap will be embedded in the existing region info card
        // We'll add it to the card body
        if (!this.container) return;

        const mapHtml = `
            <div class="minimap-container" style="display: none;">
                <div class="minimap-content">
                    <div class="minimap-canvas-container" style="position: relative; width: 256px; height: 256px; margin: 0 auto; border: 1px solid #4a73a9; background: #4a73a9;">
                        <canvas width="256" height="256" style="width: 100%; height: 100%; display: block;"></canvas>
                        <div class="minimap-overlay">
                            <div class="avatar-dot" style="position: absolute; width: 6px; height: 6px; background: #ff0000; border: 1px solid #fff; border-radius: 50%; transform: translate(-50%, -50%);"></div>
                        </div>
                        <div class="minimap-loading" style="position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); display: none;">
                            <div class="spinner-border spinner-border-sm text-primary" role="status">
                                <span class="visually-hidden">Loading map...</span>
                            </div>
                        </div>
                        <div class="minimap-placeholder" style="position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); text-align: center; color: #6c757d;">
                            <i class="fas fa-map-marker-alt fa-2x mb-2"></i>
                            <p class="small mb-0">No map available</p>
                        </div>
                    </div>
                    <div class="minimap-coordinates mt-2">
                        <small class="text-muted">
                            <span class="region-name-display text-muted">-</span><br>
                            Position: <span class="avatar-coords">-</span> | 
                            Region: <span class="region-coords">-</span>
                        </small>
                    </div>
                </div>
            </div>
        `;

        this.container.innerHTML = mapHtml;
        
        // Get canvas references
        this.canvas = this.container.querySelector('canvas');
        this.ctx = this.canvas ? this.canvas.getContext('2d') : null;
    }

    bindEvents() {
        // Subscribe to SignalR events for real-time updates
        if (window.radegastConnection) {
            window.radegastConnection.on('RegionInfoUpdated', (regionInfo) => {
                if (this.isVisible && regionInfo.accountId === this.currentAccountId) {
                    this.updateMapInfo(regionInfo);
                }
            });

            window.radegastConnection.on('PresenceUpdate', (presence) => {
                if (this.isVisible && presence.accountId === this.currentAccountId) {
                    this.updateAvatarPosition(presence);
                }
            });
        }

        // Add click handler for canvas to show coordinates
        if (this.canvas) {
            this.canvas.addEventListener('click', (e) => {
                const rect = this.canvas.getBoundingClientRect();
                const x = ((e.clientX - rect.left) / rect.width) * 256;
                const y = ((e.clientY - rect.top) / rect.height) * 256;
                console.log(`Clicked at region coordinates: (${x.toFixed(0)}, ${y.toFixed(0)})`);
            });
        }
    }

    async show(accountId) {
        this.currentAccountId = accountId;
        this.isVisible = true;
        
        const container = this.container.querySelector('.minimap-container');
        if (container) {
            container.style.display = 'block';
        }

        // Load initial map data
        await this.loadRegionMap();

        // Start periodic updates for position
        this.updateInterval = setInterval(() => {
            if (this.isVisible) {
                this.updateAvatarPosition();
            }
        }, 2000); // Update every 2 seconds
    }

    hide() {
        this.isVisible = false;
        const container = this.container.querySelector('.minimap-container');
        if (container) {
            container.style.display = 'none';
        }

        if (this.updateInterval) {
            clearInterval(this.updateInterval);
            this.updateInterval = null;
        }
    }

    async loadRegionMap() {
        if (!this.currentAccountId) return;

        try {
            this.showLoading(true);
            
            // Get map info first
            const mapInfoResponse = await window.authManager.makeAuthenticatedRequest(
                `/api/region/${this.currentAccountId}/map/info`
            );
            
            if (!mapInfoResponse.ok) {
                throw new Error('Failed to get map info');
            }

            const mapInfo = await mapInfoResponse.json();
            this.regionName = mapInfo.regionName;
            this.updateRegionDisplay(mapInfo);

            if (mapInfo.hasMapImage && mapInfo.mapImageUrl) {
                // Load the actual map image
                await this.loadMapImage(mapInfo.mapImageUrl);
            } else {
                this.showPlaceholder();
            }
        } catch (error) {
            console.error('Error loading region map:', error);
            this.showPlaceholder();
        } finally {
            this.showLoading(false);
        }
    }

    async loadMapImage(imageUrl) {
        try {
            const response = await window.authManager.makeAuthenticatedRequest(imageUrl);
            if (!response.ok) {
                throw new Error('Failed to load map image');
            }

            const blob = await response.blob();
            const imageObjectUrl = URL.createObjectURL(blob);

            return new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => {
                    this.mapImage = img;
                    this.drawMap();
                    URL.revokeObjectURL(imageObjectUrl);
                    resolve();
                };
                img.onerror = () => {
                    URL.revokeObjectURL(imageObjectUrl);
                    reject(new Error('Failed to decode map image'));
                };
                img.src = imageObjectUrl;
            });
        } catch (error) {
            console.error('Error loading map image:', error);
            this.showPlaceholder();
        }
    }

    drawMap() {
        if (!this.ctx || !this.canvas) return;

        // Clear canvas
        this.ctx.clearRect(0, 0, 256, 256);

        if (this.mapImage) {
            // Draw the map image, scaled to fit the canvas
            this.ctx.drawImage(this.mapImage, 0, 0, 256, 256);
            
            // Hide placeholder
            const placeholder = this.container.querySelector('.minimap-placeholder');
            if (placeholder) {
                placeholder.style.display = 'none';
            }
        } else {
            // Show placeholder if no image
            this.showPlaceholder();
        }

        // Draw avatar position
        this.drawAvatarPosition();
    }

    drawAvatarPosition() {
        if (!this.ctx) return;

        const avatarDot = this.container.querySelector('.avatar-dot');
        if (avatarDot) {
            // Position the avatar dot overlay
            const x = (this.avatarPosition.x / 256) * 100;
            const y = ((256 - this.avatarPosition.y) / 256) * 100; // Flip Y coordinate
            
            avatarDot.style.left = x + '%';
            avatarDot.style.top = y + '%';
            avatarDot.style.display = 'block';
        }
    }

    updateRegionDisplay(mapInfo) {
        // Update region name
        const regionNameDisplay = this.container.querySelector('.region-name-display');
        if (regionNameDisplay) {
            regionNameDisplay.textContent = mapInfo.regionName || '-';
        }

        // Update coordinates
        const regionCoords = this.container.querySelector('.region-coords');
        if (regionCoords) {
            regionCoords.textContent = `(${mapInfo.regionX}, ${mapInfo.regionY})`;
        }

        // Update avatar position if provided
        if (mapInfo.localPosition) {
            this.avatarPosition = {
                x: mapInfo.localPosition.x,
                y: mapInfo.localPosition.y,
                z: mapInfo.localPosition.z
            };
            this.updateAvatarCoordinatesDisplay();
        }
    }

    updateMapInfo(regionInfo) {
        if (regionInfo.name !== this.regionName) {
            // Region changed, reload the map
            this.regionName = regionInfo.name;
            this.loadRegionMap();
        }
    }

    updateAvatarPosition(presence) {
        if (presence && presence.position) {
            this.avatarPosition = {
                x: presence.position.x,
                y: presence.position.y,
                z: presence.position.z
            };
            this.updateAvatarCoordinatesDisplay();
            this.drawAvatarPosition();
        }
    }

    updateAvatarCoordinatesDisplay() {
        const avatarCoords = this.container.querySelector('.avatar-coords');
        if (avatarCoords) {
            avatarCoords.textContent = `(${this.avatarPosition.x.toFixed(0)}, ${this.avatarPosition.y.toFixed(0)}, ${this.avatarPosition.z.toFixed(0)})`;
        }
    }

    showLoading(show) {
        const loading = this.container.querySelector('.minimap-loading');
        if (loading) {
            loading.style.display = show ? 'block' : 'none';
        }
    }

    showPlaceholder() {
        const placeholder = this.container.querySelector('.minimap-placeholder');
        if (placeholder) {
            placeholder.style.display = 'block';
        }
        
        // Hide avatar dot when no map
        const avatarDot = this.container.querySelector('.avatar-dot');
        if (avatarDot) {
            avatarDot.style.display = 'none';
        }
    }

    // Method to refresh the map (called on teleport/login)
    async refresh() {
        if (this.isVisible) {
            await this.loadRegionMap();
        }
    }
}

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', function() {
    // Initialize the mini map - it will be placed in the existing region info area
    const regionInfoContainer = document.getElementById('regionInfo');
    if (regionInfoContainer) {
        window.miniMap = new MiniMap('regionInfo');
    }
});

// Export for module usage
if (typeof module !== 'undefined' && module.exports) {
    module.exports = MiniMap;
}