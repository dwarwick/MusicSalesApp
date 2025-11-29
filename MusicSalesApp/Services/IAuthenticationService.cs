using System.Security.Claims;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for handling user authentication operations.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Logs out the current user.
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Gets the current authenticated user's claims principal.
    /// </summary>
    /// <returns>The current user's ClaimsPrincipal.</returns>
    Task<ClaimsPrincipal> GetCurrentUserAsync();

    /// <summary>
    /// Checks if the current user is authenticated.
    /// </summary>
    /// <returns>True if authenticated, false otherwise.</returns>
    Task<bool> IsAuthenticatedAsync();

    /// <summary>
    /// Registers a new user with the specified email and password.
    /// </summary>
    /// <param name="email">The user's email address.</param>
    /// <param name="password">The user's password.</param>
    /// <returns>A tuple indicating success and an error message if applicable.</returns>
    Task<(bool Success, string Error)> RegisterAsync(string email, string password);

    /// <summary>
    /// Sends a verification email to the specified address.
    /// </summary>
    /// <param name="email">The email address to send verification to.</param>
    /// <param name="baseUrl">The base URL for constructing the verification link.</param>
    /// <returns>A tuple indicating success and an error message if applicable.</returns>
    Task<(bool Success, string Error)> SendVerificationEmailAsync(string email, string baseUrl);

    /// <summary>
    /// Verifies the user's email address using the provided token.
    /// </summary>
    /// <param name="userId">The ID of the user to verify.</param>
    /// <param name="token">The verification token sent to the user's email.</param>
    /// <returns>A tuple indicating success and an error message if applicable.</returns>
    Task<(bool Success, string Error)> VerifyEmailAsync(string userId, string token);

    /// <summary>
    /// Updates the user's email address and sends a new verification email.
    /// Only allowed for users who haven't verified their email yet.
    /// </summary>
    /// <param name="currentEmail">The user's current email address.</param>
    /// <param name="newEmail">The new email address to update to.</param>
    /// <param name="baseUrl">The base URL for constructing the verification link.</param>
    /// <returns>A tuple indicating success and an error message if applicable.</returns>
    Task<(bool Success, string Error)> UpdateEmailAsync(string currentEmail, string newEmail, string baseUrl);

    /// <summary>
    /// Checks if a verification email can be resent (based on cooldown period).
    /// </summary>
    /// <param name="email">The user's email address.</param>
    /// <returns>A tuple indicating if resend is allowed and seconds remaining until allowed.</returns>
    Task<(bool CanResend, int SecondsRemaining)> CanResendVerificationEmailAsync(string email);

    /// <summary>
    /// Checks if the user's email address has been verified.
    /// </summary>
    /// <param name="email">The user's email address.</param>
    /// <returns>True if the email is verified, false otherwise.</returns>
    Task<bool> IsEmailVerifiedAsync(string email);
}
