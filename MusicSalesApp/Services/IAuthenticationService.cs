using System.Security.Claims;

namespace MusicSalesApp.Services;

public interface IAuthenticationService
{    
    Task LogoutAsync();
    Task<ClaimsPrincipal> GetCurrentUserAsync();
    Task<bool> IsAuthenticatedAsync();
    Task<(bool Success, string Error)> RegisterAsync(string email, string password);
    Task<(bool Success, string Error)> SendVerificationEmailAsync(string email, string baseUrl);
    Task<(bool Success, string Error)> VerifyEmailAsync(string userId, string token);
    Task<(bool Success, string Error)> UpdateEmailAsync(string currentEmail, string newEmail, string baseUrl);
    Task<(bool CanResend, int SecondsRemaining)> CanResendVerificationEmailAsync(string email);
    Task<bool> IsEmailVerifiedAsync(string email);
}
