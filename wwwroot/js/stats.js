/**
 * RadegastWeb Visitor Statistics Dashboard
 */

class StatsManager {
    constructor() {
        this.charts = {};
        this.currentPeriod = 30;
        this.currentRegion = '';
        this.currentHourlyPeriod = 7;
        this.lastUpdateTime = null;
        
        this.initializeEventListeners();
        this.loadStatistics();
        
        // Auto-refresh every 5 minutes
        setInterval(() => this.loadStatistics(), 5 * 60 * 1000);
    }

    initializeEventListeners() {
        // Time period selector
        document.querySelectorAll('input[name="timePeriod"]').forEach(radio => {
            radio.addEventListener('change', (e) => {
                this.currentPeriod = parseInt(e.target.value);
                this.loadStatistics();
            });
        });

        // Hourly period selector
        document.querySelectorAll('input[name="hourlyPeriod"]').forEach(radio => {
            radio.addEventListener('change', (e) => {
                this.currentHourlyPeriod = parseInt(e.target.value);
                this.loadHourlyActivity();
            });
        });

        // Region filter
        document.getElementById('regionFilter').addEventListener('change', (e) => {
            this.currentRegion = e.target.value;
            this.loadStatistics();
        });
    }

    async loadStatistics() {
        try {
            this.showLoading();
            
            // Prepare parameters for both dashboard and visitor stats
            const params = new URLSearchParams({
                days: this.currentPeriod.toString()
            });
            if (this.currentRegion) {
                params.append('region', this.currentRegion);
            }
            
            // Load dashboard summary with the same filters
            const dashboardData = await this.fetchAPI(`/api/stats/dashboard?${params}`);
            this.updateDashboard(dashboardData);

            // Load visitor statistics
            const visitorStats = await this.fetchAPI(`/api/stats/visitors?${params}`);
            this.updateCharts(visitorStats);

            // Load unique visitors
            const uniqueVisitors = await this.fetchAPI(`/api/stats/visitors/unique?${params}`);
            this.updateRecentVisitors(uniqueVisitors);

            // Load monitored regions for filter
            const regions = await this.fetchAPI('/api/stats/regions/monitored');
            this.updateRegionFilter(regions);

            // Load hourly activity
            await this.loadHourlyActivity();

            this.updateLastUpdated();
            this.showContent();
        } catch (error) {
            console.error('Error loading statistics:', error);
            this.showError(error.message);
        }
    }

    async fetchAPI(url) {
        const response = await fetch(url);
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}: ${response.statusText}`);
        }
        return await response.json();
    }

    updateDashboard(data) {
        // Use the correct property names from the backend, with fallbacks for both naming conventions
        const todayValue = data.TotalVisitorsToday || data.totalVisitorsToday || 0;
        
        this.animateNumber('visitorsToday', todayValue);
        this.animateNumber('visitors7Days', data.TotalUniqueVisitors7Days || data.totalUniqueVisitors7Days || 0);
        this.animateNumber('visitors30Days', data.TotalUniqueVisitors30Days || data.totalUniqueVisitors30Days || 0);
        this.animateNumber('monitoredRegions', data.MonitoredRegionsCount || data.monitoredRegionsCount || 0);

        // Update region stats table
        this.updateRegionStats(data.RegionStats || data.regionStats || []);
    }

    updateCharts(visitorStats) {
        if (!Array.isArray(visitorStats)) {
            console.warn('Visitor statistics data is not an array:', visitorStats);
            return;
        }
        
        if (visitorStats.length === 0) {
            console.warn('No visitor statistics data available');
            return;
        }

        this.updateDailyVisitorsChart(visitorStats);
        this.updateRegionDistributionChart(visitorStats);
    }

    updateDailyVisitorsChart(visitorStats) {
        const ctx = document.getElementById('dailyVisitorsChart').getContext('2d');
        
        // Destroy existing chart
        if (this.charts.dailyVisitors) {
            this.charts.dailyVisitors.destroy();
        }

        // Check if we have valid data
        if (!Array.isArray(visitorStats) || visitorStats.length === 0) {
            console.warn('No visitor statistics data available for chart');
            return;
        }

        // Prepare data - combine all regions' daily stats including true unique visitors
        const dateMap = new Map();
        
        visitorStats.forEach(regionData => {
            // Check if DailyStats exists and is an array
            const dailyStats = regionData.DailyStats || regionData.dailyStats || [];
            if (!Array.isArray(dailyStats)) {
                console.warn('Invalid dailyStats for region:', regionData);
                return;
            }
            
            dailyStats.forEach(dayData => {
                const date = (dayData.Date || dayData.date || '').split('T')[0]; // Get date part only
                if (!date) return;
                
                if (!dateMap.has(date)) {
                    dateMap.set(date, { visitors: 0, trueUnique: 0, visits: 0, regions: new Set() });
                }
                const existing = dateMap.get(date);
                existing.visitors += dayData.UniqueVisitors || dayData.uniqueVisitors || 0;
                existing.trueUnique += dayData.TrueUniqueVisitors || dayData.trueUniqueVisitors || 0;
                existing.visits += dayData.TotalVisits || dayData.totalVisits || 0;
                existing.regions.add(regionData.RegionName || regionData.regionName || 'Unknown');
            });
        });

        // Sort by date and prepare chart data
        const sortedDates = Array.from(dateMap.keys()).sort();
        const labels = sortedDates.map(date => this.formatDate(date));
        const visitorsData = sortedDates.map(date => dateMap.get(date).visitors);
        const trueUniqueData = sortedDates.map(date => dateMap.get(date).trueUnique);
        const visitsData = sortedDates.map(date => dateMap.get(date).visits);

        this.charts.dailyVisitors = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Total Visitors',
                    data: visitsData,
                    borderColor: 'rgb(74, 115, 169)',
                    backgroundColor: 'rgba(0, 123, 255, 0.1)',
                    tension: 0.4,
                    fill: false
                }, {
                    label: 'Unique Visitors',
                    data: trueUniqueData,
                    borderColor: 'rgb(220, 53, 69)',
                    backgroundColor: 'rgba(220, 53, 69, 0.1)',
                    tension: 0.4,
                    fill: true
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    title: {
                        display: false
                    },
                    legend: {
                        position: 'top'
                    }
                },
                scales: {
                    y: {
                        beginAtZero: true,
                        grid: {
                            color: 'rgba(0, 0, 0, 0.05)'
                        }
                    },
                    x: {
                        grid: {
                            color: 'rgba(0, 0, 0, 0.05)'
                        }
                    }
                },
                interaction: {
                    intersect: false
                }
            }
        });
    }

    updateRegionDistributionChart(visitorStats) {
        const ctx = document.getElementById('regionDistributionChart').getContext('2d');
        
        // Destroy existing chart
        if (this.charts.regionDistribution) {
            this.charts.regionDistribution.destroy();
        }

        // Check if we have valid data
        if (!Array.isArray(visitorStats) || visitorStats.length === 0) {
            console.warn('No visitor statistics data available for region distribution chart');
            return;
        }

        // Prepare data for top 10 regions
        const regionData = visitorStats
            .map(stat => ({
                region: stat.RegionName || stat.regionName || 'Unknown',
                visitors: stat.TotalUniqueVisitors || stat.totalUniqueVisitors || 0
            }))
            .filter(item => item.visitors > 0)
            .sort((a, b) => b.visitors - a.visitors)
            .slice(0, 10);

        if (regionData.length === 0) {
            return;
        }

        const colors = [
            '#4a73a9', '#1E7E34', '#ffc107', '#dc3545', '#17a2b8',
            '#6f42c1', '#e83e8c', '#fd7e14', '#20c997', '#6c757d'
        ];

        this.charts.regionDistribution = new Chart(ctx, {
            type: 'doughnut',
            data: {
                labels: regionData.map(item => item.region),
                datasets: [{
                    data: regionData.map(item => item.visitors),
                    backgroundColor: colors.slice(0, regionData.length),
                    borderWidth: 2,
                    borderColor: '#fff'
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        position: 'bottom',
                        labels: {
                            padding: 15,
                            usePointStyle: true
                        }
                    },
                    tooltip: {
                        callbacks: {
                            label: function(context) {
                                const total = context.dataset.data.reduce((a, b) => a + b, 0);
                                const percentage = ((context.parsed / total) * 100).toFixed(1);
                                return `${context.label}: ${context.parsed} visitors (${percentage}%)`;
                            }
                        }
                    }
                }
            }
        });
    }

    updateRecentVisitors(visitors) {
        const tbody = document.getElementById('recentVisitorsList');
        
        if (!Array.isArray(visitors) || visitors.length === 0) {
            tbody.innerHTML = '<tr><td colspan="4" class="text-center text-muted">No recent visitors</td></tr>';
            return;
        }

        // Sort by last seen (most recent first) and take top 20
        const recentVisitors = visitors
            .sort((a, b) => new Date(b.LastSeen || b.lastSeen || 0) - new Date(a.LastSeen || a.lastSeen || 0))
            .slice(0, 20);

        tbody.innerHTML = recentVisitors.map(visitor => {
            // Use the best available name from our enhanced cache data
            // Priority: displayName > avatarName > fallback to truncated avatar ID
            let displayName = this.getBestAvailableName(visitor);
            
            const firstSeen = this.formatDateTime(visitor.FirstSeen || visitor.firstSeen);
            const lastSeen = this.formatDateTime(visitor.LastSeen || visitor.lastSeen);
            
            // Add visual indicator for true unique visitors (new in 60 days)
            const isTrueUnique = visitor.IsTrueUnique || visitor.isTrueUnique || false;
            const uniqueBadge = isTrueUnique ? 
                '<small class="badge bg-success ms-1" title="New visitor (not seen in past 60 days)">NEW</small>' : 
                '<small class="badge bg-secondary ms-1" title="Returning visitor (seen in past 60 days)">RET</small>';
            
            const avatarId = visitor.AvatarId || visitor.avatarId || 'unknown';
            const regionsVisited = visitor.RegionsVisited || visitor.regionsVisited || [];
            const visitCount = visitor.VisitCount || visitor.visitCount || 0;
            
            return `
                <tr>
                    <td>
                        <div class="visitor-display-name" title="${avatarId}">
                            ${this.escapeHtml(displayName)}${uniqueBadge}
                        </div>
                        ${regionsVisited.length > 1 ? 
                            `<small class="text-muted">${regionsVisited.length} regions</small>` : 
                            `<small class="text-muted">${regionsVisited[0] || ''}</small>`
                        }
                    </td>
                    <td><small>${firstSeen}</small></td>
                    <td><small>${lastSeen}</small></td>
                    <td><span class="badge bg-primary">${visitCount}</span></td>
                </tr>
            `;
        }).join('');
    }

    /**
     * Gets the best available name for a visitor, prioritizing good names over placeholders
     */
    getBestAvailableName(visitor) {
        // Check for valid display name first (handle both property name cases)
        const displayName = visitor.DisplayName || visitor.displayName;
        if (this.isValidName(displayName)) {
            return displayName;
        }
        
        // Check for valid avatar/legacy name (handle both property name cases)
        const avatarName = visitor.AvatarName || visitor.avatarName;
        if (this.isValidName(avatarName)) {
            return avatarName;
        }
        
        // Fallback to truncated avatar ID if we have it (handle both property name cases)
        const avatarId = visitor.AvatarId || visitor.avatarId;
        if (avatarId) {
            return `Avatar ${avatarId.substring(0, 8)}...`;
        }
        
        // Last resort
        return 'Unknown User';
    }

    /**
     * Checks if a name is valid (not null, empty, or a placeholder)
     */
    isValidName(name) {
        if (!name || typeof name !== 'string' || name.trim() === '') {
            return false;
        }
        
        const lowerName = name.toLowerCase().trim();
        const invalidNames = [
            'loading...',
            'unknown user', 
            'unknown',
            '???',
            'loading',
            ''
        ];
        
        return !invalidNames.includes(lowerName) && !lowerName.startsWith('loading');
    }

    updateRegionStats(regionStats) {
        const tbody = document.getElementById('regionStatsList');
        
        if (!Array.isArray(regionStats) || regionStats.length === 0) {
            tbody.innerHTML = '<tr><td colspan="4" class="text-center text-muted">No region data</td></tr>';
            return;
        }

        tbody.innerHTML = regionStats.map(region => {
            const regionName = region.RegionName || region.regionName || 'Unknown';
            const totalUniqueVisitors = region.TotalUniqueVisitors || region.totalUniqueVisitors || 0;
            const totalVisits = region.TotalVisits || region.totalVisits || 0;
            const avgVisitorsPerDay = region.AverageVisitorsPerDay || region.averageVisitorsPerDay || 0;
            
            return `
                <tr>
                    <td>
                        <span class="region-name">${this.escapeHtml(regionName)}</span>
                        <span class="status-indicator status-monitoring" title="Currently monitoring"></span>
                    </td>
                    <td><strong>${totalUniqueVisitors}</strong></td>
                    <td>${totalVisits}</td>
                    <td><small>${avgVisitorsPerDay.toFixed(1)}</small></td>
                </tr>
            `;
        }).join('');
    }

    updateRegionFilter(regions) {
        const select = document.getElementById('regionFilter');
        const currentValue = select.value;
        
        // Clear existing options except "All Regions"
        select.innerHTML = '<option value="">All Regions</option>';
        
        // Add region options
        regions.forEach(region => {
            const option = document.createElement('option');
            option.value = region;
            option.textContent = region;
            select.appendChild(option);
        });
        
        // Restore previous selection if still valid
        if (currentValue && regions.includes(currentValue)) {
            select.value = currentValue;
        }
    }

    animateNumber(elementId, newValue) {
        const element = document.getElementById(elementId);
        const currentValue = parseInt(element.textContent) || 0;
        
        if (currentValue === newValue) return;
        
        element.classList.add('number-update');
        element.textContent = newValue.toLocaleString();
        
        setTimeout(() => element.classList.remove('number-update'), 500);
    }

    updateLastUpdated() {
        const now = new Date();
        this.lastUpdateTime = now;
        document.getElementById('lastUpdated').textContent = this.formatTime(now);
    }

    formatDate(dateString) {
        return new Date(dateString).toLocaleDateString('en-US', {
            month: 'short',
            day: 'numeric'
        });
    }

    formatDateTime(dateString) {
        return new Date(dateString).toLocaleDateString('en-US', {
            month: 'short',
            day: 'numeric',
            hour: '2-digit',
            minute: '2-digit'
        });
    }

    formatTime(date) {
        return date.toLocaleTimeString('en-US', {
            hour: '2-digit',
            minute: '2-digit'
        });
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    showLoading() {
        document.getElementById('loadingSpinner').classList.remove('d-none');
        document.getElementById('statsContent').classList.add('d-none');
        document.getElementById('errorState').classList.add('d-none');
    }

    showContent() {
        document.getElementById('loadingSpinner').classList.add('d-none');
        document.getElementById('statsContent').classList.remove('d-none');
        document.getElementById('errorState').classList.add('d-none');
    }

    async loadHourlyActivity() {
        try {
            const params = new URLSearchParams({
                days: this.currentHourlyPeriod.toString()
            });
            if (this.currentRegion) {
                params.append('region', this.currentRegion);
            }
            
            const hourlyData = await this.fetchAPI(`/api/stats/hourly?${params}`);
            console.log('Hourly data received:', hourlyData);
            this.updateHourlyChart(hourlyData);
        } catch (error) {
            console.error('Error loading hourly activity:', error);
        }
    }

    updateHourlyChart(hourlyData) {
        const ctx = document.getElementById('hourlyActivityChart').getContext('2d');
        
        // Destroy existing chart
        if (this.charts.hourlyActivity) {
            this.charts.hourlyActivity.destroy();
        }

        // Handle both PascalCase and camelCase property names
        const hourlyStats = hourlyData.HourlyStats || hourlyData.hourlyStats || [];
        
        if (!hourlyData || hourlyStats.length === 0) {
            console.warn('No hourly activity data available', hourlyData);
            return;
        }

        // Update summary info (handle both naming conventions)
        const daysAnalyzed = hourlyData.DaysAnalyzed || hourlyData.daysAnalyzed || this.currentHourlyPeriod;
        const peakHourLabel = hourlyData.PeakHourLabel || hourlyData.peakHourLabel || '-';
        const peakHourAverage = hourlyData.PeakHourAverage || hourlyData.peakHourAverage || 0;
        const quietHourLabel = hourlyData.QuietHourLabel || hourlyData.quietHourLabel || '-';
        const quietHourAverage = hourlyData.QuietHourAverage || hourlyData.quietHourAverage || 0;
        
        document.getElementById('hourlyDaysLabel').textContent = daysAnalyzed;
        document.getElementById('peakHourLabel').textContent = peakHourLabel;
        document.getElementById('peakHourAvg').textContent = peakHourAverage.toFixed(1);
        document.getElementById('quietHourLabel').textContent = quietHourLabel;
        document.getElementById('quietHourAvg').textContent = quietHourAverage.toFixed(1);
        document.getElementById('daysAnalyzed').textContent = daysAnalyzed;

        // Prepare chart data (handle both naming conventions)
        const labels = hourlyStats.map(h => (h.HourLabel || h.hourLabel) || `${(h.Hour || h.hour || 0)}:00`);
        const averageData = hourlyStats.map(h => (h.AverageVisitors || h.averageVisitors) || 0);
        const totalData = hourlyStats.map(h => (h.UniqueVisitors || h.uniqueVisitors) || 0);

        this.charts.hourlyActivity = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Average Visitors per Hour',
                    data: averageData,
                    backgroundColor: 'rgba(74, 115, 169, 0.7)',
                    borderColor: 'rgb(74, 115, 169)',
                    borderWidth: 1,
                    yAxisID: 'y'
                }, {
                    label: 'Total Unique Visitors',
                    data: totalData,
                    type: 'line',
                    borderColor: 'rgb(220, 53, 69)',
                    backgroundColor: 'rgba(220, 53, 69, 0.1)',
                    borderWidth: 2,
                    fill: false,
                    tension: 0.4,
                    yAxisID: 'y1'
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    title: {
                        display: false
                    },
                    legend: {
                        position: 'top'
                    },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
                        callbacks: {
                            afterLabel: function(context) {
                                const hourData = hourlyStats[context.dataIndex];
                                const totalVisits = (hourData.TotalVisits || hourData.totalVisits) || 0;
                                return `Total Visits: ${totalVisits}`;
                            }
                        }
                    }
                },
                interaction: {
                    mode: 'nearest',
                    axis: 'x',
                    intersect: false
                },
                scales: {
                    x: {
                        title: {
                            display: true,
                            text: 'Time (SLT)'
                        },
                        grid: {
                            color: 'rgba(0, 0, 0, 0.05)'
                        }
                    },
                    y: {
                        type: 'linear',
                        display: true,
                        position: 'left',
                        title: {
                            display: true,
                            text: 'Average Visitors'
                        },
                        grid: {
                            color: 'rgba(0, 0, 0, 0.05)'
                        },
                        beginAtZero: true
                    },
                    y1: {
                        type: 'linear',
                        display: true,
                        position: 'right',
                        title: {
                            display: true,
                            text: 'Total Unique Visitors'
                        },
                        grid: {
                            drawOnChartArea: false,
                        },
                        beginAtZero: true
                    }
                }
            }
        });
    }

    showError(message) {
        document.getElementById('loadingSpinner').classList.add('d-none');
        document.getElementById('statsContent').classList.add('d-none');
        document.getElementById('errorState').classList.remove('d-none');
        document.getElementById('errorMessage').textContent = message;
    }
}

// Initialize when DOM is loaded
document.addEventListener('DOMContentLoaded', () => {
    window.statsManager = new StatsManager();
});

// Global function for retry button
function loadStatistics() {
    if (window.statsManager) {
        window.statsManager.loadStatistics();
    }
}