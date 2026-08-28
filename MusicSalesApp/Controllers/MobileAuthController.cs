using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Extensions;
using MusicSalesApp.Middleware;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using AppAuthenticationService = MusicSalesApp.Services.IAuthenticationService;

namespace MusicSalesApp.Controllers;

[Route("api/mobile-auth")]
[ApiController]
[RequireMobileApiKey]
public class MobileAuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppAuthenticationService _authService;
    private readonly IMobileExternalAuthTokenService _mobileExternalAuthTokenService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IEmailService _emailService;
    private readonly IAccountEmailService _accountEmailService;
    private readonly IAccountDeletionService _accountDeletionService;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly AppDbContext _context;
    private readonly ILogger<MobileAuthController> _logger;
    private readonly IPayPalSubscriptionManagementService _payPalSubscriptionManagementService;
    private readonly IAppleIdentityTokenValidator _appleIdentityTokenValidator;
    private readonly IAppleTokenRevocationService _appleTokenRevocationService;

    private const int CodeExpirationMinutes = 10;
    private const int CodeLength = 6;
    private const int ResendCooldownMinutes = 2;

    public MobileAuthController(
        IConfiguration configuration,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        AppAuthenticationService authService,
        IMobileExternalAuthTokenService mobileExternalAuthTokenService,
        ISubscriptionService subscriptionService,
        IEmailService emailService,
        IAccountEmailService accountEmailService,
        IAccountDeletionService accountDeletionService,
        IAdminNotificationService adminNotificationService,
        AppDbContext context,
        ILogger<MobileAuthController> logger,
        IAppleIdentityTokenValidator appleIdentityTokenValidator,
        IAppleTokenRevocationService appleTokenRevocationService,
        IPayPalSubscriptionManagementService payPalSubscriptionManagementService = null)
    {
        _configuration = configuration;
        _signInManager = signInManager;
        _userManager = userManager;
        _authService = authService;
        _mobileExternalAuthTokenService = mobileExternalAuthTokenService;
        _subscriptionService = subscriptionService;
        _emailService = emailService;
        _accountEmailService = accountEmailService;
        _accountDeletionService = accountDeletionService;
        _adminNotificationService = adminNotificationService;
        _context = context;
        _logger = logger;
        _appleIdentityTokenValidator = appleIdentityTokenValidator;
        _appleTokenRevocationService = appleTokenRevocationService;
        _payPalSubscriptionManagementService = payPalSubscriptionManagementService;
    }

    [HttpGet("google/start")]
    [SkipMobileApiKey]
    public IActionResult StartGoogleLogin()
    {
        if (string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientId"]) ||
            string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientSecret"]))
        {
            return StatusCode(503, new { message = "Google sign-in is not configured." });
        }

        var redirectUri = Url.ActionLink(nameof(GoogleCallback), values: null, protocol: Request.Scheme);
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return StatusCode(500, new { message = "Unable to start Google sign-in." });
        }

        var properties = _signInManager.ConfigureExternalAuthenticationProperties(
            ExternalLoginProviders.Google,
            redirectUri);

        return Challenge(properties, ExternalLoginProviders.Google);
    }

    [HttpGet("google/callback")]
    [SkipMobileApiKey]
    public async Task<IActionResult> GoogleCallback()
    {
        var callbackUrl = GetMobileExternalAuthCallbackUrl();

        try
        {
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null || !string.Equals(info.LoginProvider, ExternalLoginProviders.Google, StringComparison.Ordinal))
            {
                return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
                {
                    [MobileExternalAuthQueryKeys.Error] = "Google sign-in could not be completed."
                }));
            }

            if (!TryGetGoogleEmail(info.Principal, out var email))
            {
                return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
                {
                    [MobileExternalAuthQueryKeys.Error] = "Google account did not provide a verified email address."
                }));
            }

            var user = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (user == null)
            {
                user = await _userManager.FindByEmailAsync(email);
                if (user != null)
                {
                    if (user.IsSuspended)
                    {
                        return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
                        {
                            [MobileExternalAuthQueryKeys.Error] = "Your account has been suspended."
                        }));
                    }

                    var (linked, linkError) = await EnsureExternalLoginAsync(user, info.LoginProvider, info.ProviderKey);
                    if (!linked)
                    {
                        return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
                        {
                            [MobileExternalAuthQueryKeys.Error] = linkError
                        }));
                    }

                    var (promoted, promoteError) = await _authService.MarkEmailVerifiedAndPromoteRoleAsync(user);
                    if (!promoted)
                    {
                        return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
                        {
                            [MobileExternalAuthQueryKeys.Error] = promoteError
                        }));
                    }
                }
            }

            if (user == null)
            {
                var pendingToken = _mobileExternalAuthTokenService.ProtectPendingRegistration(
                    new MobilePendingExternalRegistrationTokenPayload(
                        info.LoginProvider,
                        info.ProviderKey,
                        email,
                        info.Principal.Identity?.Name ?? string.Empty));

                return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
                {
                    [MobileExternalAuthQueryKeys.RequiresRegistration] = bool.TrueString,
                    [MobileExternalAuthQueryKeys.PendingRegistrationToken] = pendingToken,
                    [MobileExternalAuthQueryKeys.Email] = email
                }));
            }

            if (user.IsSuspended)
            {
                return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
                {
                    [MobileExternalAuthQueryKeys.Error] = "Your account has been suspended."
                }));
            }

            var exchangeToken = _mobileExternalAuthTokenService.ProtectLoginExchange(user.Id);
            return Redirect(BuildMobileExternalAuthCallback(callbackUrl, new Dictionary<string, string>
            {
                [MobileExternalAuthQueryKeys.ExchangeToken] = exchangeToken,
                [MobileExternalAuthQueryKeys.Email] = user.Email ?? string.Empty
            }));
        }
        finally
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        }
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] MobileRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Email and password are required." });

        var (success, error) = await _authService.RegisterAsync(request.Email, request.Password);
        if (!success)
            return BadRequest(new { message = error });

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
            return StatusCode(500, new { message = "Registration succeeded but user not found." });

        // Generate and send 6-digit verification code
        var sendResult = await GenerateAndSendCodeAsync(user, MobileVerificationPurpose.EmailVerification,
            "Verify Your Email", BuildVerificationEmailBody);
        if (!sendResult.Success)
            return StatusCode(500, new { message = sendResult.Error });

        _logger.LogInformation("Mobile registration completed for {Email}, verification code sent", request.Email);

        // Notify admin of new registration
        try
        {
            await _adminNotificationService.NotifyUserRegisteredAsync(request.Email);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send admin registration notification for {Email}", request.Email);
        }

        return Ok(new { userId = user.Id, message = "Registration successful. Check your email for a verification code." });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] MobileLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Email and password are required." });

        var user = await _userManager.FindByEmailOrUsernameAsync(request.Email);
        if (user == null)
            return Unauthorized(new { message = "Invalid email or password." });

        if (user.IsSuspended)
            return Unauthorized(new { message = "Your account has been suspended." });

        // Check password without issuing a cookie
        var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            await _userManager.AccessFailedAsync(user);
            if (await _userManager.IsLockedOutAsync(user))
                return Unauthorized(new { message = "Account locked due to multiple failed login attempts. Please try again later." });
            return Unauthorized(new { message = "Invalid email or password." });
        }

        // Reset lockout on success
        await _userManager.ResetAccessFailedCountAsync(user);

        var loginResponse = await BuildLoginResponseAsync(user);
        return Ok(loginResponse);
    }

    [HttpPost("google/exchange")]
    public async Task<IActionResult> ExchangeGoogleLogin([FromBody] MobileGoogleExchangeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExchangeToken))
            return BadRequest(new { message = "Exchange token is required." });

        if (!_mobileExternalAuthTokenService.TryUnprotectLoginExchange(request.ExchangeToken, out var payload))
            return BadRequest(new { message = "Google sign-in token is invalid or expired." });

        var user = await _userManager.FindByIdAsync(payload.UserId.ToString());
        if (user == null)
            return Unauthorized(new { message = "User not found." });

        if (user.IsSuspended)
            return Unauthorized(new { message = "Your account has been suspended." });

        var loginResponse = await BuildLoginResponseAsync(user);
        return Ok(loginResponse);
    }

    [HttpPost("google/register")]
    public Task<IActionResult> RegisterWithGoogle([FromBody] MobileGoogleRegisterRequest request)
        => CompleteExternalRegistrationAsync(
            request.PendingRegistrationToken,
            request.AcceptTermsOfUse,
            request.AcceptPrivacyPolicy,
            request.AcceptRefundPolicy,
            ExternalLoginProviders.Google);

    /// <summary>
    /// Turns a pending-registration token plus the three policy acceptances into a real account.
    /// Shared by Google and Apple - the only thing that differs is which provider the token is
    /// required to have been minted for.
    /// </summary>
    private async Task<IActionResult> CompleteExternalRegistrationAsync(
        string pendingRegistrationToken,
        bool acceptTermsOfUse,
        bool acceptPrivacyPolicy,
        bool acceptRefundPolicy,
        string expectedProvider)
    {
        if (string.IsNullOrWhiteSpace(pendingRegistrationToken))
            return BadRequest(new { message = "Pending registration token is required." });

        if (!acceptTermsOfUse || !acceptPrivacyPolicy || !acceptRefundPolicy)
        {
            return BadRequest(new { message = "You must accept the Terms of Use, Privacy Policy, and Refund Policy to register." });
        }

        if (!_mobileExternalAuthTokenService.TryUnprotectPendingRegistration(pendingRegistrationToken, out var payload))
            return BadRequest(new { message = $"{expectedProvider} registration token is invalid or expired." });

        if (!string.Equals(payload.LoginProvider, expectedProvider, StringComparison.Ordinal))
            return BadRequest(new { message = "Invalid external login provider." });

        var existingLoginUser = await _userManager.FindByLoginAsync(payload.LoginProvider, payload.ProviderKey);
        if (existingLoginUser != null)
        {
            if (existingLoginUser.IsSuspended)
                return Unauthorized(new { message = "Your account has been suspended." });

            var existingLoginResponse = await BuildLoginResponseAsync(existingLoginUser);
            return Ok(existingLoginResponse);
        }

        var user = await _userManager.FindByEmailAsync(payload.Email);
        var isNewUser = false;

        if (user == null)
        {
            user = new ApplicationUser
            {
                UserName = payload.Email,
                Email = payload.Email,
                EmailConfirmed = true
            };

            var createResult = await _userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                return BadRequest(new { message = string.Join("; ", createResult.Errors.Select(e => e.Description)) });
            }

            isNewUser = true;
        }

        if (user.IsSuspended)
            return Unauthorized(new { message = "Your account has been suspended." });

        var (linkSuccess, linkError) = await EnsureExternalLoginAsync(user, payload.LoginProvider, payload.ProviderKey);
        if (!linkSuccess)
            return BadRequest(new { message = linkError });

        var (promoteSuccess, promoteError) = await _authService.MarkEmailVerifiedAndPromoteRoleAsync(user);
        if (!promoteSuccess)
            return StatusCode(500, new { message = promoteError });

        if (!string.IsNullOrWhiteSpace(payload.ExternalRefreshToken)
            && payload.ExternalRefreshToken != user.AppleRefreshToken)
        {
            user.AppleRefreshToken = payload.ExternalRefreshToken;
            await _userManager.UpdateAsync(user);
        }

        if (isNewUser)
        {
            await NotifyExternalRegistrationAsync(user, expectedProvider);
        }

        var loginResponse = await BuildLoginResponseAsync(user);
        return Ok(loginResponse);
    }

    /// <summary>
    /// Completes a native (iOS) Sign in with Apple. The device has already driven the Apple
    /// sheet, so unlike the Google flow there is no start/callback/exchange round trip - the app
    /// posts the finished identity token and we verify it.
    /// </summary>
    [HttpPost("apple/token")]
    public async Task<IActionResult> AppleToken([FromBody] MobileAppleTokenRequest request)
    {
        if (!_appleIdentityTokenValidator.IsConfigured)
        {
            return StatusCode(503, new { message = "Sign in with Apple is not configured." });
        }

        var (valid, applePayload, validationError) = await _appleIdentityTokenValidator
            .ValidateAsync(request.IdentityToken, HttpContext.RequestAborted);
        if (!valid)
        {
            return BadRequest(new { message = validationError });
        }

        // Apple supplies the email and name on the FIRST authorization only; every later sign-in
        // carries just the subject. So the subject is the durable identity and must be tried
        // first - resolving by email would strand every returning user.
        var user = await _userManager.FindByLoginAsync(ExternalLoginProviders.Apple, applePayload.Subject);
        if (user != null)
        {
            if (user.IsSuspended)
            {
                return Unauthorized(new { message = "Your account has been suspended." });
            }

            await CaptureAppleRefreshTokenAsync(user, request.AuthorizationCode);
            return Ok(new MobileAppleTokenResponse
            {
                Email = user.Email ?? string.Empty,
                Login = await BuildLoginResponseAsync(user)
            });
        }

        var email = !string.IsNullOrWhiteSpace(applePayload.Email)
            ? applePayload.Email
            : request.Email ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new { message = "Apple sign-in did not provide an email address." });
        }

        // A "Hide My Email" relay address simply will not match anything here, which is correct -
        // it is a brand new identity, not a failed lookup.
        user = await _userManager.FindByEmailAsync(email);
        if (user != null)
        {
            if (user.IsSuspended)
            {
                return Unauthorized(new { message = "Your account has been suspended." });
            }

            var (linked, linkError) = await EnsureExternalLoginAsync(
                user, ExternalLoginProviders.Apple, applePayload.Subject);
            if (!linked)
            {
                return BadRequest(new { message = linkError });
            }

            var (promoted, promoteError) = await _authService.MarkEmailVerifiedAndPromoteRoleAsync(user);
            if (!promoted)
            {
                return StatusCode(500, new { message = promoteError });
            }

            await CaptureAppleRefreshTokenAsync(user, request.AuthorizationCode);
            return Ok(new MobileAppleTokenResponse
            {
                Email = user.Email ?? string.Empty,
                Login = await BuildLoginResponseAsync(user)
            });
        }

        var pendingToken = _mobileExternalAuthTokenService.ProtectPendingRegistration(
            new MobilePendingExternalRegistrationTokenPayload(
                ExternalLoginProviders.Apple,
                applePayload.Subject,
                email,
                request.FullName ?? string.Empty,
                await ExchangeAppleRefreshTokenAsync(request.AuthorizationCode)));

        return Ok(new MobileAppleTokenResponse
        {
            RequiresRegistration = true,
            PendingRegistrationToken = pendingToken,
            Email = email
        });
    }

    [HttpPost("apple/register")]
    public Task<IActionResult> RegisterWithApple([FromBody] MobileAppleRegisterRequest request)
        => CompleteExternalRegistrationAsync(
            request.PendingRegistrationToken,
            request.AcceptTermsOfUse,
            request.AcceptPrivacyPolicy,
            request.AcceptRefundPolicy,
            ExternalLoginProviders.Apple);

    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode([FromBody] MobileVerifyCodeRequest request)
    {
        if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { message = "User ID and code are required." });

        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user == null)
            return BadRequest(new { message = "Invalid user." });

        var valid = await ValidateCodeAsync(user.Id, request.Code, MobileVerificationPurpose.EmailVerification);
        if (!valid)
            return BadRequest(new { message = "Invalid or expired verification code." });

        // Confirm email and promote role
        if (!user.EmailConfirmed)
        {
            // Generate token and use ConfirmEmailAndPromoteRoleAsync (no URL-decode)
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var (success, error) = await _authService.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), token);
            if (!success)
                _logger.LogWarning("Email confirmation/role promotion failed for user {UserId}: {Error}", user.Id, error);

            // Send welcome email and admin notification
            await SendPostVerificationNotificationsAsync(user);
        }

        var loginResponse = await BuildLoginResponseAsync(user);
        return Ok(loginResponse);
    }

    [HttpPost("resend-code")]
    public async Task<IActionResult> ResendCode([FromBody] MobileResendCodeRequest request)
    {
        if (request.UserId <= 0)
            return BadRequest(new { message = "User ID is required." });

        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user == null)
            return BadRequest(new { message = "Invalid user." });

        // Check cooldown
        var lastCode = await _context.MobileVerificationCodes
            .Where(c => c.UserId == user.Id && c.Purpose == MobileVerificationPurpose.EmailVerification)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (lastCode != null && lastCode.CreatedAt.AddMinutes(ResendCooldownMinutes) > DateTime.UtcNow)
        {
            var waitSeconds = (int)(lastCode.CreatedAt.AddMinutes(ResendCooldownMinutes) - DateTime.UtcNow).TotalSeconds;
            return BadRequest(new { message = $"Please wait {waitSeconds} seconds before requesting a new code." });
        }

        var sendResult = await GenerateAndSendCodeAsync(user, MobileVerificationPurpose.EmailVerification,
            "Verify Your Email", BuildVerificationEmailBody);
        if (!sendResult.Success)
            return StatusCode(500, new { message = sendResult.Error });

        return Ok(new { message = "A new verification code has been sent to your email." });
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] MobileForgotPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { message = "Email is required." });

        var user = await _userManager.FindByEmailOrUsernameAsync(request.Email);
        if (user == null)
        {
            // Don't reveal whether the email exists
            return Ok(new { userId = 0, message = "If an account with that email exists, a reset code has been sent." });
        }

        // Check cooldown
        var lastCode = await _context.MobileVerificationCodes
            .Where(c => c.UserId == user.Id && c.Purpose == MobileVerificationPurpose.PasswordReset)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (lastCode != null && lastCode.CreatedAt.AddMinutes(ResendCooldownMinutes) > DateTime.UtcNow)
        {
            return Ok(new { userId = user.Id, message = "If an account with that email exists, a reset code has been sent." });
        }

        var sendResult = await GenerateAndSendCodeAsync(user, MobileVerificationPurpose.PasswordReset,
            "Reset Your Password", BuildPasswordResetEmailBody);
        if (!sendResult.Success)
            return StatusCode(500, new { message = sendResult.Error });

        return Ok(new { userId = user.Id, message = "If an account with that email exists, a reset code has been sent." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] MobileResetPasswordRequest request)
    {
        if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { message = "User ID, code, and new password are required." });

        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user == null)
            return BadRequest(new { message = "Invalid user." });

        var valid = await ValidateCodeAsync(user.Id, request.Code, MobileVerificationPurpose.PasswordReset);
        if (!valid)
            return BadRequest(new { message = "Invalid or expired reset code." });

        // Generate a password reset token and immediately use it
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            return BadRequest(new { message = errors });
        }

        _logger.LogInformation("Password reset via mobile for user {UserId}", user.Id);
        return Ok(new { message = "Password has been reset successfully." });
    }

    [HttpPost("change-email")]
    public async Task<IActionResult> ChangeEmail([FromBody] MobileChangeEmailRequest request)
    {
        if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.NewEmail))
            return BadRequest(new { message = "User ID and new email are required." });

        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user == null)
            return BadRequest(new { message = "Invalid user." });

        if (user.EmailConfirmed)
            return BadRequest(new { message = "Cannot change email after verification. Please create a new account." });

        var newEmailNormalized = request.NewEmail.Trim().ToUpperInvariant();
        var existingUser = await _userManager.FindByEmailAsync(request.NewEmail.Trim());
        if (existingUser != null && existingUser.Id != user.Id)
            return BadRequest(new { message = "This email address is already in use." });

        // Update email
        user.Email = request.NewEmail.Trim();
        user.NormalizedEmail = newEmailNormalized;
        user.UserName = request.NewEmail.Trim();
        user.NormalizedUserName = newEmailNormalized;
        await _userManager.UpdateAsync(user);

        // Generate and send a new verification code
        var sendResult = await GenerateAndSendCodeAsync(user, MobileVerificationPurpose.EmailVerification,
            "Verify Your Email", BuildVerificationEmailBody);
        if (!sendResult.Success)
            return StatusCode(500, new { message = sendResult.Error });

        _logger.LogInformation("Email changed from mobile for user {UserId} to {NewEmail}", user.Id, request.NewEmail);
        return Ok(new { message = "Email updated. A new verification code has been sent." });
    }

    [Authorize]
    [HttpDelete("account")]
    public async Task<IActionResult> DeleteAccount()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Invalid authentication." });

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return NotFound(new { message = "User not found." });

        // Check for active (non-cancelled) subscription
        var hasActiveSubscription = await _context.Subscriptions
            .AnyAsync(s => s.UserId == user.Id && s.EndDate == null);

        if (hasActiveSubscription)
            return Conflict(new { message = "You must cancel your active subscription before deleting your account." });

        // Capture user info before deletion
        var userEmail = user.Email ?? string.Empty;
        var userName = user.UserName ?? userEmail;
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var result = await _accountDeletionService.DeleteAccountAsync(user);

        if (!result.Succeeded)
        {
            var activeCreatorError = result.Errors.FirstOrDefault(e => e.Code == AccountDeletionErrorCodes.ActiveCreatorMustStopSellingFirst);
            if (activeCreatorError != null)
            {
                _logger.LogInformation("Blocked mobile account deletion for active creator user {UserId}", userId);
                return Conflict(new { message = activeCreatorError.Description });
            }

            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogError("Account deletion failed for user {UserId}: {Errors}", userId, errors);
            return StatusCode(500, new { message = "Failed to delete account. Please try again." });
        }

        // Send account deleted email (fire-and-forget, don't fail the response)
        if (!string.IsNullOrEmpty(userEmail))
        {
            try
            {
                await _accountEmailService.SendAccountDeletedEmailAsync(userEmail, userName, baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send account deleted email for {Email}", userEmail);
            }
        }

        _logger.LogInformation("Account deleted via mobile for user {UserId} ({Email})", userId, userEmail);
        return Ok(new { message = "Your account has been permanently deleted." });
    }

    // --- Helper methods ---

    private async Task SendPostVerificationNotificationsAsync(ApplicationUser user)
    {
        var userEmail = user.Email ?? string.Empty;
        var userName = user.UserName ?? userEmail;
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        try
        {
            await _accountEmailService.SendAccountCreatedEmailAsync(userEmail, userName, baseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send welcome email for {Email}", userEmail);
        }

        try
        {
            await _adminNotificationService.NotifyEmailConfirmedAsync(userEmail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send admin email-confirmed notification for {Email}", userEmail);
        }
    }

    private async Task NotifyExternalRegistrationAsync(ApplicationUser user, string provider)
    {
        try
        {
            await _adminNotificationService.NotifyUserRegisteredAsync(user.Email ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send admin registration notification for {Provider} user {Email}", provider, user.Email);
        }

        await SendPostVerificationNotificationsAsync(user);
    }

    private async Task<MobileLoginResponse> BuildLoginResponseAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var permissions = roles.SelectMany(GetPermissionsForRole).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var token = GenerateJwtToken(user, roles, permissions);

        if (_payPalSubscriptionManagementService != null)
        {
            await _payPalSubscriptionManagementService.RefreshIfNeededAsync(user.Id, GetSubscriptionBaseUrl());
        }

        var activeSubscription = await _subscriptionService.GetActiveSubscriptionAsync(user.Id);
        var latestSubscription = activeSubscription ?? await _subscriptionService.GetLatestSubscriptionAsync(user.Id);
        var isOnTrial = latestSubscription?.TrialEndDate is { } trialEndDate
            && trialEndDate > DateTime.UtcNow
            && activeSubscription != null;

        var creator = await _context.Creators.FirstOrDefaultAsync(c => c.UserId == user.Id && c.IsActive);

        return new MobileLoginResponse
        {
            Token = token,
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            Roles = roles.ToList(),
            EmailConfirmed = user.EmailConfirmed,
            HasActiveSubscription = activeSubscription != null,
            IsOnTrial = isOnTrial,
            SubscriptionStatus = latestSubscription?.Status,
            SubscriptionEndDate = latestSubscription?.EndDate,
            TrialEndDate = latestSubscription?.TrialEndDate,
            BillingSource = latestSubscription?.BillingSource,
            IsCreator = creator != null,
            CreatorId = creator?.Id
        };
    }

    private string GetSubscriptionBaseUrl()
    {
        var configured = _configuration["PayPal:ReturnBaseUrl"] ?? _configuration["BaseUrl"];
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : $"{Request.Scheme}://{Request.Host}";
    }

    private string GenerateJwtToken(ApplicationUser user, IList<string> roles, List<string> permissions)
    {
        var key = _configuration["Jwt:SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey not configured");
        var issuer = _configuration["Jwt:Issuer"] ?? "MusicSalesApp";
        var audience = _configuration["Jwt:Audience"] ?? "MusicSalesApp.Maui";
        var expireMinutes = _configuration.GetValue<int>("Jwt:ExpireMinutes", 10080); // 7 days default

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim(CustomClaimTypes.Permission, p)));

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(expireMinutes),
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return handler.WriteToken(token);
    }

    private string GetMobileExternalAuthCallbackUrl()
    {
        return _configuration[AppSettingKeys.MobileExternalAuthCallbackUrl] ?? "streamtunes://auth";
    }

    private static string BuildMobileExternalAuthCallback(string callbackUrl, IDictionary<string, string> parameters)
    {
        var query = new QueryBuilder();
        foreach (var parameter in parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Value))
            {
                query.Add(parameter.Key, parameter.Value);
            }
        }

        return callbackUrl + query.ToQueryString();
    }

    private static bool TryGetGoogleEmail(ClaimsPrincipal principal, out string email)
    {
        email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue(GoogleClaimTypes.Email)
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var emailVerified = principal.FindFirstValue(GoogleClaimTypes.EmailVerified);
        return !string.Equals(emailVerified, bool.FalseString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Trades the sheet's one-shot authorization code for a refresh token and stores it, so the
    /// account-deletion path can later revoke the user's Apple grant as Apple requires.
    /// </summary>
    /// <remarks>
    /// Best-effort on purpose: a user must still be able to sign in when Apple's token endpoint is
    /// unreachable or no Sign in with Apple key is configured yet.
    /// </remarks>
    private async Task CaptureAppleRefreshTokenAsync(ApplicationUser user, string authorizationCode)
    {
        var refreshToken = await ExchangeAppleRefreshTokenAsync(authorizationCode);
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken == user.AppleRefreshToken)
        {
            return;
        }

        try
        {
            user.AppleRefreshToken = refreshToken;
            await _userManager.UpdateAsync(user);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not store the Apple refresh token for user {UserId}", user.Id);
        }
    }

    private async Task<string> ExchangeAppleRefreshTokenAsync(string authorizationCode)
    {
        if (!_appleTokenRevocationService.IsConfigured || string.IsNullOrWhiteSpace(authorizationCode))
        {
            return null;
        }

        try
        {
            return await _appleTokenRevocationService
                .ExchangeAuthorizationCodeAsync(authorizationCode, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not exchange the Apple authorization code");
            return null;
        }
    }

    private async Task<(bool Success, string Error)> EnsureExternalLoginAsync(
        ApplicationUser user,
        string loginProvider,
        string providerKey)
    {
        var existingLogins = await _userManager.GetLoginsAsync(user);
        if (existingLogins.Any(login =>
                string.Equals(login.LoginProvider, loginProvider, StringComparison.Ordinal) &&
                string.Equals(login.ProviderKey, providerKey, StringComparison.Ordinal)))
        {
            return (true, string.Empty);
        }

        var result = await _userManager.AddLoginAsync(user, new UserLoginInfo(loginProvider, providerKey, loginProvider));
        if (!result.Succeeded)
        {
            return (false, string.Join("; ", result.Errors.Select(error => error.Description)));
        }

        return (true, string.Empty);
    }

    private async Task<(bool Success, string Error)> GenerateAndSendCodeAsync(
        ApplicationUser user, string purpose, string emailSubject,
        Func<string, string, string> buildBody)
    {
        // Remove any existing codes for this user and purpose
        var existing = await _context.MobileVerificationCodes
            .Where(c => c.UserId == user.Id && c.Purpose == purpose)
            .ToListAsync();
        _context.MobileVerificationCodes.RemoveRange(existing);

        // Generate secure 6-digit code
        var code = GenerateSecureCode();

        _context.MobileVerificationCodes.Add(new MobileVerificationCode
        {
            UserId = user.Id,
            Code = code,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(CodeExpirationMinutes),
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Send email
        var logoHtml = _emailService.GetEmailLogoHtml();
        var body = buildBody(logoHtml, code);
        var emailResult = await _emailService.SendEmailWithResultAsync(user.Email!, emailSubject, body);
        if (!emailResult.Success)
        {
            _logger.LogWarning("Failed to send {Purpose} email to {Email}: {Error}", purpose, user.Email, emailResult.ErrorMessage);
            return (false, "Failed to send email. Please try again later.");
        }

        return (true, string.Empty);
    }

    private async Task<bool> ValidateCodeAsync(int userId, string code, string purpose)
    {
        var record = await _context.MobileVerificationCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.Code == code)
            .FirstOrDefaultAsync();

        if (record == null || record.ExpiresAt < DateTime.UtcNow)
            return false;

        // Remove the used code
        _context.MobileVerificationCodes.Remove(record);
        await _context.SaveChangesAsync();
        return true;
    }

    private static string GenerateSecureCode()
    {
        // Generate a cryptographically secure 6-digit code
        var number = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return number.ToString().PadLeft(CodeLength, '0');
    }

    private static string BuildVerificationEmailBody(string logoHtml, string code)
    {
        return $@"{logoHtml}
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
    <h2 style='color: #333;'>Verify Your Email</h2>
    <p>Your verification code is:</p>
    <div style='background: #f5f5f5; padding: 20px; text-align: center; border-radius: 8px; margin: 20px 0;'>
        <span style='font-size: 32px; font-weight: bold; letter-spacing: 8px; color: #1db954;'>{code}</span>
    </div>
    <p>This code expires in {CodeExpirationMinutes} minutes.</p>
    <p style='color: #666; font-size: 14px;'>If you did not create an account, you can ignore this email.</p>
</div>";
    }

    private static string BuildPasswordResetEmailBody(string logoHtml, string code)
    {
        return $@"{logoHtml}
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
    <h2 style='color: #333;'>Reset Your Password</h2>
    <p>Your password reset code is:</p>
    <div style='background: #f5f5f5; padding: 20px; text-align: center; border-radius: 8px; margin: 20px 0;'>
        <span style='font-size: 32px; font-weight: bold; letter-spacing: 8px; color: #1db954;'>{code}</span>
    </div>
    <p>This code expires in {CodeExpirationMinutes} minutes.</p>
    <p style='color: #666; font-size: 14px;'>If you did not request a password reset, you can ignore this email.</p>
</div>";
    }

    private static List<string> GetPermissionsForRole(string role)
    {
        var permissions = new List<string>();
        if (role.Equals(Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            var all = typeof(Permissions).GetFields()
                .Select(f => f.GetValue(null)?.ToString())
                .Where(v => !string.IsNullOrEmpty(v));
            permissions.AddRange(all.Where(p => !string.Equals(p, Permissions.NonValidatedUser, StringComparison.OrdinalIgnoreCase))!);
        }
        else if (role.Equals(Roles.User, StringComparison.OrdinalIgnoreCase))
        {
            permissions.Add(Permissions.ValidatedUser);
        }
        return permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

// --- Request/Response DTOs ---

public class MobileRegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class MobileLoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class MobileVerifyCodeRequest
{
    public int UserId { get; set; }
    public string Code { get; set; } = string.Empty;
}

public class MobileResendCodeRequest
{
    public int UserId { get; set; }
}

public class MobileForgotPasswordRequest
{
    public string Email { get; set; } = string.Empty;
}

public class MobileResetPasswordRequest
{
    public int UserId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class MobileChangeEmailRequest
{
    public int UserId { get; set; }
    public string NewEmail { get; set; } = string.Empty;
}

public class MobileLoginResponse
{
    public string Token { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
    public bool EmailConfirmed { get; set; }
    public bool HasActiveSubscription { get; set; }
    public bool IsOnTrial { get; set; }
    public string SubscriptionStatus { get; set; } = string.Empty;
    public DateTime? SubscriptionEndDate { get; set; }
    public DateTime? TrialEndDate { get; set; }
    public string BillingSource { get; set; } = string.Empty;
    public bool IsCreator { get; set; }
    public int? CreatorId { get; set; }
}

public class MobileGoogleExchangeRequest
{
    public string ExchangeToken { get; set; } = string.Empty;
}

public class MobileGoogleRegisterRequest
{
    public string PendingRegistrationToken { get; set; } = string.Empty;
    public bool AcceptTermsOfUse { get; set; }
    public bool AcceptPrivacyPolicy { get; set; }
    public bool AcceptRefundPolicy { get; set; }
}

public class MobileAppleTokenRequest
{
    public string IdentityToken { get; set; } = string.Empty;

    // Kept for a future token-revocation-on-account-deletion implementation; unused today.
    public string AuthorizationCode { get; set; } = string.Empty;

    // Apple sends these on the FIRST authorization only, so they cannot be relied on.
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
}

public class MobileAppleTokenResponse
{
    /// <summary>
    /// True when this Apple id has no account yet and the app must collect policy acceptance
    /// before <c>apple/register</c> can create one.
    /// </summary>
    public bool RequiresRegistration { get; set; }
    public string PendingRegistrationToken { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>Populated only when <see cref="RequiresRegistration"/> is false.</summary>
    public MobileLoginResponse Login { get; set; }
}

public class MobileAppleRegisterRequest
{
    public string PendingRegistrationToken { get; set; } = string.Empty;
    public bool AcceptTermsOfUse { get; set; }
    public bool AcceptPrivacyPolicy { get; set; }
    public bool AcceptRefundPolicy { get; set; }
}
