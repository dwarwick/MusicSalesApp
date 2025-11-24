using System.Security.Claims;

namespace MusicSalesApp.Services;

public interface IAuthenticationService
{
    Task<bool> LoginAsync(string username, string password);
    Task LogoutAsync();
    Task<ClaimsPrincipal> GetCurrentUserAsync();
    Task<bool> IsAuthenticatedAsync();
}
