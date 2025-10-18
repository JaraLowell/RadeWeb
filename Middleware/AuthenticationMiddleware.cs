using RadegastWeb.Services;

namespace RadegastWeb.Middleware
{
    public class AuthenticationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<AuthenticationMiddleware> _logger;
        private readonly string[] _publicPaths = {
            "/api/auth/login",
            "/api/auth/verify",
            "/api/stats/",
            "/login.html",
            "/stats.html",
            "/css/",
            "/js/",
            "/img/",
            "/favicon.ico"
        };

        public AuthenticationMiddleware(RequestDelegate next, ILogger<AuthenticationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IAuthenticationService authService)
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
            
            // Allow public paths
            if (_publicPaths.Any(p => path.StartsWith(p.ToLowerInvariant())))
            {
                await _next(context);
                return;
            }

            // Check for authentication token
            var token = context.Request.Cookies["auth_token"] ?? 
                       context.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

            if (string.IsNullOrWhiteSpace(token) || !authService.ValidateSessionToken(token))
            {
                // For API calls, return 401
                if (path.StartsWith("/api/"))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("{\"success\": false, \"message\": \"Authentication required\"}");
                    return;
                }

                // For web pages, redirect to login
                context.Response.Redirect("/login.html");
                return;
            }

            await _next(context);
        }
    }

    public static class AuthenticationMiddlewareExtensions
    {
        public static IApplicationBuilder UseCustomAuthentication(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<AuthenticationMiddleware>();
        }
    }
}