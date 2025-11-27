using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using System.Security.Claims;

namespace MusicSalesApp.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager; // added role manager
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(
        AuthenticationStateProvider authenticationStateProvider,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<IdentityRole<int>> roleManager,        
        ILogger<AuthenticationService> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;        
        _logger = logger;
    }
    
    public async Task<(bool Success, string Error)> RegisterAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return (false, "Email and password are required");
        }
        try
        {
            var existing = await _userManager.FindByEmailAsync(email);
            if (existing != null)
            {
                return (false, "Email already registered");
            }

            // Ensure NonValidatedUser role exists
            if (!await _roleManager.RoleExistsAsync(Roles.NonValidatedUser))
            {
                var createRole = await _roleManager.CreateAsync(new IdentityRole<int> { Name = Roles.NonValidatedUser, NormalizedName = Roles.NonValidatedUser.ToUpper() });
                if (!createRole.Succeeded)
                {
                    _logger.LogWarning("Failed creating NonValidatedUser role: {Errors}", string.Join(',', createRole.Errors.Select(e => e.Description)));
                    return (false, "Unable to create role");
                }
            }

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = false
            };
            var createResult = await _userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
            {
                var errors = string.Join(";", createResult.Errors.Select(e => e.Description));
                return (false, errors);
            }

            // Assign NonValidatedUser role
            var addRoleResult = await _userManager.AddToRoleAsync(user, Roles.NonValidatedUser);
            if (!addRoleResult.Succeeded)
            {
                var errors = string.Join(";", addRoleResult.Errors.Select(e => e.Description));
                return (false, errors);
            }

            // Add permission claim NonValidatedUser to the role if missing
            var role = await _roleManager.FindByNameAsync(Roles.NonValidatedUser);
            if (role != null)
            {
                var claims = await _roleManager.GetClaimsAsync(role);
                if (!claims.Any(c => c.Type == CustomClaimTypes.Permission && c.Value == Permissions.NonValidatedUser))
                {
                    var rc = await _roleManager.AddClaimAsync(role, new Claim(CustomClaimTypes.Permission, Permissions.NonValidatedUser));
                    if (!rc.Succeeded)
                    {
                        _logger.LogWarning("Failed adding permission claim to role NonValidatedUser");
                    }
                }
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering user {Email}", email);
            return (false, "Unexpected error creating account");
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            await _signInManager.SignOutAsync();
            _logger.LogInformation("User logged out successfully");
            NotifyAuthenticationStateChange();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during logout");
            NotifyAuthenticationStateChange();
        }
    }

    private void NotifyAuthenticationStateChange()
    {
        if (_authenticationStateProvider is ServerAuthenticationStateProvider serverAuthStateProvider)
        {
            serverAuthStateProvider.NotifyAuthenticationStateChanged();
        }
    }

    public async Task<ClaimsPrincipal> GetCurrentUserAsync()
    {
        var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        return authState.User;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var user = await GetCurrentUserAsync();
        return user?.Identity?.IsAuthenticated ?? false;
    }
}
