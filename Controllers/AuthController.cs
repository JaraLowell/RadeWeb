using Microsoft.AspNetCore.Mvc;
using RadegastWeb.Models;
using RadegastWeb.Services;

namespace RadegastWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthenticationService _authService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(IAuthenticationService authService, ILogger<AuthController> logger)
        {
            _authService = authService;
            _logger = logger;
        }

        [HttpPost("login")]
        public IActionResult Login([FromBody] WebLoginRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new WebLoginResponse 
                { 
                    Success = false, 
                    Message = "Username and password are required" 
                });
            }

            if (_authService.ValidateCredentials(request.Username, request.Password))
            {
                var token = _authService.GenerateSessionToken();
                
                // Set secure HTTP-only cookie
                var cookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Expires = DateTime.UtcNow.AddHours(1)
                };
                
                Response.Cookies.Append("auth_token", token, cookieOptions);
                
                return Ok(new WebLoginResponse 
                { 
                    Success = true, 
                    Message = "Login successful",
                    Token = token 
                });
            }

            return Unauthorized(new WebLoginResponse 
            { 
                Success = false, 
                Message = "Invalid username or password" 
            });
        }

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            var token = Request.Cookies["auth_token"] ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
            
            if (!string.IsNullOrWhiteSpace(token))
            {
                _authService.InvalidateSessionToken(token);
            }

            Response.Cookies.Delete("auth_token");
            
            return Ok(new { Success = true, Message = "Logged out successfully" });
        }

        [HttpGet("verify")]
        public IActionResult VerifySession()
        {
            var token = Request.Cookies["auth_token"] ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
            
            if (string.IsNullOrWhiteSpace(token) || !_authService.ValidateSessionToken(token))
            {
                return Unauthorized(new { Success = false, Message = "Invalid or expired session" });
            }

            return Ok(new { Success = true, Message = "Session is valid" });
        }
    }
}