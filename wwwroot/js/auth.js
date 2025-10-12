// Authentication utilities
class AuthManager {
    constructor() {
        this.isAuthenticated = false;
        this.checkAuthOnLoad();
    }

    async checkAuthOnLoad() {
        try {
            const response = await fetch('/api/auth/verify', {
                method: 'GET',
                credentials: 'include'
            });

            if (!response.ok) {
                this.redirectToLogin();
                return;
            }

            this.isAuthenticated = true;
            this.setupLogoutHandler();
        } catch (error) {
            console.error('Auth check failed:', error);
            this.redirectToLogin();
        }
    }

    redirectToLogin() {
        window.location.href = '/login.html';
    }

    setupLogoutHandler() {
        // Add logout button to header if it doesn't exist
        const header = document.querySelector('header .col-auto');
        if (header && !document.getElementById('logoutBtn')) {
            const logoutBtn = document.createElement('button');
            logoutBtn.id = 'logoutBtn';
            logoutBtn.className = 'btn btn-outline-light ms-2';
            logoutBtn.innerHTML = '<i class="fas fa-sign-out-alt me-1"></i>Logout';
            logoutBtn.title = 'Logout';
            logoutBtn.onclick = () => this.logout();
            header.appendChild(logoutBtn);
        }
    }

    async logout() {
        try {
            await fetch('/api/auth/logout', {
                method: 'POST',
                credentials: 'include'
            });
        } catch (error) {
            console.error('Logout error:', error);
        } finally {
            this.redirectToLogin();
        }
    }

    async makeAuthenticatedRequest(url, options = {}) {
        const defaultOptions = {
            credentials: 'include',
            headers: {
                'Content-Type': 'application/json',
                ...options.headers
            }
        };

        const response = await fetch(url, { ...defaultOptions, ...options });

        if (response.status === 401) {
            this.redirectToLogin();
            throw new Error('Authentication required');
        }

        return response;
    }
}

// Initialize auth manager
window.authManager = new AuthManager();