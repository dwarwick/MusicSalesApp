using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

namespace MusicSalesApp.Controllers;

[Route("api/mobile-auth")]
[ApiController]
[RequireMobileApiKey]
public class MobileAuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthenticationService _authService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IEmailService _emailService;
    private readonly IAccountEmailService _accountEmailService;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly AppDbContext _context;
    private readonly ILogger<MobileAuthController> _logger;

    private const int CodeExpirationMinutes = 10;
    private const int CodeLength = 6;
    private const int ResendCooldownMinutes = 2;

    public MobileAuthController(
        IConfiguration configuration,
        UserManager<ApplicationUser> userManager,
        IAuthenticationService authService,
        ISubscriptionService subscriptionService,
        IEmailService emailService,
        IAccountEmailService accountEmailService,
        IAdminNotificationService adminNotificationService,
        AppDbContext context,
        ILogger<MobileAuthController> logger)
    {
        _configuration = configuration;
        _userManager = userManager;
        _authService = authService;
        _subscriptionService = subscriptionService;
        _emailService = emailService;
        _accountEmailService = accountEmailService;
        _adminNotificationService = adminNotificationService;
        _context = context;
        _logger = logger;
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

    private async Task<MobileLoginResponse> BuildLoginResponseAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var permissions = roles.SelectMany(GetPermissionsForRole).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var token = GenerateJwtToken(user, roles, permissions);

        var hasSubscription = await _subscriptionService.HasActiveSubscriptionAsync(user.Id);

        var creator = await _context.Creators.FirstOrDefaultAsync(c => c.UserId == user.Id);

        return new MobileLoginResponse
        {
            Token = token,
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            Roles = roles.ToList(),
            EmailConfirmed = user.EmailConfirmed,
            HasActiveSubscription = hasSubscription,
            IsCreator = creator != null,
            CreatorId = creator?.Id
        };
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
    public bool IsCreator { get; set; }
    public int? CreatorId { get; set; }
}
