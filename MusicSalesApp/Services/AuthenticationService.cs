using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using System.Security.Claims;
using System.Web;

namespace MusicSalesApp.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly IEmailService _emailService;
    private readonly ILogger<AuthenticationService> _logger;
    private const int VerificationEmailCooldownMinutes = 10;

    public AuthenticationService(
        AuthenticationStateProvider authenticationStateProvider,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<IdentityRole<int>> roleManager,
        IEmailService emailService,
        ILogger<AuthenticationService> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
        _emailService = emailService;
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

    public async Task<(bool Success, string Error)> SendVerificationEmailAsync(string email, string baseUrl)
    {
        try
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return (false, "User not found");
            }

            if (user.EmailConfirmed)
            {
                return (false, "Email is already verified");
            }

            // Check cooldown period
            if (user.LastVerificationEmailSent.HasValue)
            {
                var timeSinceLastEmail = DateTime.UtcNow - user.LastVerificationEmailSent.Value;
                if (timeSinceLastEmail.TotalMinutes < VerificationEmailCooldownMinutes)
                {
                    var remainingSeconds = (int)(VerificationEmailCooldownMinutes * 60 - timeSinceLastEmail.TotalSeconds);
                    return (false, $"Please wait {remainingSeconds / 60} minutes and {remainingSeconds % 60} seconds before requesting another verification email");
                }
            }

            // Generate email confirmation token (expires based on Identity token provider settings)
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            
            // URL-encode the token to handle special characters
            var encodedToken = HttpUtility.UrlEncode(token);
            var verificationUrl = $"{baseUrl.TrimEnd('/')}/verify-email?userId={user.Id}&token={encodedToken}";

            // Send verification email
            var emailSent = _emailService.SendEmailVerificationMessage(email, verificationUrl);
            if (!emailSent)
            {
                return (false, "Failed to send verification email. Please try again later.");
            }

            // Update last verification email sent timestamp
            user.LastVerificationEmailSent = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);

            _logger.LogInformation("Verification email sent to {Email}", email);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending verification email to {Email}", email);
            return (false, "Unexpected error sending verification email");
        }
    }

    public async Task<(bool Success, string Error)> VerifyEmailAsync(string userId, string token)
    {
        try
        {
            if (!int.TryParse(userId, out var id))
            {
                return (false, "Invalid user ID");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return (false, "User not found");
            }

            if (user.EmailConfirmed)
            {
                return (true, "Email is already verified");
            }

            // URL-decode the token
            var decodedToken = HttpUtility.UrlDecode(token);
            
            var result = await _userManager.ConfirmEmailAsync(user, decodedToken);
            if (!result.Succeeded)
            {
                var errors = string.Join(";", result.Errors.Select(e => e.Description));
                _logger.LogWarning("Email verification failed for user {UserId}: {Errors}", userId, errors);
                return (false, "Email verification failed. The link may have expired. Please request a new verification email.");
            }

            // Remove from NonValidatedUser role and add to User role
            if (await _userManager.IsInRoleAsync(user, Roles.NonValidatedUser))
            {
                await _userManager.RemoveFromRoleAsync(user, Roles.NonValidatedUser);
            }

            // Ensure User role exists
            if (!await _roleManager.RoleExistsAsync(Roles.User))
            {
                await _roleManager.CreateAsync(new IdentityRole<int> { Name = Roles.User, NormalizedName = Roles.User.ToUpper() });
            }

            if (!await _userManager.IsInRoleAsync(user, Roles.User))
            {
                await _userManager.AddToRoleAsync(user, Roles.User);
            }

            _logger.LogInformation("Email verified for user {UserId}", userId);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying email for user {UserId}", userId);
            return (false, "Unexpected error verifying email");
        }
    }

    public async Task<(bool Success, string Error)> UpdateEmailAsync(string currentEmail, string newEmail, string baseUrl)
    {
        try
        {
            var user = await _userManager.FindByEmailAsync(currentEmail);
            if (user == null)
            {
                return (false, "User not found");
            }

            if (user.EmailConfirmed)
            {
                return (false, "Cannot change email after verification. Please create a new account.");
            }

            // Check if new email is already taken
            var existingUser = await _userManager.FindByEmailAsync(newEmail);
            if (existingUser != null && existingUser.Id != user.Id)
            {
                return (false, "Email already registered");
            }

            // Update email and username
            user.Email = newEmail;
            user.NormalizedEmail = newEmail.ToUpperInvariant();
            user.UserName = newEmail;
            user.NormalizedUserName = newEmail.ToUpperInvariant();
            user.LastVerificationEmailSent = null; // Reset cooldown
            
            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                var errors = string.Join(";", updateResult.Errors.Select(e => e.Description));
                return (false, errors);
            }

            // Send verification email to new address
            var (emailSent, emailError) = await SendVerificationEmailAsync(newEmail, baseUrl);
            if (!emailSent)
            {
                return (false, $"Email updated but failed to send verification: {emailError}");
            }

            _logger.LogInformation("Email updated from {OldEmail} to {NewEmail}", currentEmail, newEmail);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating email from {CurrentEmail} to {NewEmail}", currentEmail, newEmail);
            return (false, "Unexpected error updating email");
        }
    }

    public async Task<(bool CanResend, int SecondsRemaining)> CanResendVerificationEmailAsync(string email)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user == null || user.EmailConfirmed)
        {
            return (false, 0);
        }

        if (!user.LastVerificationEmailSent.HasValue)
        {
            return (true, 0);
        }

        var timeSinceLastEmail = DateTime.UtcNow - user.LastVerificationEmailSent.Value;
        if (timeSinceLastEmail.TotalMinutes >= VerificationEmailCooldownMinutes)
        {
            return (true, 0);
        }

        var remainingSeconds = (int)(VerificationEmailCooldownMinutes * 60 - timeSinceLastEmail.TotalSeconds);
        return (false, remainingSeconds);
    }

    public async Task<bool> IsEmailVerifiedAsync(string email)
    {
        var user = await _userManager.FindByEmailAsync(email);
        return user?.EmailConfirmed ?? false;
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
