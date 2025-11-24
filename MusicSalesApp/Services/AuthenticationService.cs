using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(
        AuthenticationStateProvider authenticationStateProvider,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AuthenticationService> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            // Try to find user by email first, then by username
            var user = await _userManager.FindByEmailAsync(username) 
                       ?? await _userManager.FindByNameAsync(username);

            if (user == null)
            {
                _logger.LogWarning("Login failed: User not found for username: {Username}", username);
                return false;
            }

            // Validate password and sign in with lockout protection
            var result = await _signInManager.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true);

            if (result.Succeeded)
            {
                _logger.LogInformation("User {Username} logged in successfully", username);
                NotifyAuthenticationStateChange();
                return true;
            }

            if (result.IsLockedOut)
            {
                _logger.LogWarning("Login failed for user {Username}: Account is locked out", username);
                return false;
            }
            _logger.LogWarning("Login failed for user {Username}: Invalid password", username);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login for user: {Username}", username);
            return false;
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
            // Still notify to clear local state
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
