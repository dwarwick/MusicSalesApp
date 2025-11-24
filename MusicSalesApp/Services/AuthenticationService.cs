using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IConfiguration _configuration;

    public AuthenticationService(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationStateProvider authenticationStateProvider,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _authenticationStateProvider = authenticationStateProvider;
        _configuration = configuration;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        // TODO: In a real application, validate credentials against a database
        // For now, we'll use a simple check for demonstration
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return false;
        }

        // Create claims for the user
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "Admin")
        };

        // Add permissions based on role
        // Admin role gets ManageUsers permission
        if (username.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(CustomClaimTypes.Permission, Permissions.ManageUsers));
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

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            claimsPrincipal,
            authProperties);

        // Notify the authentication state provider
        if (_authenticationStateProvider is ServerAuthenticationStateProvider serverAuthStateProvider)
        {
            serverAuthStateProvider.NotifyAuthenticationStateChanged();
        }

        return true;
    }

    public async Task LogoutAsync()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            // Notify the authentication state provider
            if (_authenticationStateProvider is ServerAuthenticationStateProvider serverAuthStateProvider)
            {
                serverAuthStateProvider.NotifyAuthenticationStateChanged();
            }
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
