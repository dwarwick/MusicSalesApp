using System.Security.Claims;

namespace MusicSalesApp.Services;

public interface IAuthenticationService
{    
    Task LogoutAsync();
    Task<ClaimsPrincipal> GetCurrentUserAsync();
    Task<bool> IsAuthenticatedAsync();
    Task<(bool Success, string Error)> RegisterAsync(string email, string password); // returns success and error message
}
