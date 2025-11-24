using System.Security.Claims;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;

namespace MusicSalesApp.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(
        AuthenticationStateProvider authenticationStateProvider,
        IJSRuntime jsRuntime,
        ILogger<AuthenticationService> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _jsRuntime = jsRuntime;
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
            // Call the API endpoint via JavaScript fetch to ensure cookies are set properly
            var result = await _jsRuntime.InvokeAsync<bool>("loginUser", username, password);
            
            if (result)
            {
                // Notify the authentication state provider
                NotifyAuthenticationStateChange();
            }

            return result;
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
            // Call the API endpoint via JavaScript fetch and wait for completion
            await _jsRuntime.InvokeVoidAsync("logoutUser");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during logout, user may already be logged out");
        }
        finally
        {
            // Always notify authentication state change, even if logout API fails
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
