using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using AppAuthenticationService = MusicSalesApp.Services.IAuthenticationService;
using AspNetAuthenticationService = Microsoft.AspNetCore.Authentication.IAuthenticationService;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class WebGoogleAuthControllerTests
{
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<SignInManager<ApplicationUser>> _mockSignInManager;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<AppAuthenticationService> _mockAuthService;
    private Mock<IAccountEmailService> _mockAccountEmailService;
    private Mock<IAdminNotificationService> _mockAdminNotificationService;
    private Mock<AspNetAuthenticationService> _mockAspNetAuthenticationService;
    private Mock<ILogger<WebGoogleAuthController>> _mockLogger;
    private IMobileExternalAuthTokenService _mobileExternalAuthTokenService;
    private IWebGoogleAuthTokenService _webGoogleAuthTokenService;
    private WebGoogleAuthController _controller;

    [SetUp]
    public void SetUp()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(c => c["Authentication:Google:ClientId"]).Returns("google-client-id");
        _mockConfiguration.Setup(c => c["Authentication:Google:ClientSecret"]).Returns("google-client-secret");

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        var contextAccessor = new Mock<IHttpContextAccessor>();
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _mockSignInManager = new Mock<SignInManager<ApplicationUser>>(
            _mockUserManager.Object,
            contextAccessor.Object,
            claimsFactory.Object,
            null!, null!, null!, null!);

        _mockAuthService = new Mock<AppAuthenticationService>();
        _mockAccountEmailService = new Mock<IAccountEmailService>();
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();
        _mockAspNetAuthenticationService = new Mock<AspNetAuthenticationService>();
        _mockLogger = new Mock<ILogger<WebGoogleAuthController>>();

        var dataProtectionProvider = CreateDataProtectionProvider();
        _mobileExternalAuthTokenService = new MobileExternalAuthTokenService(dataProtectionProvider);
        _webGoogleAuthTokenService = new WebGoogleAuthTokenService(dataProtectionProvider);

        _mockAuthService.Setup(x => x.MarkEmailVerifiedAndPromoteRoleAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync((true, string.Empty));
        _mockSignInManager.Setup(x => x.SignInAsync(
                It.IsAny<ApplicationUser>(),
                true,
                ExternalLoginProviders.Google))
            .Returns(Task.CompletedTask);
        _mockAspNetAuthenticationService
            .Setup(x => x.SignOutAsync(It.IsAny<HttpContext>(), IdentityConstants.ExternalScheme, null))
            .Returns(Task.CompletedTask);
        _mockAccountEmailService.Setup(x => x.SendAccountCreatedEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true);

        _controller = CreateController();
    }

    [TearDown]
    public void TearDown()
    {
        _controller.Dispose();
    }

    [Test]
    public async Task Callback_ExistingLinkedGoogleUser_SignsIn()
    {
        var user = CreateUser(emailConfirmed: true);
        SetupExternalLogin("linked@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync(user);

        var result = await _controller.Callback();

        Assert.That(result, Is.InstanceOf<LocalRedirectResult>());
        _mockSignInManager.Verify(x => x.SignInAsync(user, true, ExternalLoginProviders.Google), Times.Once);
    }

    [Test]
    public void StartRegistration_MissingPolicies_StartsGoogleChallengeWithoutRegistrationIntent()
    {
        var redirectUrl = string.Empty;
        _mockSignInManager.Setup(x => x.ConfigureExternalAuthenticationProperties(
                ExternalLoginProviders.Google,
                It.IsAny<string>(),
                null))
            .Callback<string, string, string>((_, url, _) => redirectUrl = url)
            .Returns(new AuthenticationProperties());

        var result = _controller.StartRegistration(
            acceptTermsOfUse: false,
            acceptPrivacyPolicy: false,
            acceptRefundPolicy: false,
            receiveNewSongEmails: false);

        var challenge = result as ChallengeResult;
        Assert.That(challenge, Is.Not.Null);
        Assert.That(challenge!.AuthenticationSchemes, Does.Contain(ExternalLoginProviders.Google));
        Assert.That(redirectUrl, Is.Not.Empty);
        Assert.That(redirectUrl, Does.Not.Contain(ExternalAuthFormFields.RegistrationIntentToken));
    }

    [Test]
    public void StartLogin_IncludesReturnUrlInGoogleCallback()
    {
        var redirectUrl = string.Empty;
        _mockSignInManager.Setup(x => x.ConfigureExternalAuthenticationProperties(
                ExternalLoginProviders.Google,
                It.IsAny<string>(),
                null))
            .Callback<string, string, string>((_, url, _) => redirectUrl = url)
            .Returns(new AuthenticationProperties());

        var result = _controller.StartLogin(AppPageRoutes.CreatorSettings);

        var challenge = result as ChallengeResult;
        Assert.That(challenge, Is.Not.Null);
        var query = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query);
        Assert.That(query[ExternalAuthFormFields.ReturnUrl].ToString(), Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public async Task Callback_ExistingPasswordUserByGoogleEmail_LinksPromotesAndSignsIn()
    {
        var user = CreateUser(email: "existing@example.com", emailConfirmed: false);
        SetupExternalLogin("existing@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.FindByEmailAsync("existing@example.com"))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GetLoginsAsync(user))
            .ReturnsAsync(new List<UserLoginInfo>());
        _mockUserManager.Setup(x => x.AddLoginAsync(user, It.IsAny<UserLoginInfo>()))
            .ReturnsAsync(IdentityResult.Success);

        var result = await _controller.Callback();

        Assert.That(result, Is.InstanceOf<LocalRedirectResult>());
        _mockUserManager.Verify(x => x.AddLoginAsync(
            user,
            It.Is<UserLoginInfo>(login =>
                login.LoginProvider == ExternalLoginProviders.Google &&
                login.ProviderKey == "google-provider-key")), Times.Once);
        _mockAuthService.Verify(x => x.MarkEmailVerifiedAndPromoteRoleAsync(user), Times.Once);
        _mockSignInManager.Verify(x => x.SignInAsync(user, true, ExternalLoginProviders.Google), Times.Once);
    }

    [Test]
    public async Task Callback_ExistingLinkedGoogleUser_WithReturnUrl_RedirectsToReturnUrl()
    {
        var user = CreateUser(emailConfirmed: true);
        SetupExternalLogin("linked@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync(user);

        var result = await _controller.Callback(callbackReturnUrl: AppPageRoutes.CreatorSettings);

        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public async Task Callback_NewLoginPageGoogleUser_RedirectsToRegisterWithPendingToken()
    {
        SetupExternalLogin("new@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.FindByEmailAsync("new@example.com"))
            .ReturnsAsync((ApplicationUser)null!);

        var result = await _controller.Callback();

        var redirect = result as RedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Does.StartWith("/register"));

        var query = QueryHelpers.ParseQuery(new Uri($"https://streamtunes.net{redirect.Url}").Query);
        Assert.That(query[ExternalAuthFormFields.PendingRegistrationToken].ToString(), Is.Not.Empty);
        Assert.That(query[ExternalAuthFormFields.Email].ToString(), Is.EqualTo("new@example.com"));
    }

    [Test]
    public async Task Callback_NewLoginPageGoogleUser_PreservesReturnUrlOnPendingRegistration()
    {
        SetupExternalLogin("new-return@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.FindByEmailAsync("new-return@example.com"))
            .ReturnsAsync((ApplicationUser)null!);

        var result = await _controller.Callback(callbackReturnUrl: AppPageRoutes.CreatorSettings);

        var redirect = result as RedirectResult;
        Assert.That(redirect, Is.Not.Null);
        var query = QueryHelpers.ParseQuery(new Uri($"https://streamtunes.net{redirect!.Url}").Query);
        Assert.That(query[ExternalAuthFormFields.ReturnUrl].ToString(), Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public async Task Callback_NonLocalReturnUrl_FallsBackToHome()
    {
        var user = CreateUser(emailConfirmed: true);
        SetupExternalLogin("linked@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync(user);

        var result = await _controller.Callback(callbackReturnUrl: "https://evil.example/path");

        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Is.EqualTo(AppPageRoutes.Home));
    }

    [Test]
    public async Task Callback_RegisterPageGoogleUser_CreatesVerifiedUserAndSignsIn()
    {
        ApplicationUser createdUser = null!;
        var registrationIntentToken = _webGoogleAuthTokenService.ProtectRegistrationIntent(
            new WebGoogleRegistrationIntentTokenPayload(true, true, true, true, "/"));
        SetupExternalLogin("new-register@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.FindByEmailAsync("new-register@example.com"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>()))
            .Callback<ApplicationUser>(user =>
            {
                createdUser = user;
                user.Id = 42;
            })
            .ReturnsAsync(IdentityResult.Success);
        _mockUserManager.Setup(x => x.GetLoginsAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(new List<UserLoginInfo>());
        _mockUserManager.Setup(x => x.AddLoginAsync(It.IsAny<ApplicationUser>(), It.IsAny<UserLoginInfo>()))
            .ReturnsAsync(IdentityResult.Success);

        var result = await _controller.Callback(registrationIntentToken);

        Assert.That(result, Is.InstanceOf<LocalRedirectResult>());
        Assert.That(createdUser, Is.Not.Null);
        Assert.That(createdUser.Email, Is.EqualTo("new-register@example.com"));
        Assert.That(createdUser.EmailConfirmed, Is.True);
        Assert.That(createdUser.ReceiveNewSongEmails, Is.True);
        _mockAdminNotificationService.Verify(x => x.NotifyUserRegisteredAsync("new-register@example.com"), Times.Once);
        _mockAdminNotificationService.Verify(x => x.NotifyEmailConfirmedAsync("new-register@example.com"), Times.Once);
        _mockSignInManager.Verify(x => x.SignInAsync(createdUser, true, ExternalLoginProviders.Google), Times.Once);
    }

    [Test]
    public async Task Callback_RegisterPageGoogleUser_WithCreatorReturnUrl_RecordsCreatorAccountRegistered()
    {
        ApplicationUser createdUser = null!;
        var registrationIntentToken = _webGoogleAuthTokenService.ProtectRegistrationIntent(
            new WebGoogleRegistrationIntentTokenPayload(true, true, true, true, AppPageRoutes.CreatorSettings));
        SetupExternalLogin("new-creator-register@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.FindByEmailAsync("new-creator-register@example.com"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>()))
            .Callback<ApplicationUser>(user =>
            {
                createdUser = user;
                user.Id = 43;
            })
            .ReturnsAsync(IdentityResult.Success);
        _mockUserManager.Setup(x => x.GetLoginsAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(new List<UserLoginInfo>());
        _mockUserManager.Setup(x => x.AddLoginAsync(It.IsAny<ApplicationUser>(), It.IsAny<UserLoginInfo>()))
            .ReturnsAsync(IdentityResult.Success);

        var result = await _controller.Callback(registrationIntentToken);

        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Is.EqualTo(AppPageRoutes.CreatorSettings));
        _mockAdminNotificationService.Verify(x => x.RecordUserHistoryAsync(
            createdUser.Id,
            createdUser.Email,
            UserHistoryEventTypes.CreatorAccountRegistered,
            It.IsAny<string>(),
            null,
            null), Times.Once);
    }

    [Test]
    public async Task Register_MissingPolicyAcceptance_DoesNotCreateUser()
    {
        var payload = new MobilePendingExternalRegistrationTokenPayload(
            ExternalLoginProviders.Google,
            "google-provider-key",
            "new@example.com",
            "New User");
        var pendingToken = _mobileExternalAuthTokenService.ProtectPendingRegistration(payload);

        var result = await _controller.Register(
            pendingToken,
            acceptTermsOfUse: true,
            acceptPrivacyPolicy: false,
            acceptRefundPolicy: true,
            receiveNewSongEmails: false,
            email: "new@example.com");

        Assert.That(result, Is.InstanceOf<RedirectResult>());
        _mockUserManager.Verify(x => x.CreateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Test]
    public async Task Register_ExistingPasswordUserByGoogleEmail_LinksPromotesAndSignsIn()
    {
        var user = CreateUser(email: "existing-register@example.com", emailConfirmed: false);
        var payload = new MobilePendingExternalRegistrationTokenPayload(
            ExternalLoginProviders.Google,
            "google-provider-key",
            user.Email!,
            "Existing User");
        var pendingToken = _mobileExternalAuthTokenService.ProtectPendingRegistration(payload);

        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.FindByEmailAsync(user.Email!))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GetLoginsAsync(user))
            .ReturnsAsync(new List<UserLoginInfo>());
        _mockUserManager.Setup(x => x.AddLoginAsync(user, It.IsAny<UserLoginInfo>()))
            .ReturnsAsync(IdentityResult.Success);

        var result = await _controller.Register(
            pendingToken,
            acceptTermsOfUse: true,
            acceptPrivacyPolicy: true,
            acceptRefundPolicy: true,
            receiveNewSongEmails: false,
            email: user.Email!);

        Assert.That(result, Is.InstanceOf<LocalRedirectResult>());
        _mockUserManager.Verify(x => x.CreateAsync(It.IsAny<ApplicationUser>()), Times.Never);
        _mockUserManager.Verify(x => x.AddLoginAsync(user, It.IsAny<UserLoginInfo>()), Times.Once);
        _mockAuthService.Verify(x => x.MarkEmailVerifiedAndPromoteRoleAsync(user), Times.Once);
        _mockSignInManager.Verify(x => x.SignInAsync(user, true, ExternalLoginProviders.Google), Times.Once);
    }

    [Test]
    public async Task Callback_SuspendedExistingUser_DoesNotLinkOrSignIn()
    {
        var user = CreateUser(email: "suspended@example.com", emailConfirmed: true);
        user.IsSuspended = true;
        SetupExternalLogin("suspended@example.com", "google-provider-key");
        _mockUserManager.Setup(x => x.FindByLoginAsync(ExternalLoginProviders.Google, "google-provider-key"))
            .ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.FindByEmailAsync(user.Email!))
            .ReturnsAsync(user);

        var result = await _controller.Callback();

        var redirect = result as RedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Does.StartWith("/login"));
        _mockUserManager.Verify(x => x.AddLoginAsync(It.IsAny<ApplicationUser>(), It.IsAny<UserLoginInfo>()), Times.Never);
        _mockSignInManager.Verify(x => x.SignInAsync(It.IsAny<ApplicationUser>(), true, ExternalLoginProviders.Google), Times.Never);
    }

    private WebGoogleAuthController CreateController()
    {
        var controller = new WebGoogleAuthController(
            _mockConfiguration.Object,
            _mockSignInManager.Object,
            _mockUserManager.Object,
            _mockAuthService.Object,
            _mobileExternalAuthTokenService,
            _webGoogleAuthTokenService,
            _mockAccountEmailService.Object,
            _mockAdminNotificationService.Object,
            _mockLogger.Object);

        var services = new ServiceCollection()
            .AddSingleton(_mockAspNetAuthenticationService.Object)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("streamtunes.net");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    private void SetupExternalLogin(string email, string providerKey)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Email, email),
            new Claim(GoogleClaimTypes.EmailVerified, bool.TrueString),
            new Claim(ClaimTypes.Name, "Google User")
        }, ExternalLoginProviders.Google));
        var externalLogin = new ExternalLoginInfo(
            principal,
            ExternalLoginProviders.Google,
            providerKey,
            ExternalLoginProviders.Google);

        _mockSignInManager.Setup(x => x.GetExternalLoginInfoAsync(It.IsAny<string>()))
            .ReturnsAsync(externalLogin);
    }

    private static ApplicationUser CreateUser(int id = 100, string email = "user@example.com", bool emailConfirmed = false)
    {
        return new ApplicationUser
        {
            Id = id,
            Email = email,
            UserName = email,
            EmailConfirmed = emailConfirmed
        };
    }

    private static IDataProtectionProvider CreateDataProtectionProvider()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        directory.Create();
        return DataProtectionProvider.Create(directory);
    }
}
