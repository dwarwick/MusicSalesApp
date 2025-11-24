using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using System.Security.Claims;

namespace MusicSalesApp.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AuthController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new { message = "Invalid username or password" });
        }

        // Validate credentials
        // This is a simplified authentication for demonstration purposes
        // In production, validate against a database with hashed passwords
        if (!ValidateCredentials(request.Username, request.Password))
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        // Create claims for the user
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, request.Username),
            new Claim(ClaimTypes.Role, GetUserRole(request.Username))
        };

        // Add permissions based on role
        var permissions = GetUserPermissions(request.Username);
        foreach (var permission in permissions)
        {
            claims.Add(new Claim(CustomClaimTypes.Permission, permission));
        }

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var claimsPrincipal = new ClaimsPrincipal(claimsIdentity);

        // Get expiration time from configuration
        var expireMinutes = _configuration.GetValue<int>("Auth:ExpireMinutes", 300);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(expireMinutes)
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            claimsPrincipal,
            authProperties);

        return Ok(new { message = "Login successful" });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { message = "Logout successful" });
    }

    private bool ValidateCredentials(string username, string password)
    {
        // For demonstration purposes, accept any non-empty password
        // In production, validate against a database with hashed passwords
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        // Accept admin user or any other username for demo purposes
        return true;
    }

    private string GetUserRole(string username)
    {
        // Assign Admin role to admin user, User role to others
        return username.Equals("admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";
    }

    private List<string> GetUserPermissions(string username)
    {
        var permissions = new List<string>();

        // Admin users get all permissions
        if (username.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            permissions.Add(Permissions.ManageUsers);
        }

        return permissions;
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
