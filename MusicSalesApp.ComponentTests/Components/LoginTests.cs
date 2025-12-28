using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class LoginTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        
        // Mock IWebHostEnvironment for Login component
        var mockEnvironment = new Mock<IWebHostEnvironment>();
        mockEnvironment.Setup(e => e.EnvironmentName).Returns("Development");
        TestContext.Services.AddSingleton<IWebHostEnvironment>(mockEnvironment.Object);
    }

    [Test]
    public void Login_RendersCorrectly()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert - SfCard renders the title in CardHeader
        Assert.That(cut.Markup, Does.Contain("Login"));
    }

    [Test]
    public void Login_HasUsernameField()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        var usernameInput = cut.Find("#username");
        Assert.That(usernameInput, Is.Not.Null);
        Assert.That(usernameInput.GetAttribute("type"), Is.EqualTo("text"));
        Assert.That(usernameInput.GetAttribute("required"), Is.Not.Null);
    }

    [Test]
    public void Login_HasPasswordField()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        var passwordInput = cut.Find("#password");
        Assert.That(passwordInput, Is.Not.Null);
        Assert.That(passwordInput.GetAttribute("type"), Is.EqualTo("password"));
        Assert.That(passwordInput.GetAttribute("required"), Is.Not.Null);
    }

    [Test]
    public void Login_HasLoginButton()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert - SfButton renders with e-btn class and contains "Login"
        Assert.That(cut.Markup, Does.Contain("Login"));
        Assert.That(cut.Markup, Does.Contain("e-btn"));
    }

    [Test]
    public void Login_HasAntiforgeryToken()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        var tokenInput = cut.Find("input[name='__RequestVerificationToken']");
        Assert.That(tokenInput, Is.Not.Null);
        Assert.That(tokenInput.GetAttribute("type"), Is.EqualTo("hidden"));
    }

    [Test]
    public void Login_InDevelopment_ShowsHintMessage()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("admin@app.com"));
        Assert.That(cut.Markup, Does.Contain("user@app.com"));
        Assert.That(cut.Markup, Does.Contain("Password_123"));
    }

    [Test]
    public void Login_HasCorrectFormAction()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        var form = cut.Find("form");
        Assert.That(form.GetAttribute("action"), Is.EqualTo("/account/login"));
        Assert.That(form.GetAttribute("method"), Is.EqualTo("post"));
    }

    [Test]
    public void Login_HasPasskeyLoginButton()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Login with Passkey"));
    }

    [Test]
    public void Login_HasPasswordLoginButton()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Login with Password"));
    }

    [Test]
    public void Login_HasForgotPasswordLink()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Forgot Password?"));
        Assert.That(cut.Markup, Does.Contain("href=\"/forgot-password\""));
    }
}