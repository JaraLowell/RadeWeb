# RadegastWeb Authentication Setup

This document explains how to configure and use the authentication system in RadegastWeb.

## Configuration

Authentication credentials are configured in `appsettings.json`:

```json
{
  "Authentication": {
    "Username": "admin",
    "Password": "password123",
    "SessionTimeoutMinutes": 60
  }
}
```

### Configuration Options

- **Username**: The username required to access RadegastWeb
- **Password**: The password required to access RadegastWeb  
- **SessionTimeoutMinutes**: How long a session remains active (default: 60 minutes)

## Usage

1. **First Time Setup**: 
   - Edit `appsettings.json` to set your desired username and password
   - Build and run the application

2. **Accessing RadegastWeb**:
   - Navigate to your RadegastWeb URL (e.g., `http://localhost:5269`)
   - You'll be redirected to the login page
   - Enter your configured username and password
   - Upon successful login, you'll be redirected to the main interface

3. **Session Management**:
   - Sessions automatically extend on activity
   - Sessions expire after the configured timeout period
   - You can manually logout using the logout button in the header
   - If your session expires, you'll be redirected to the login page

## Security Features

- **HTTP-Only Cookies**: Session tokens are stored in secure HTTP-only cookies
- **Session Validation**: All API endpoints require valid authentication
- **Automatic Cleanup**: Expired sessions are automatically cleaned up
- **CSRF Protection**: Uses same-site cookie policy
- **Secure Transport**: HTTPS enforcement in production

## Default Credentials

For development/testing purposes, the default credentials are:
- **Username**: `admin`
- **Password**: `password123`

**Important**: Change these credentials before deploying to production!

## Troubleshooting

- **Can't Login**: Check that the username and password in `appsettings.json` match what you're entering
- **Session Expired**: Your session may have timed out, simply login again
- **Browser Issues**: Clear cookies and try again if experiencing login issues

## Development Notes

- Authentication is bypassed for static assets (CSS, JS, images)
- The `/api/auth/*` endpoints are publicly accessible for login/logout operations
- All other endpoints require authentication
- SignalR connections also require valid authentication