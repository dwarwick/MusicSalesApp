using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Auth;
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
        Assert.That(cut.Markup, Does.Contain("Log in"));
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

        // Assert - SfButton renders with e-btn class and carries the submit label
        Assert.That(cut.Markup, Does.Contain("Log in"));
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
        Assert.That(cut.Markup, Does.Contain("Log in with a passkey"));
    }

    [Test]
    public void Login_HasGoogleLoginButton()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Continue with Google"));
        Assert.That(cut.Markup, Does.Contain("google_logo.svg"));
    }

    [Test]
    public void Login_HasPasswordLoginButton()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Log in"));
    }

    [Test]
    public void Login_HasForgotPasswordLink()
    {
        // Act
        var cut = TestContext.Render<Login>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Forgot password?"));
        Assert.That(cut.Markup, Does.Contain("href=\"/forgot-password\""));
    }

    [Test]
    public void Login_ReturnUrlQuery_PopulatesPasswordLoginHiddenField()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/login?{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings");

        var cut = TestContext.Render<Login>();

        var returnUrlInput = cut.Find($"input[name='{ExternalAuthFormFields.ReturnUrl}']");
        Assert.That(returnUrlInput.GetAttribute("value"), Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public void Login_ContinueWithGoogle_UsesReturnUrlQuery()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/login?{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings");
        var cut = TestContext.Render<Login>();

        var googleButton = cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Continue with Google"));
        googleButton.Click();

        Assert.That(navigationManager.Uri, Does.EndWith(
            $"{GoogleAuthRoutes.WebStartPath}" +
            $"?{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings" +
            $"&{ExternalAuthFormFields.RememberMe}=true"));
    }

    [Test]
    public void Login_HasKeepMeSignedInField_TickedByDefault()
    {
        var cut = TestContext.Render<Login>();

        var rememberMeInput = cut.Find($"input[name='{ExternalAuthFormFields.RememberMe}']");
        Assert.Multiple(() =>
        {
            Assert.That(rememberMeInput.GetAttribute("type"), Is.EqualTo("hidden"));
            // Default ticked, so an existing user who ignores the box keeps the
            // persistent cookie they get today.
            Assert.That(rememberMeInput.GetAttribute("value"), Is.EqualTo("true"));
            Assert.That(cut.Markup, Does.Contain("Keep me signed in on this device"));
        });
    }

    [Test]
    public void Login_KeepMeSignedInUnticked_PostsFalse()
    {
        var cut = TestContext.Render<Login>();

        cut.Find("input[type='checkbox']").Click();

        var rememberMeInput = cut.Find($"input[name='{ExternalAuthFormFields.RememberMe}']");
        Assert.That(rememberMeInput.GetAttribute("value"), Is.EqualTo("false"));
    }

    [Test]
    public void Login_KeepMeSignedInUnticked_CarriesThroughToGoogle()
    {
        var cut = TestContext.Render<Login>();
        cut.Find("input[type='checkbox']").Click();

        var googleButton = cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Continue with Google"));
        googleButton.Click();

        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        Assert.That(navigationManager.Uri, Does.EndWith($"&{ExternalAuthFormFields.RememberMe}=false"));
    }

    [Test]
    public void Login_PasskeyLogin_PassesRememberMeToJs()
    {
        var invocation = TestContext.JSInterop.SetupVoid(
            "passkeyHelper.loginWithPasskey",
            _ => true);

        var cut = TestContext.Render<Login>();
        cut.Find("#username").Change("dave.warwick");
        cut.Find("input[type='checkbox']").Click();

        var passkeyButton = cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Log in with a passkey"));
        passkeyButton.Click();

        var args = invocation.Invocations.Single().Arguments;
        Assert.Multiple(() =>
        {
            Assert.That(args[0], Is.EqualTo("dave.warwick"));
            Assert.That(args[1], Is.EqualTo(false));
        });
    }

    [Test]
    public void Login_HasBrandPanel()
    {
        var cut = TestContext.Render<Login>();

        var panel = cut.Find(".auth-panel");
        var logo = cut.Find(".auth-panel-logo");

        Assert.Multiple(() =>
        {
            Assert.That(panel, Is.Not.Null);
            // The transparent mark specifically. logo-dark-small.png bakes its background to
            // #181c1f - the dark app-bar colour - so on the navy panel it is a black box.
            Assert.That(logo.GetAttribute("src"), Does.Contain("logo-mark"));
            Assert.That(logo.GetAttribute("src"), Does.Not.Contain("logo-dark-small"));
            Assert.That(cut.Markup, Does.Contain("Welcome back."));
        });
    }

    [Test]
    public void Login_LinksToRegister_PreservingTheReturnUrl()
    {
        // Register has always linked to Login; this is the other half of that pair. The
        // returnUrl has to survive the hop or a creator sent here from /CreatorSettings loses
        // the destination by choosing to sign up instead.
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/login?{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings");

        var cut = TestContext.Render<Login>();

        var registerLink = cut.FindAll("a").Single(a => a.TextContent.Trim() == "Register");
        Assert.That(
            registerLink.GetAttribute("href"),
            Is.EqualTo($"{AppPageRoutes.Register}?{ExternalAuthFormFields.ReturnUrl}={Uri.EscapeDataString(AppPageRoutes.CreatorSettings)}"));
    }
}
