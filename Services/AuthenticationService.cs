using Microsoft.Extensions.Options;
using RadegastWeb.Models;
using System.Security.Cryptography;
using System.Text;

namespace RadegastWeb.Services
{
    public interface IAuthenticationService
    {
        bool ValidateCredentials(string username, string password);
        string GenerateSessionToken();
        bool ValidateSessionToken(string token);
        void InvalidateSessionToken(string token);
        bool IsSessionValid(string token);
        bool ValidateHttpContext(HttpContext context);
    }

    public class AuthenticationService : IAuthenticationService
    {
        private readonly AuthenticationConfig _config;
        private readonly ILogger<AuthenticationService> _logger;
        private readonly Dictionary<string, DateTime> _activeSessions = new();
        private readonly object _sessionLock = new();

        public AuthenticationService(IOptions<AuthenticationConfig> config, ILogger<AuthenticationService> logger)
        {
            _config = config.Value;
            _logger = logger;
        }

        public bool ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("Login attempt with empty username or password");
                return false;
            }

            var isValid = string.Equals(username, _config.Username, StringComparison.Ordinal) &&
                         string.Equals(password, _config.Password, StringComparison.Ordinal);

            if (isValid)
            {
                _logger.LogInformation("Successful login for user: {Username}", username);
            }
            else
            {
                _logger.LogWarning("Failed login attempt for user: {Username}", username);
            }

            return isValid;
        }

        public string GenerateSessionToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            
            var token = Convert.ToBase64String(bytes);
            var expirationTime = DateTime.UtcNow.AddMinutes(_config.SessionTimeoutMinutes);

            lock (_sessionLock)
            {
                _activeSessions[token] = expirationTime;
                
                // Clean up expired sessions
                var expiredTokens = _activeSessions
                    .Where(kvp => kvp.Value < DateTime.UtcNow)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var expiredToken in expiredTokens)
                {
                    _activeSessions.Remove(expiredToken);
                }
            }

            _logger.LogInformation("Generated new session token, expires at: {ExpirationTime}", expirationTime);
            return token;
        }

        public bool ValidateSessionToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            lock (_sessionLock)
            {
                if (_activeSessions.TryGetValue(token, out var expiration))
                {
                    if (expiration > DateTime.UtcNow)
                    {
                        // Extend session on activity
                        _activeSessions[token] = DateTime.UtcNow.AddMinutes(_config.SessionTimeoutMinutes);
                        return true;
                    }
                    else
                    {
                        // Token expired, remove it
                        _activeSessions.Remove(token);
                        _logger.LogInformation("Session token expired and removed");
                    }
                }
            }

            return false;
        }

        public bool IsSessionValid(string token)
        {
            return ValidateSessionToken(token);
        }

        public void InvalidateSessionToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;

            lock (_sessionLock)
            {
                if (_activeSessions.Remove(token))
                {
                    _logger.LogInformation("Session token invalidated");
                }
            }
        }

        public bool ValidateHttpContext(HttpContext context)
        {
            var token = context.Request.Cookies["auth_token"] ?? 
                       context.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
            
            return !string.IsNullOrWhiteSpace(token) && ValidateSessionToken(token);
        }
    }
}