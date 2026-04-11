using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Auth;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class ValidateEmailTests : BUnitTestBase
{
    private void SetupAuthenticatedUserWithEmail(string email)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Name, email),
            new Claim(ClaimTypes.Email, email)
        };

        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(claimsPrincipal);
        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        // Use bUnit's authorization context with the same claims
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized(email);
        authContext.SetClaims(claims.ToArray());
    }

    [Test]
    public async Task UnverifiedUser_AutoSendsVerificationEmail()
    {
        SetupAuthenticatedUserWithEmail("test@test.com");
        MockAuthService.Setup(x => x.IsEmailVerifiedAsync("test@test.com"))
            .ReturnsAsync(false);
        MockAuthService.Setup(x => x.CanResendVerificationEmailAsync("test@test.com"))
            .ReturnsAsync((true, 0));
        MockAuthService.Setup(x => x.SendVerificationEmailAsync("test@test.com", It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));

        var cut = TestContext.Render<ValidateEmail>();
        await Task.Delay(100);
        cut.Render();

        MockAuthService.Verify(
            x => x.SendVerificationEmailAsync("test@test.com", It.IsAny<string>()),
            Times.Once);
        Assert.That(cut.Markup, Does.Contain("verification email has been sent"));
    }

    [Test]
    public async Task UnverifiedUser_WithCooldown_DoesNotAutoSend()
    {
        SetupAuthenticatedUserWithEmail("test@test.com");
        MockAuthService.Setup(x => x.IsEmailVerifiedAsync("test@test.com"))
            .ReturnsAsync(false);
        MockAuthService.Setup(x => x.CanResendVerificationEmailAsync("test@test.com"))
            .ReturnsAsync((false, 300));

        var cut = TestContext.Render<ValidateEmail>();
        await Task.Delay(100);
        cut.Render();

        MockAuthService.Verify(
            x => x.SendVerificationEmailAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task AlreadyVerifiedUser_ShowsVerifiedMessage()
    {
        SetupAuthenticatedUserWithEmail("test@test.com");
        MockAuthService.Setup(x => x.IsEmailVerifiedAsync("test@test.com"))
            .ReturnsAsync(true);

        var cut = TestContext.Render<ValidateEmail>();
        await Task.Delay(100);
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("already verified"));
        MockAuthService.Verify(
            x => x.SendVerificationEmailAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task UnverifiedUser_ShowsChangeEmailSection()
    {
        SetupAuthenticatedUserWithEmail("test@test.com");
        MockAuthService.Setup(x => x.IsEmailVerifiedAsync("test@test.com"))
            .ReturnsAsync(false);
        MockAuthService.Setup(x => x.CanResendVerificationEmailAsync("test@test.com"))
            .ReturnsAsync((true, 0));
        MockAuthService.Setup(x => x.SendVerificationEmailAsync("test@test.com", It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));

        var cut = TestContext.Render<ValidateEmail>();
        await Task.Delay(100);
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("Change Email Address"));
    }

    [Test]
    public async Task UnverifiedUser_ShowsResendButton()
    {
        SetupAuthenticatedUserWithEmail("test@test.com");
        MockAuthService.Setup(x => x.IsEmailVerifiedAsync("test@test.com"))
            .ReturnsAsync(false);
        MockAuthService.Setup(x => x.CanResendVerificationEmailAsync("test@test.com"))
            .ReturnsAsync((true, 0));
        MockAuthService.Setup(x => x.SendVerificationEmailAsync("test@test.com", It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));

        var cut = TestContext.Render<ValidateEmail>();
        await Task.Delay(100);
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("Resend Verification Email"));
    }
}
