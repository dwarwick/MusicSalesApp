using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class ResetPasswordTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        
        // Mock IWebHostEnvironment for component
        var mockEnvironment = new Mock<IWebHostEnvironment>();
        mockEnvironment.Setup(e => e.EnvironmentName).Returns("Development");
        TestContext.Services.AddSingleton<IWebHostEnvironment>(mockEnvironment.Object);
    }

    [Test]
    public void ResetPassword_ShowsError_WhenMissingParameters()
    {
        // Navigate to the page without query parameters
        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/reset-password");

        var cut = TestContext.Render<ResetPassword>();
        
        Assert.That(cut.Markup, Does.Contain("Invalid or Expired Link"));
        Assert.That(cut.Markup, Does.Contain("Invalid password reset link"));
    }

    [Test]
    public async Task ResetPassword_ShowsPasswordForm_WhenTokenIsValid()
    {
        MockAuthService.Setup(a => a.VerifyPasswordResetTokenAsync("1", "valid-token"))
            .ReturnsAsync((true, string.Empty));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/reset-password?userId=1&token=valid-token");

        var cut = TestContext.Render<ResetPassword>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        Assert.That(cut.Markup, Does.Contain("New Password"));
        Assert.That(cut.Markup, Does.Contain("Confirm Password"));
        Assert.That(cut.Markup, Does.Contain("Reset Password"));
    }

    [Test]
    public async Task ResetPassword_ShowsError_WhenTokenIsInvalid()
    {
        MockAuthService.Setup(a => a.VerifyPasswordResetTokenAsync("1", "invalid-token"))
            .ReturnsAsync((false, "This password reset link has expired or is invalid."));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/reset-password?userId=1&token=invalid-token");

        var cut = TestContext.Render<ResetPassword>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        Assert.That(cut.Markup, Does.Contain("Invalid or Expired Link"));
        Assert.That(cut.Markup, Does.Contain("expired or is invalid"));
        Assert.That(cut.Markup, Does.Contain("Request New Reset Link"));
    }

    [Test]
    public async Task ResetPassword_HasNewResetLinkButton_WhenTokenIsInvalid()
    {
        MockAuthService.Setup(a => a.VerifyPasswordResetTokenAsync("1", "expired-token"))
            .ReturnsAsync((false, "Token expired"));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/reset-password?userId=1&token=expired-token");

        var cut = TestContext.Render<ResetPassword>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        // SfButton renders with e-btn class
        Assert.That(cut.Markup, Does.Contain("Request New Reset Link"));
        Assert.That(cut.Markup, Does.Contain("e-btn"));
    }

    [Test]
    public async Task ResetPassword_ShowsSuccess_WhenPasswordResetSucceeds()
    {
        MockAuthService.Setup(a => a.VerifyPasswordResetTokenAsync("1", "valid-token"))
            .ReturnsAsync((true, string.Empty));
        MockAuthService.Setup(a => a.ResetPasswordAsync("1", "valid-token", "NewPassword123!"))
            .ReturnsAsync((true, string.Empty));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/reset-password?userId=1&token=valid-token");

        var cut = TestContext.Render<ResetPassword>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();

        // Fill in the passwords
        var newPasswordInput = cut.Find("#newPassword");
        var confirmPasswordInput = cut.Find("#confirmPassword");
        newPasswordInput.Change("NewPassword123!");
        confirmPasswordInput.Change("NewPassword123!");

        // Submit the form
        var form = cut.Find("form");
        await cut.InvokeAsync(() => form.Submit());
        
        // Wait for component to update
        await Task.Delay(50);
        cut.Render();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Password Changed!"));
        Assert.That(cut.Markup, Does.Contain("successfully reset"));
        Assert.That(cut.Markup, Does.Contain("Login with New Password"));
    }

    [Test]
    public void ResetPassword_HasBackToLoginLink()
    {
        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/reset-password");

        var cut = TestContext.Render<ResetPassword>();

        Assert.That(cut.Markup, Does.Contain("Back to Login"));
        Assert.That(cut.Markup, Does.Contain("href=\"/login\""));
    }

    [Test]
    public async Task ResetPassword_HasPasswordRequirementsHint_WhenTokenIsValid()
    {
        MockAuthService.Setup(a => a.VerifyPasswordResetTokenAsync("1", "valid-token"))
            .ReturnsAsync((true, string.Empty));

        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/reset-password?userId=1&token=valid-token");

        var cut = TestContext.Render<ResetPassword>();
        
        // Wait for component to finish loading
        await Task.Delay(50);
        cut.Render();
        
        Assert.That(cut.Markup, Does.Contain("at least 8 characters"));
        Assert.That(cut.Markup, Does.Contain("upper, lower, digit, and symbol"));
    }
}
