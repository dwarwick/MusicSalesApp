using Bunit;
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;
using Moq;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class VerifyEmailTests : BUnitTestBase
{
    [Test]
    public void VerifyEmail_ShowsError_WhenMissingParameters()
    {
        // Navigate to the page without query parameters
        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/verify-email");

        var cut = TestContext.Render<VerifyEmail>();
        
        Assert.That(cut.Markup, Does.Contain("Verification Failed"));
        Assert.That(cut.Markup, Does.Contain("Invalid verification link"));
    }

    [Test]
    public async Task VerifyEmail_ShowsSuccess_WhenVerificationSucceeds()
    {
        MockAuthService.Setup(a => a.VerifyEmailAsync("1", "test-token"))
            .ReturnsAsync((true, string.Empty));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/verify-email?userId=1&token=test-token");

        var cut = TestContext.Render<VerifyEmail>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        Assert.That(cut.Markup, Does.Contain("Email Verified"));
        Assert.That(cut.Markup, Does.Contain("full access"));
    }

    [Test]
    public async Task VerifyEmail_ShowsError_WhenVerificationFails()
    {
        MockAuthService.Setup(a => a.VerifyEmailAsync("1", "expired-token"))
            .ReturnsAsync((false, "The link has expired"));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/verify-email?userId=1&token=expired-token");

        var cut = TestContext.Render<VerifyEmail>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        Assert.That(cut.Markup, Does.Contain("Verification Failed"));
        Assert.That(cut.Markup, Does.Contain("The link has expired"));
    }

    [Test]
    public async Task VerifyEmail_HasLoginLink_WhenVerificationSucceeds()
    {
        MockAuthService.Setup(a => a.VerifyEmailAsync("1", "test-token"))
            .ReturnsAsync((true, string.Empty));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/verify-email?userId=1&token=test-token");

        var cut = TestContext.Render<VerifyEmail>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        // SfButton renders with e-btn class and contains "Login"
        Assert.That(cut.Markup, Does.Contain("Go to Login"));
        Assert.That(cut.Markup, Does.Contain("e-btn"));
    }

    [Test]
    public async Task VerifyEmail_HasRegisterLink_WhenVerificationFails()
    {
        MockAuthService.Setup(a => a.VerifyEmailAsync("1", "bad-token"))
            .ReturnsAsync((false, "Token expired"));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/verify-email?userId=1&token=bad-token");

        var cut = TestContext.Render<VerifyEmail>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        // SfButton renders with e-btn class and contains "Register"
        Assert.That(cut.Markup, Does.Contain("Go to Register Page"));
        Assert.That(cut.Markup, Does.Contain("e-btn"));
    }
}
