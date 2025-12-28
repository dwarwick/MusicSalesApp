using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class ForgotPasswordTests : BUnitTestBase
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
    public void ForgotPassword_RendersCorrectly()
    {
        // Act
        var cut = TestContext.Render<ForgotPassword>();

        // Assert - SfCard renders the title in CardHeader
        Assert.That(cut.Markup, Does.Contain("Forgot Password"));
    }

    [Test]
    public void ForgotPassword_HasEmailField()
    {
        // Act
        var cut = TestContext.Render<ForgotPassword>();

        // Assert
        var emailInput = cut.Find("#email");
        Assert.That(emailInput, Is.Not.Null);
        Assert.That(emailInput.GetAttribute("required"), Is.Not.Null);
    }

    [Test]
    public void ForgotPassword_HasSubmitButton()
    {
        // Act
        var cut = TestContext.Render<ForgotPassword>();

        // Assert - SfButton renders with e-btn class
        Assert.That(cut.Markup, Does.Contain("Send Reset Link"));
        Assert.That(cut.Markup, Does.Contain("e-btn"));
    }

    [Test]
    public void ForgotPassword_HasBackToLoginLink()
    {
        // Act
        var cut = TestContext.Render<ForgotPassword>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Back to Login"));
        Assert.That(cut.Markup, Does.Contain("href=\"/login\""));
    }

    [Test]
    public void ForgotPassword_HasInstructionalText()
    {
        // Act
        var cut = TestContext.Render<ForgotPassword>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Enter your email address"));
        Assert.That(cut.Markup, Does.Contain("send you a link to reset your password"));
    }

    [Test]
    public async Task ForgotPassword_ShowsSuccessMessage_AfterSubmit()
    {
        // Arrange
        MockAuthService.Setup(a => a.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));

        var cut = TestContext.Render<ForgotPassword>();
        
        // Fill in email
        var emailInput = cut.Find("#email");
        emailInput.Change("test@example.com");

        // Find and submit the form
        var form = cut.Find("form");
        await cut.InvokeAsync(() => form.Submit());
        
        // Wait for component to update
        await Task.Delay(50);
        cut.Render();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Check Your Email"));
        Assert.That(cut.Markup, Does.Contain("10 minutes"));
        Assert.That(cut.Markup, Does.Contain("Return to Login"));
    }

    [Test]
    public async Task ForgotPassword_DoesNotRevealAccountExistence()
    {
        // Arrange - Even for non-existent accounts, we should show success
        MockAuthService.Setup(a => a.SendPasswordResetEmailAsync("nonexistent@example.com", It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));

        var cut = TestContext.Render<ForgotPassword>();
        
        // Fill in email
        var emailInput = cut.Find("#email");
        emailInput.Change("nonexistent@example.com");

        // Find and submit the form
        var form = cut.Find("form");
        await cut.InvokeAsync(() => form.Submit());
        
        // Wait for component to update
        await Task.Delay(50);
        cut.Render();

        // Assert - Should still show success message to not reveal if account exists
        Assert.That(cut.Markup, Does.Contain("Check Your Email"));
        Assert.That(cut.Markup, Does.Not.Contain("not found"));
        Assert.That(cut.Markup, Does.Not.Contain("does not exist"));
    }
}
