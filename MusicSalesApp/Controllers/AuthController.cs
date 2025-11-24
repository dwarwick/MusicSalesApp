using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using System.Security.Claims;

namespace MusicSalesApp.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AuthController(
        IConfiguration configuration,
        AppDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _configuration = configuration;
        _dbContext = dbContext;
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new { message = "Invalid username or password" });
        }

        // Find user by email/username (consolidated query)
        var user = await _userManager.FindByEmailAsync(request.Username) 
            ?? await _userManager.FindByNameAsync(request.Username);

        if (user == null)
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        // Validate password using UserManager
        var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        // Get user roles
        var roles = await _userManager.GetRolesAsync(user);

        // Create claims for the user
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim(ClaimTypes.Email, user.Email)
        };

        // Add role claims
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        // Add permissions based on roles
        foreach (var role in roles)
        {
            var permissions = GetPermissionsForRole(role);
            foreach (var permission in permissions)
            {
                claims.Add(new Claim(CustomClaimTypes.Permission, permission));
            }
        }

        var claimsIdentity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme);
        var claimsPrincipal = new ClaimsPrincipal(claimsIdentity);

        // Get expiration time from configuration
        var expireMinutes = _configuration.GetValue<int>("Auth:ExpireMinutes", 300);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(expireMinutes)
        };

        await HttpContext.SignInAsync(
            IdentityConstants.ApplicationScheme,
            claimsPrincipal,
            authProperties);

        return Ok(new { message = "Login successful" });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Ok(new { message = "Logout successful" });
    }

    private List<string> GetPermissionsForRole(string role)
    {
        var permissions = new List<string>();

        if (role.Equals(Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            permissions.Add(Permissions.ManageUsers);
            permissions.Add(Permissions.ValidatedUser);
        }
        else if (role.Equals(Roles.User, StringComparison.OrdinalIgnoreCase))
        {
            permissions.Add(Permissions.ValidatedUser);
        }

        return permissions;
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
