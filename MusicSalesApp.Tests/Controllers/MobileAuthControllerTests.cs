using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MobileAuthControllerTests
{
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IAuthenticationService> _mockAuthService;
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IEmailService> _mockEmailService;
    private Mock<IAccountEmailService> _mockAccountEmailService;
    private Mock<IAdminNotificationService> _mockAdminNotificationService;
    private Mock<ILogger<MobileAuthController>> _mockLogger;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;
    private SqliteConnection _connection;
    private MobileAuthController _controller;

    private const int TestUserId = 100;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(_contextOptions);
        _context.Database.EnsureCreated();

        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(c => c["Jwt:SecretKey"]).Returns("TestSecretKeyThatIsAtLeast32CharactersLong!");
        _mockConfiguration.Setup(c => c["Jwt:Issuer"]).Returns("TestIssuer");
        _mockConfiguration.Setup(c => c["Jwt:Audience"]).Returns("TestAudience");

        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(s => s.Value).Returns("60");
        _mockConfiguration.Setup(c => c.GetSection("Jwt:ExpireMinutes")).Returns(configSection.Object);

        var store = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _mockAuthService = new Mock<IAuthenticationService>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockEmailService = new Mock<IEmailService>();
        _mockAccountEmailService = new Mock<IAccountEmailService>();
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();
        _mockLogger = new Mock<ILogger<MobileAuthController>>();

        // Default setups
        _mockEmailService.Setup(x => x.GetEmailLogoHtml()).Returns("<div>Logo</div>");
        _mockEmailService.Setup(x => x.SendEmailWithResultAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(EmailResult.Succeeded());
        _mockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(It.IsAny<int>()))
            .ReturnsAsync(false);
        _mockUserManager.Setup(x => x.GetRolesAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(new List<string> { Roles.User });
        _mockAccountEmailService.Setup(x => x.SendAccountCreatedEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _controller = CreateController();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _connection.Dispose();
    }

    private MobileAuthController CreateController()
    {
        var controller = new MobileAuthController(
            _mockConfiguration.Object,
            _mockUserManager.Object,
            _mockAuthService.Object,
            _mockSubscriptionService.Object,
            _mockEmailService.Object,
            _mockAccountEmailService.Object,
            _mockAdminNotificationService.Object,
            _context,
            _mockLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    private ApplicationUser CreateTestUser(int id = TestUserId, bool emailConfirmed = false)
    {
        return new ApplicationUser
        {
            Id = id,
            UserName = $"user{id}@test.com",
            Email = $"user{id}@test.com",
            EmailConfirmed = emailConfirmed
        };
    }

    private void SeedVerificationCode(int userId, string code, string purpose, DateTime expiresAt)
    {
        _context.MobileVerificationCodes.Add(new MobileVerificationCode
        {
            UserId = userId,
            Code = code,
            Purpose = purpose,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        });
        _context.SaveChanges();
    }

    #region Register Tests

    [Test]
    public async Task Register_EmptyEmail_ReturnsBadRequest()
    {
        var result = await _controller.Register(new MobileRegisterRequest { Email = "", Password = "Pass123!" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Register_EmptyPassword_ReturnsBadRequest()
    {
        var result = await _controller.Register(new MobileRegisterRequest { Email = "test@test.com", Password = "" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Register_AuthServiceFails_ReturnsBadRequest()
    {
        _mockAuthService.Setup(x => x.RegisterAsync("test@test.com", "Pass123!"))
            .ReturnsAsync((false, "Email already exists"));

        var result = await _controller.Register(new MobileRegisterRequest { Email = "test@test.com", Password = "Pass123!" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Register_Success_ReturnsOkWithUserId()
    {
        var user = CreateTestUser();
        _mockAuthService.Setup(x => x.RegisterAsync("user100@test.com", "Pass123!"))
            .ReturnsAsync((true, string.Empty));
        _mockUserManager.Setup(x => x.FindByEmailAsync("user100@test.com"))
            .ReturnsAsync(user);

        var result = await _controller.Register(new MobileRegisterRequest { Email = "user100@test.com", Password = "Pass123!" });

        var okResult = result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
    }

    [Test]
    public async Task Register_Success_SendsAdminNotification()
    {
        var user = CreateTestUser();
        _mockAuthService.Setup(x => x.RegisterAsync("user100@test.com", "Pass123!"))
            .ReturnsAsync((true, string.Empty));
        _mockUserManager.Setup(x => x.FindByEmailAsync("user100@test.com"))
            .ReturnsAsync(user);

        await _controller.Register(new MobileRegisterRequest { Email = "user100@test.com", Password = "Pass123!" });

        _mockAdminNotificationService.Verify(
            x => x.NotifyUserRegisteredAsync("user100@test.com"), Times.Once);
    }

    [Test]
    public async Task Register_AdminNotificationFails_StillReturnsOk()
    {
        var user = CreateTestUser();
        _mockAuthService.Setup(x => x.RegisterAsync("user100@test.com", "Pass123!"))
            .ReturnsAsync((true, string.Empty));
        _mockUserManager.Setup(x => x.FindByEmailAsync("user100@test.com"))
            .ReturnsAsync(user);
        _mockAdminNotificationService.Setup(x => x.NotifyUserRegisteredAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("SMTP failure"));

        var result = await _controller.Register(new MobileRegisterRequest { Email = "user100@test.com", Password = "Pass123!" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    #endregion

    #region VerifyCode — Role Upgrade Tests

    [Test]
    public async Task VerifyCode_ValidCode_ConfirmsEmailAndUpgradesRole()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((true, string.Empty));

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockAuthService.Verify(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"), Times.Once,
            "Should call ConfirmEmailAndPromoteRoleAsync which handles the NonValidatedUser → User role promotion");
    }

    [Test]
    public async Task VerifyCode_ValidCode_DoesNotSetEmailConfirmedManually()
    {
        // This test ensures we don't set EmailConfirmed=true before calling ConfirmEmailAndPromoteRoleAsync,
        // which would cause it to short-circuit and skip the role upgrade.
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((true, string.Empty));

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        // UpdateAsync should NOT be called before ConfirmEmailAndPromoteRoleAsync — it handles it
        _mockUserManager.Verify(x => x.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never,
            "Should not manually set EmailConfirmed before ConfirmEmailAndPromoteRoleAsync");
    }

    [Test]
    public async Task VerifyCode_AlreadyConfirmed_DoesNotCallConfirmEmailAndPromoteRoleAsync()
    {
        var user = CreateTestUser(emailConfirmed: true);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        _mockAuthService.Verify(x => x.ConfirmEmailAndPromoteRoleAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "Should not call ConfirmEmailAndPromoteRoleAsync when email is already confirmed");
    }

    #endregion

    #region VerifyCode — Welcome Email & Admin Notification Tests

    [Test]
    public async Task VerifyCode_ValidCode_SendsWelcomeEmail()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((true, string.Empty));

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        _mockAccountEmailService.Verify(
            x => x.SendAccountCreatedEmailAsync(user.Email!, user.UserName!, It.IsAny<string>()),
            Times.Once, "Should send welcome email after verification");
    }

    [Test]
    public async Task VerifyCode_ValidCode_SendsAdminEmailConfirmedNotification()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((true, string.Empty));

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        _mockAdminNotificationService.Verify(
            x => x.NotifyEmailConfirmedAsync(user.Email!),
            Times.Once, "Should send admin notification when email is confirmed");
    }

    [Test]
    public async Task VerifyCode_WelcomeEmailFails_StillReturnsOk()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((true, string.Empty));
        _mockAccountEmailService.Setup(x => x.SendAccountCreatedEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("SMTP error"));

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), "Verification should still succeed if welcome email fails");
    }

    [Test]
    public async Task VerifyCode_AdminNotificationFails_StillReturnsOk()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((true, string.Empty));
        _mockAdminNotificationService.Setup(x => x.NotifyEmailConfirmedAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("SMTP error"));

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), "Verification should still succeed if admin notification fails");
    }

    [Test]
    public async Task VerifyCode_AlreadyConfirmed_DoesNotSendEmails()
    {
        var user = CreateTestUser(emailConfirmed: true);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        _mockAccountEmailService.Verify(
            x => x.SendAccountCreatedEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never, "Should not send welcome email when already confirmed");
        _mockAdminNotificationService.Verify(
            x => x.NotifyEmailConfirmedAsync(It.IsAny<string>()),
            Times.Never, "Should not send admin notification when already confirmed");
    }

    #endregion

    #region VerifyCode — Wrong Code Tests

    [Test]
    public async Task VerifyCode_WrongCode_ReturnsBadRequest()
    {
        var user = CreateTestUser(emailConfirmed: false);
        // Seed a valid code but user enters a different one
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "999999" });

        var badResult = result as BadRequestObjectResult;
        Assert.That(badResult, Is.Not.Null);
    }

    [Test]
    public async Task VerifyCode_WrongCode_DoesNotConfirmEmail()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "999999" });

        _mockAuthService.Verify(x => x.ConfirmEmailAndPromoteRoleAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "Should not call ConfirmEmailAndPromoteRoleAsync for wrong code");
    }

    [Test]
    public async Task VerifyCode_NoCodeExists_ReturnsBadRequest()
    {
        var user = CreateTestUser(emailConfirmed: false);
        // No code seeded at all
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region VerifyCode — Expired Code Tests

    [Test]
    public async Task VerifyCode_ExpiredCode_ReturnsBadRequest()
    {
        var user = CreateTestUser(emailConfirmed: false);
        // Seed a code that expired 5 minutes ago
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(-5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        var badResult = result as BadRequestObjectResult;
        Assert.That(badResult, Is.Not.Null);
    }

    [Test]
    public async Task VerifyCode_ExpiredCode_DoesNotConfirmEmail()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(-5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        _mockAuthService.Verify(x => x.ConfirmEmailAndPromoteRoleAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "Should not call ConfirmEmailAndPromoteRoleAsync for expired code");
    }

    [Test]
    public async Task VerifyCode_ExpiredCode_ReturnsErrorMessage()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(-5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        var badResult = result as BadRequestObjectResult;
        Assert.That(badResult, Is.Not.Null);
        var value = badResult!.Value;
        var messageProperty = value!.GetType().GetProperty("message");
        var message = messageProperty?.GetValue(value) as string;
        Assert.That(message, Does.Contain("Invalid or expired"));
    }

    #endregion

    #region VerifyCode — Validation Tests

    [Test]
    public async Task VerifyCode_ZeroUserId_ReturnsBadRequest()
    {
        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = 0, Code = "123456" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task VerifyCode_EmptyCode_ReturnsBadRequest()
    {
        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = 1, Code = "" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task VerifyCode_InvalidUser_ReturnsBadRequest()
    {
        _mockUserManager.Setup(x => x.FindByIdAsync("999")).ReturnsAsync((ApplicationUser)null!);

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = 999, Code = "123456" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region VerifyCode — Code Consumption Tests

    [Test]
    public async Task VerifyCode_ValidCode_RemovesCodeAfterUse()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((true, string.Empty));

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        // Code should be consumed
        var remainingCodes = await _context.MobileVerificationCodes
            .Where(c => c.UserId == user.Id)
            .ToListAsync();
        Assert.That(remainingCodes, Is.Empty, "Verification code should be removed after successful use");
    }

    [Test]
    public async Task VerifyCode_WrongCode_DoesNotRemoveValidCode()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "999999" });

        // Valid code should still exist
        var remainingCodes = await _context.MobileVerificationCodes
            .Where(c => c.UserId == user.Id)
            .ToListAsync();
        Assert.That(remainingCodes, Has.Count.EqualTo(1), "Valid code should not be removed when wrong code is entered");
    }

    #endregion

    #region VerifyCode — Role Promotion Failure Tests

    [Test]
    public async Task VerifyCode_RolePromotionFails_StillReturnsOk()
    {
        var user = CreateTestUser(emailConfirmed: false);
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(5));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("fake-token");
        _mockAuthService.Setup(x => x.ConfirmEmailAndPromoteRoleAsync(user.Id.ToString(), "fake-token"))
            .ReturnsAsync((false, "Unexpected error"));

        var result = await _controller.VerifyCode(new MobileVerifyCodeRequest { UserId = user.Id, Code = "123456" });

        // Should still return OK — the code was valid, so the user gets logged in
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    #endregion

    #region Login Tests

    [Test]
    public async Task Login_EmptyEmail_ReturnsBadRequest()
    {
        var result = await _controller.Login(new MobileLoginRequest { Email = "", Password = "Pass123!" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Login_EmptyPassword_ReturnsBadRequest()
    {
        var result = await _controller.Login(new MobileLoginRequest { Email = "test@test.com", Password = "" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Login_SuspendedUser_ReturnsUnauthorized()
    {
        var user = CreateTestUser(emailConfirmed: true);
        user.IsSuspended = true;
        _mockUserManager.Setup(x => x.FindByEmailAsync("user100@test.com")).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.FindByNameAsync("user100@test.com")).ReturnsAsync(user);

        var result = await _controller.Login(new MobileLoginRequest { Email = "user100@test.com", Password = "Pass123!" });

        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    #endregion

    #region ResendCode Tests

    [Test]
    public async Task ResendCode_InvalidUserId_ReturnsBadRequest()
    {
        var result = await _controller.ResendCode(new MobileResendCodeRequest { UserId = 0 });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ResendCode_UnknownUser_ReturnsBadRequest()
    {
        _mockUserManager.Setup(x => x.FindByIdAsync("999")).ReturnsAsync((ApplicationUser)null!);

        var result = await _controller.ResendCode(new MobileResendCodeRequest { UserId = 999 });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ResendCode_WithinCooldown_ReturnsBadRequest()
    {
        var user = CreateTestUser(emailConfirmed: false);
        // Seed a code that was just created (within cooldown)
        SeedVerificationCode(user.Id, "123456", MobileVerificationPurpose.EmailVerification,
            DateTime.UtcNow.AddMinutes(10));

        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var result = await _controller.ResendCode(new MobileResendCodeRequest { UserId = user.Id });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region ChangeEmail Tests

    [Test]
    public async Task ChangeEmail_InvalidUserId_ReturnsBadRequest()
    {
        var result = await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = 0, NewEmail = "new@test.com" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ChangeEmail_EmptyNewEmail_ReturnsBadRequest()
    {
        var result = await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = 1, NewEmail = "" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ChangeEmail_UnknownUser_ReturnsBadRequest()
    {
        _mockUserManager.Setup(x => x.FindByIdAsync("999")).ReturnsAsync((ApplicationUser)null!);

        var result = await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = 999, NewEmail = "new@test.com" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ChangeEmail_AlreadyConfirmed_ReturnsBadRequest()
    {
        var user = CreateTestUser(emailConfirmed: true);
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);

        var result = await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = user.Id, NewEmail = "new@test.com" });

        var badRequest = result as BadRequestObjectResult;
        Assert.That(badRequest, Is.Not.Null);
    }

    [Test]
    public async Task ChangeEmail_EmailTakenByOtherUser_ReturnsBadRequest()
    {
        var user = CreateTestUser(id: 100, emailConfirmed: false);
        var otherUser = CreateTestUser(id: 200, emailConfirmed: true);
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.FindByEmailAsync("taken@test.com")).ReturnsAsync(otherUser);

        var result = await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = user.Id, NewEmail = "taken@test.com" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ChangeEmail_Success_ReturnsOk()
    {
        var user = CreateTestUser(emailConfirmed: false);
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.FindByEmailAsync("new@test.com")).ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);

        var result = await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = user.Id, NewEmail = "new@test.com" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task ChangeEmail_Success_UpdatesUserEmail()
    {
        var user = CreateTestUser(emailConfirmed: false);
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.FindByEmailAsync("new@test.com")).ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);

        await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = user.Id, NewEmail = "new@test.com" });

        Assert.That(user.Email, Is.EqualTo("new@test.com"));
        Assert.That(user.UserName, Is.EqualTo("new@test.com"));
    }

    [Test]
    public async Task ChangeEmail_Success_SendsVerificationCode()
    {
        var user = CreateTestUser(emailConfirmed: false);
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        _mockUserManager.Setup(x => x.FindByEmailAsync("new@test.com")).ReturnsAsync((ApplicationUser)null!);
        _mockUserManager.Setup(x => x.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);

        await _controller.ChangeEmail(new MobileChangeEmailRequest { UserId = user.Id, NewEmail = "new@test.com" });

        _mockEmailService.Verify(x => x.SendEmailWithResultAsync("new@test.com", "Verify Your Email", It.IsAny<string>()), Times.Once);
    }

    #endregion
}
