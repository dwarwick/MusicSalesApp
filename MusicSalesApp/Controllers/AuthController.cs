using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Extensions;
using MusicSalesApp.Models;
using System.Security.Claims;

namespace MusicSalesApp.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AuthController(
        IConfiguration configuration,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _configuration = configuration;
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

        // Find user by email or username
        var user = await _userManager.FindByEmailOrUsernameAsync(request.Username);

        if (user == null)
        {
            return Unauthorized(new { message = "Invalid username or password" });
        }

        // Check if account is suspended
        if (user.IsSuspended)
        {
            // If user wants to reactivate account, verify password first
            if (request.ReactivateAccount)
            {
                // Verify the password is correct
                var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
                if (!passwordValid)
                {
                    return Unauthorized(new { message = "Invalid username or password" });
                }

                // Reactivate the account
                user.IsSuspended = false;
                user.SuspendedAt = null;
                await _userManager.UpdateAsync(user);
            }
            else
            {
                return Unauthorized(new { message = "Your account has been suspended. Check the 'Reactivate my suspended account' box to restore access." });
            }
        }

        // Validate password and trigger lockout protection
        var signInResult = await _signInManager.PasswordSignInAsync(
            user.UserName,
            request.Password,
            isPersistent: false,
            lockoutOnFailure: true);
        if (!signInResult.Succeeded)
        {
            if (signInResult.IsLockedOut)
            {
                return Unauthorized(new { message = "Account locked due to multiple failed login attempts. Please try again later." });
            }
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
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        // Add permissions based on roles
        var permissions = roles.SelectMany(GetPermissionsForRole);
        claims.AddRange(permissions.Select(permission => new Claim(CustomClaimTypes.Permission, permission)));

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
            // Admin gets all permissions except NonValidatedUser
            var all = typeof(Permissions).GetFields().Select(f => f.GetValue(null)?.ToString()).Where(v => !string.IsNullOrEmpty(v));
            permissions.AddRange(all.Where(p => !string.Equals(p, Permissions.NonValidatedUser, StringComparison.OrdinalIgnoreCase)));
        }
        else if (role.Equals(Roles.User, StringComparison.OrdinalIgnoreCase))
        {
            permissions.Add(Permissions.ValidatedUser);
        }

        return permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool ReactivateAccount { get; set; } = false;
}
