#nullable enable
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using AppAuthenticationService = MusicSalesApp.Services.IAuthenticationService;

namespace MusicSalesApp.Controllers;

[Route(GoogleAuthRoutes.WebBase)]
public class WebGoogleAuthController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppAuthenticationService _authService;
    private readonly IMobileExternalAuthTokenService _mobileExternalAuthTokenService;
    private readonly IWebGoogleAuthTokenService _webGoogleAuthTokenService;
    private readonly IAccountEmailService _accountEmailService;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly ILogger<WebGoogleAuthController> _logger;

    public WebGoogleAuthController(
        IConfiguration configuration,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        AppAuthenticationService authService,
        IMobileExternalAuthTokenService mobileExternalAuthTokenService,
        IWebGoogleAuthTokenService webGoogleAuthTokenService,
        IAccountEmailService accountEmailService,
        IAdminNotificationService adminNotificationService,
        ILogger<WebGoogleAuthController> logger)
    {
        _configuration = configuration;
        _signInManager = signInManager;
        _userManager = userManager;
        _authService = authService;
        _mobileExternalAuthTokenService = mobileExternalAuthTokenService;
        _webGoogleAuthTokenService = webGoogleAuthTokenService;
        _accountEmailService = accountEmailService;
        _adminNotificationService = adminNotificationService;
        _logger = logger;
    }

    [HttpGet("start")]
    public IActionResult StartLogin([FromQuery(Name = ExternalAuthFormFields.ReturnUrl)] string returnUrl = AppPageRoutes.Home)
    {
        return StartGoogleChallenge(registrationIntentToken: null, returnUrl);
    }

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    public IActionResult StartRegistration(
        [FromForm(Name = ExternalAuthFormFields.AcceptTermsOfUse)] bool acceptTermsOfUse,
        [FromForm(Name = ExternalAuthFormFields.AcceptPrivacyPolicy)] bool acceptPrivacyPolicy,
        [FromForm(Name = ExternalAuthFormFields.AcceptRefundPolicy)] bool acceptRefundPolicy,
        [FromForm(Name = ExternalAuthFormFields.ReceiveNewSongEmails)] bool receiveNewSongEmails,
        [FromForm(Name = ExternalAuthFormFields.ReturnUrl)] string returnUrl = AppPageRoutes.Home)
    {
        var normalizedReturnUrl = NormalizeLocalReturnUrl(returnUrl);
        string? registrationIntentToken = null;
        if (acceptTermsOfUse && acceptPrivacyPolicy && acceptRefundPolicy)
        {
            registrationIntentToken = _webGoogleAuthTokenService.ProtectRegistrationIntent(
                new WebGoogleRegistrationIntentTokenPayload(
                    acceptTermsOfUse,
                    acceptPrivacyPolicy,
                    acceptRefundPolicy,
                    receiveNewSongEmails,
                    normalizedReturnUrl));
        }

        return StartGoogleChallenge(registrationIntentToken, normalizedReturnUrl);
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery(Name = ExternalAuthFormFields.RegistrationIntentToken)] string registrationIntentToken = "",
        [FromQuery(Name = ExternalAuthFormFields.ReturnUrl)] string callbackReturnUrl = AppPageRoutes.Home)
    {
        try
        {
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null || !string.Equals(info.LoginProvider, ExternalLoginProviders.Google, StringComparison.Ordinal))
            {
                return RedirectToLoginError("Google sign-in could not be completed.");
            }

            if (!TryGetGoogleEmail(info.Principal, out var email))
            {
                return RedirectToLoginError("Google account did not provide a verified email address.");
            }

            var returnUrl = NormalizeLocalReturnUrl(callbackReturnUrl);
            WebGoogleRegistrationIntentTokenPayload? registrationIntent = null;
            if (!string.IsNullOrWhiteSpace(registrationIntentToken))
            {
                if (!_webGoogleAuthTokenService.TryUnprotectRegistrationIntent(registrationIntentToken, out var payload)
                    || !payload.AcceptTermsOfUse
                    || !payload.AcceptPrivacyPolicy
                    || !payload.AcceptRefundPolicy)
                {
                    return RedirectToRegisterError("Google registration session expired. Please try again.", returnUrl: returnUrl);
                }

                registrationIntent = payload;
                returnUrl = NormalizeLocalReturnUrl(payload.ReturnUrl);
            }

            var linkedUser = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (linkedUser != null)
            {
                return await SignInGoogleUserAsync(linkedUser, returnUrl);
            }

            var existingEmailUser = await _userManager.FindByEmailAsync(email);
            if (existingEmailUser != null)
            {
                return await LinkPromoteAndSignInAsync(existingEmailUser, info.LoginProvider, info.ProviderKey, returnUrl);
            }

            if (registrationIntent != null)
            {
                return await CompleteGoogleRegistrationAsync(
                    info.LoginProvider,
                    info.ProviderKey,
                    email,
                    info.Principal.Identity?.Name ?? email,
                    registrationIntent.ReceiveNewSongEmails,
                    returnUrl);
            }

            var pendingRegistrationToken = _mobileExternalAuthTokenService.ProtectPendingRegistration(
                new MobilePendingExternalRegistrationTokenPayload(
                    info.LoginProvider,
                    info.ProviderKey,
                    email,
                    info.Principal.Identity?.Name ?? string.Empty));

            return RedirectToRegisterPending(pendingRegistrationToken, email, returnUrl);
        }
        finally
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        }
    }

    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(
        [FromForm(Name = ExternalAuthFormFields.PendingRegistrationToken)] string pendingRegistrationToken,
        [FromForm(Name = ExternalAuthFormFields.AcceptTermsOfUse)] bool acceptTermsOfUse,
        [FromForm(Name = ExternalAuthFormFields.AcceptPrivacyPolicy)] bool acceptPrivacyPolicy,
        [FromForm(Name = ExternalAuthFormFields.AcceptRefundPolicy)] bool acceptRefundPolicy,
        [FromForm(Name = ExternalAuthFormFields.ReceiveNewSongEmails)] bool receiveNewSongEmails,
        [FromForm(Name = ExternalAuthFormFields.Email)] string email = "",
        [FromForm(Name = ExternalAuthFormFields.ReturnUrl)] string returnUrl = AppPageRoutes.Home)
    {
        var normalizedReturnUrl = NormalizeLocalReturnUrl(returnUrl);
        if (string.IsNullOrWhiteSpace(pendingRegistrationToken))
        {
            return RedirectToRegisterError("Google registration session expired. Please try again.", returnUrl: normalizedReturnUrl);
        }

        if (!acceptTermsOfUse || !acceptPrivacyPolicy || !acceptRefundPolicy)
        {
            return RedirectToRegisterError(
                "You must accept the Terms of Use, Privacy Policy, and Refund Policy to register.",
                pendingRegistrationToken,
                email,
                normalizedReturnUrl);
        }

        if (!_mobileExternalAuthTokenService.TryUnprotectPendingRegistration(pendingRegistrationToken, out var payload)
            || !string.Equals(payload.LoginProvider, ExternalLoginProviders.Google, StringComparison.Ordinal))
        {
            return RedirectToRegisterError("Google registration session expired. Please try again.", returnUrl: normalizedReturnUrl);
        }

        return await CompleteGoogleRegistrationAsync(
            payload.LoginProvider,
            payload.ProviderKey,
            payload.Email,
            payload.DisplayName,
            receiveNewSongEmails,
            normalizedReturnUrl);
    }

    private IActionResult StartGoogleChallenge(string? registrationIntentToken, string returnUrl)
    {
        if (string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientId"]) ||
            string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientSecret"]))
        {
            return RedirectToLoginError("Google sign-in is not configured.");
        }

        var normalizedReturnUrl = NormalizeLocalReturnUrl(returnUrl);
        var callbackUrl = UriHelper.BuildAbsolute(
            Request.Scheme,
            Request.Host,
            Request.PathBase,
            new PathString(GoogleAuthRoutes.WebCallbackPath));

        callbackUrl = QueryHelpers.AddQueryString(
            callbackUrl,
            ExternalAuthFormFields.ReturnUrl,
            normalizedReturnUrl);

        if (!string.IsNullOrWhiteSpace(registrationIntentToken))
        {
            callbackUrl = QueryHelpers.AddQueryString(
                callbackUrl,
                ExternalAuthFormFields.RegistrationIntentToken,
                registrationIntentToken);
        }

        var properties = _signInManager.ConfigureExternalAuthenticationProperties(
            ExternalLoginProviders.Google,
            callbackUrl);

        return Challenge(properties, ExternalLoginProviders.Google);
    }

    private async Task<IActionResult> CompleteGoogleRegistrationAsync(
        string loginProvider,
        string providerKey,
        string email,
        string displayName,
        bool receiveNewSongEmails,
        string returnUrl)
    {
        var existingLoginUser = await _userManager.FindByLoginAsync(loginProvider, providerKey);
        if (existingLoginUser != null)
        {
            return await SignInGoogleUserAsync(existingLoginUser, returnUrl);
        }

        var user = await _userManager.FindByEmailAsync(email);
        var isNewUser = false;

        if (user == null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                ReceiveNewSongEmails = receiveNewSongEmails
            };

            var createResult = await _userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                return RedirectToRegisterError(string.Join("; ", createResult.Errors.Select(error => error.Description)));
            }

            isNewUser = true;
        }

        var signInResult = await LinkPromoteAndSignInAsync(user, loginProvider, providerKey, returnUrl);
        if (signInResult is not LocalRedirectResult)
        {
            return signInResult;
        }

        if (isNewUser)
        {
            await NotifyGoogleRegistrationAsync(user, displayName);
        }

        return signInResult;
    }

    private async Task<IActionResult> LinkPromoteAndSignInAsync(
        ApplicationUser user,
        string loginProvider,
        string providerKey,
        string returnUrl)
    {
        if (user.IsSuspended)
        {
            return RedirectToLoginError("Your account has been suspended.");
        }

        var (linkSuccess, linkError) = await EnsureExternalLoginAsync(user, loginProvider, providerKey);
        if (!linkSuccess)
        {
            return RedirectToLoginError(linkError);
        }

        return await SignInGoogleUserAsync(user, returnUrl);
    }

    private async Task<IActionResult> SignInGoogleUserAsync(ApplicationUser user, string returnUrl)
    {
        if (user.IsSuspended)
        {
            return RedirectToLoginError("Your account has been suspended.");
        }

        var (promoteSuccess, promoteError) = await _authService.MarkEmailVerifiedAndPromoteRoleAsync(user);
        if (!promoteSuccess)
        {
            return RedirectToLoginError(promoteError);
        }

        await _signInManager.SignInAsync(user, isPersistent: true, authenticationMethod: ExternalLoginProviders.Google);
        _logger.LogInformation("User {Email} signed in with Google", user.Email);
        return LocalRedirect(NormalizeLocalReturnUrl(returnUrl));
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

    private async Task NotifyGoogleRegistrationAsync(ApplicationUser user, string displayName)
    {
        var email = user.Email ?? string.Empty;
        var userName = string.IsNullOrWhiteSpace(displayName) ? user.UserName ?? email : displayName;
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        try
        {
            await _adminNotificationService.NotifyUserRegisteredAsync(email);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send admin registration notification for Google user {Email}", email);
        }

        try
        {
            await _accountEmailService.SendAccountCreatedEmailAsync(email, userName, baseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send welcome email for Google user {Email}", email);
        }

        try
        {
            await _adminNotificationService.NotifyEmailConfirmedAsync(email);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send admin email-confirmed notification for Google user {Email}", email);
        }
    }

    private static bool TryGetGoogleEmail(ClaimsPrincipal principal, out string email)
    {
        email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue(GoogleClaimTypes.Email)
            ?? string.Empty;
        email = email.Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var emailVerified = principal.FindFirstValue(GoogleClaimTypes.EmailVerified);
        return !string.Equals(emailVerified, bool.FalseString, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLocalReturnUrl(string returnUrl)
    {
        return string.IsNullOrWhiteSpace(returnUrl) || !IsLocalUrl(returnUrl)
            ? AppPageRoutes.Home
            : returnUrl;
    }

    private static bool IsLocalUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url[0] == '/')
        {
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }

        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
        {
            return url.Length == 2 || (url[2] != '/' && url[2] != '\\');
        }

        return false;
    }

    private RedirectResult RedirectToLoginError(string error)
    {
        return Redirect(QueryHelpers.AddQueryString(AppPageRoutes.Login, ExternalAuthFormFields.Error, error));
    }

    private RedirectResult RedirectToRegisterError(
        string error,
        string pendingRegistrationToken = "",
        string email = "",
        string returnUrl = AppPageRoutes.Home)
    {
        var query = new Dictionary<string, string?>
        {
            [ExternalAuthFormFields.Error] = error
        };

        if (!string.IsNullOrWhiteSpace(pendingRegistrationToken))
        {
            query[ExternalAuthFormFields.PendingRegistrationToken] = pendingRegistrationToken;
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            query[ExternalAuthFormFields.Email] = email;
        }

        var normalizedReturnUrl = NormalizeLocalReturnUrl(returnUrl);
        if (!string.Equals(normalizedReturnUrl, AppPageRoutes.Home, StringComparison.Ordinal))
        {
            query[ExternalAuthFormFields.ReturnUrl] = normalizedReturnUrl;
        }

        return Redirect(QueryHelpers.AddQueryString(AppPageRoutes.Register, query));
    }

    private RedirectResult RedirectToRegisterPending(string pendingRegistrationToken, string email, string returnUrl)
    {
        var query = new Dictionary<string, string?>
        {
            [ExternalAuthFormFields.PendingRegistrationToken] = pendingRegistrationToken,
            [ExternalAuthFormFields.Email] = email,
            [ExternalAuthFormFields.ReturnUrl] = NormalizeLocalReturnUrl(returnUrl)
        };

        return Redirect(QueryHelpers.AddQueryString(AppPageRoutes.Register, query));
    }
}
