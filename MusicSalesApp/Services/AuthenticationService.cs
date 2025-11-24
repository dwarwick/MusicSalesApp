using System.Security.Claims;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace MusicSalesApp.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IJSRuntime _jsRuntime;

    public AuthenticationService(
        AuthenticationStateProvider authenticationStateProvider,
        IJSRuntime jsRuntime)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _jsRuntime = jsRuntime;
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
                if (_authenticationStateProvider is ServerAuthenticationStateProvider serverAuthStateProvider)
                {
                    serverAuthStateProvider.NotifyAuthenticationStateChanged();
                }
            }

            return result;
        }
        catch
        {
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            // Call the API endpoint via JavaScript fetch
            await _jsRuntime.InvokeVoidAsync("logoutUser");

            // Notify the authentication state provider
            if (_authenticationStateProvider is ServerAuthenticationStateProvider serverAuthStateProvider)
            {
                serverAuthStateProvider.NotifyAuthenticationStateChanged();
            }
        }
        catch
        {
            // Ignore errors
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
