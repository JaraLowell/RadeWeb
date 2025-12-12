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
        this.lastAvatarsHash = null; // Track changes in nearby avatars
        this.lastAvatarPositions = new Map(); // Cache of avatar positions by ID
        
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
                    <div class="minimap-canvas-container" style="position: relative; width: 258px; height: 258px; margin: 0 auto; border: 1px solid #4a73a9; background: #4a73a9;">
                        <canvas width="258" height="258" style="width: 100%; height: 100%; display: block;"></canvas>
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
        
        // Get canvas references and ensure context is available
        this.canvas = this.container.querySelector('canvas');
        if (this.canvas) {
            this.ctx = this.canvas.getContext('2d');
        }
    }

    bindEvents() {
        // Add click handler for canvas to show coordinates
        if (this.canvas) {
            this.canvas.addEventListener('click', (e) => {
                const rect = this.canvas.getBoundingClientRect();
                const x = ((e.clientX - rect.left) / rect.width) * 256;
                const y = ((e.clientY - rect.top) / rect.height) * 256;
            });
        }
    }

    async show(accountId) {
        // Check if we're switching accounts
        if (this.currentAccountId !== accountId) {
            this.currentAccountId = accountId;
            this.lastAvatarsHash = null; // Reset avatar tracking for new account
            this.lastAvatarPositions.clear();
        }
        
        this.isVisible = true;
        
        const container = this.container.querySelector('.minimap-container');
        if (container) {
            container.style.display = 'block';
        }

        // Load initial map data
        await this.loadRegionMap();

        // Note: Updates are now triggered via main client's avatar events
        // No need for periodic timer as we get real-time updates
    }

    hide() {
        this.isVisible = false;
        this.currentAccountId = null;
        this.lastAvatarsHash = null; // Clear avatar tracking
        this.lastAvatarPositions.clear();
        
        const container = this.container.querySelector('.minimap-container');
        if (container) {
            container.style.display = 'none';
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
                // Fetch directly from the public URL - browser handles caching
                // Different URLs for different regions means browser naturally fetches new images
                console.log('Loading map image from:', mapInfo.mapImageUrl);
                await this.loadMapImage(mapInfo.mapImageUrl);
            } else {
                console.warn('No map image available for region:', this.regionName);
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
            // Directly load the public Second Life map URL
            // Browser handles caching automatically via HTTP headers
            return new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => {
                    this.mapImage = img;
                    this.drawMap();
                    resolve();
                };
                img.onerror = (error) => {
                    console.error('Failed to load map image:', error);
                    this.showPlaceholder();
                    reject(new Error('Failed to load map image from ' + imageUrl));
                };
                // Load directly without crossOrigin - SL maps don't support CORS
                // This is fine since we're just displaying the image
                img.src = imageUrl;
            });
        } catch (error) {
            console.error('Error loading map image:', error);
            this.showPlaceholder();
        }
    }

    // Method called when the main client is ready
    onClientReady() {
        // If we're visible and have been waiting to draw, try now
        if (this.isVisible) {
            this.safeRedraw();
        }
    }

    // Check if nearby avatars have actually changed
    hasAvatarsChanged() {
        if (!window.radegastClient || !window.radegastClient.nearbyAvatars) {
            return false;
        }

        const nearbyAvatars = window.radegastClient.nearbyAvatars;
        
        // Filter to only current account's avatars
        const currentAccountAvatars = nearbyAvatars.filter(avatar => 
            avatar.accountId === this.currentAccountId
        );

        // Create a hash of the current avatar positions
        const currentHash = currentAccountAvatars
            .map(avatar => `${avatar.id}:${avatar.position?.x || 0},${avatar.position?.y || 0}`)
            .sort()
            .join('|');

        // Check if hash has changed
        if (this.lastAvatarsHash === currentHash) {
            return false; // No changes
        }

        // Update the hash and position cache
        this.lastAvatarsHash = currentHash;
        this.lastAvatarPositions.clear();
        currentAccountAvatars.forEach(avatar => {
            if (avatar.position) {
                this.lastAvatarPositions.set(avatar.id, {
                    x: avatar.position.x,
                    y: avatar.position.y,
                    z: avatar.position.z,
                    name: avatar.name
                });
            }
        });
        return true;
    }

    // Method to safely redraw the map, ensuring all components are ready
    safeRedraw() {
        // Check if the minimap is visible and canvas is ready
        if (!this.isVisible) {
            return;
        }

        // Ensure canvas context is available
        if (!this.ctx && this.canvas) {
            this.ctx = this.canvas.getContext('2d');
        }

        if (!this.ctx || !this.canvas) {
            return;
        }

        // Only redraw if avatars have actually changed
        if (!this.hasAvatarsChanged()) {
            return;
        }

        // Use a small delay to ensure any pending updates are complete
        requestAnimationFrame(() => {
            this.drawMap();
        });
    }

    drawMap() {
        if (!this.ctx || !this.canvas) {
            return;
        }

        // Clear canvas (now 258x258)
        this.ctx.clearRect(0, 0, 258, 258);

        if (this.mapImage) {
            // Draw the map image with 1px padding on all sides
            this.ctx.drawImage(this.mapImage, 1, 1, 256, 256);
            
            // Hide placeholder
            const placeholder = this.container.querySelector('.minimap-placeholder');
            if (placeholder) {
                placeholder.style.display = 'none';
            }
        } else {
            // Show placeholder if no image
            this.showPlaceholder();
        }

        // Draw nearby avatars first (so they appear behind our avatar)
        this.drawNearbyAvatars();

        // Draw avatar position (our red dot on top)
        this.drawAvatarPosition();
    }

    drawAvatarPosition() {
        if (!this.ctx) return;

        const avatarDot = this.container.querySelector('.avatar-dot');
        if (avatarDot) {
            // Position the avatar dot overlay (accounting for 1px padding)
            const x = ((this.avatarPosition.x / 256) * (256/258) * 100) + (1/258 * 100);
            const y = (((256 - this.avatarPosition.y) / 256) * (256/258) * 100) + (1/258 * 100); // Flip Y coordinate
            
            avatarDot.style.left = x + '%';
            avatarDot.style.top = y + '%';
            avatarDot.style.display = 'block';
        }
    }

    drawNearbyAvatars() {
        // Check if we have the required components
        if (!this.ctx) {
            return;
        }
        
        if (!window.radegastClient) {
            return;
        }
        
        if (!window.radegastClient.isInitialized) {
            return;
        }

        // Use cached positions from our change detection
        if (this.lastAvatarPositions.size === 0) {
            return;
        }

        // Draw yellow dots for each cached avatar
        this.ctx.save();
        this.ctx.fillStyle = '#FFD700'; // Gold/yellow color
        this.ctx.strokeStyle = '#FFFFFF'; // White border
        this.ctx.lineWidth = 1;

        this.lastAvatarPositions.forEach((position, avatarId) => {
            // Create local copies for bounds checking without affecting original position
            let adjustedX = position.x;
            let adjustedY = position.y;
            
            // Apply bounds checking
            if (adjustedX <= 0) adjustedX = 0.5;
            if (adjustedY <= 0) adjustedY = 0.5;
            if (adjustedX >= 256) adjustedX = 255.5;
            if (adjustedY >= 256) adjustedY = 255.5;
            // Convert avatar position to canvas coordinates (with 1px padding)
            const canvasX = ((adjustedX / 256) * 256) + 1;
            const canvasY = (((256 - adjustedY) / 256) * 256) + 1; // Flip Y coordinate
            
            // Ensure the dot is within canvas bounds (now 258x258)
            if (canvasX >= 0 && canvasX <= 258 && canvasY >= 0 && canvasY <= 258) {
                // Draw yellow dot with white border
                this.ctx.beginPath();
                this.ctx.arc(canvasX, canvasY, 2, 0, 2 * Math.PI);
                this.ctx.fill();
                this.ctx.stroke();
            }
        });

        this.ctx.restore();
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
            // Region changed - reload with new public URL
            // Browser will automatically fetch new image for different region coordinates
            console.log(`Minimap: Region changed from "${this.regionName}" to "${regionInfo.name}" - reloading map`);
            this.regionName = regionInfo.name;
            this.mapImage = null; // Clear cached image reference
            
            // Reload immediately - new URL means browser fetches new image
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

    // Method to force a redraw (ignoring change detection)
    forceRedraw() {
        if (!this.isVisible || !this.ctx || !this.canvas) {
            return;
        }

        this.lastAvatarsHash = null; // Reset to force detection of changes
        
        // Use a small delay to ensure any pending updates are complete
        requestAnimationFrame(() => {
            this.drawMap();
        });
    }

    // Method to refresh the map (called on teleport/login)
    async refresh() {
        if (this.isVisible) {
            await this.loadRegionMap();
            // Force a redraw to update avatar positions
            this.forceRedraw();
        }
    }
}

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', function() {
    // Initialize the mini map - it will be placed in the existing region info area
    const regionInfoContainer = document.getElementById('regionInfo');
    if (regionInfoContainer) {
        window.miniMap = new MiniMap('regionInfo');
        // Check if the main client is already available and initialized
        if (window.radegastClient && window.radegastClient.isInitialized) {
            window.miniMap.onClientReady();
        }
    }
});

// Export for module usage
if (typeof module !== 'undefined' && module.exports) {
    module.exports = MiniMap;
}