using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MusicSalesApp.Services;

public class ServerAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ServerAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        
        if (httpContext?.User?.Identity?.IsAuthenticated == true)
        {
            return Task.FromResult(new AuthenticationState(httpContext.User));
        }

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        return Task.FromResult(new AuthenticationState(anonymous));
    }

    public void NotifyAuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
