using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Auth;
using MusicSalesApp.ComponentTests.Testing;
using Moq;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class RegisterTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupRendererInfo();
    }

    [Test]
    public void Register_RendersForm()
    {
        var cut = TestContext.Render<Register>();
        // SfCard renders the title in CardHeader
        Assert.That(cut.Markup, Does.Contain("Create your account"));
        Assert.That(cut.Find("input#email"), Is.Not.Null);
    }

    [Test]
    public void Register_DisplaysPolicyCheckboxes()
    {
        var cut = TestContext.Render<Register>();
        // Verify the legal agreement section is rendered
        Assert.That(cut.Markup, Does.Contain("Terms of Use"));
        Assert.That(cut.Markup, Does.Contain("Privacy Policy"));
        Assert.That(cut.Markup, Does.Contain("Refund Policy"));
        Assert.That(cut.Markup, Does.Contain("legal-agreements"));
    }

    [Test]
    public void Register_HasGoogleRegisterButton()
    {
        var cut = TestContext.Render<Register>();

        Assert.That(cut.Markup, Does.Contain("Continue with Google"));
        Assert.That(cut.Markup, Does.Contain(GoogleAuthRoutes.WebStartPath));
        Assert.That(cut.Markup, Does.Contain("google_logo.svg"));
    }

    [Test]
    public void Register_GoogleRegisterButton_DoesNotRequirePoliciesBeforeChallenge()
    {
        var cut = TestContext.Render<Register>();

        var googleForm = cut.FindAll("form")
            .Single(form => form.GetAttribute("action") == GoogleAuthRoutes.WebStartPath);
        var googleButton = googleForm.QuerySelector("button");

        Assert.That(googleButton, Is.Not.Null);
        Assert.That(googleButton!.HasAttribute("disabled"), Is.False);
    }

    [Test]
    public void Register_DisplaysError_WhenPasswordsDoNotMatch()
    {
        var cut = TestContext.Render<Register>();
        cut.Find("input#email").Change("test@example.com");
        cut.Find("input#password").Change("Password_1!");
        cut.Find("input#confirm").Change("Password_2!");
        cut.Find("form").Submit();
        Assert.That(cut.Markup, Does.Contain("Passwords do not match"));
    }

    [Test]
    public void Register_DisplaysError_WhenTermsNotAccepted()
    {
        var cut = TestContext.Render<Register>();
        cut.Find("input#email").Change("test@example.com");
        cut.Find("input#password").Change("Password_1!");
        cut.Find("input#confirm").Change("Password_1!");
        
        // Submit without checking the checkboxes
        cut.Find("form").Submit();
        
        // Should show error message about accepting policies
        Assert.That(cut.Markup, Does.Contain("accept the Terms of Use, Privacy Policy, and Refund Policy"));
    }

    [Test]
    public void Register_PendingGoogleRegistration_HidesPasswordFieldsAndShowsCompletionButton()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/register?{ExternalAuthFormFields.PendingRegistrationToken}=pending-token&{ExternalAuthFormFields.Email}=google%40example.com");

        var cut = TestContext.Render<Register>();

        Assert.That(cut.Markup, Does.Contain("finish creating your Google account"));
        Assert.That(cut.Markup, Does.Contain("google@example.com"));
        Assert.That(cut.Markup, Does.Contain("Complete Google Sign Up"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("input#password"));
    }

    [Test]
    public void Register_GoogleRegisterForm_PreservesReturnUrl()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/register?{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings");

        var cut = TestContext.Render<Register>();

        var googleForm = cut.FindAll("form")
            .Single(form => form.GetAttribute("action") == GoogleAuthRoutes.WebStartPath);
        var returnUrlInput = googleForm.QuerySelector($"input[name='{ExternalAuthFormFields.ReturnUrl}']");

        Assert.That(returnUrlInput, Is.Not.Null);
        Assert.That(returnUrlInput!.GetAttribute("value"), Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public void Register_PendingGoogleRegistration_PreservesReturnUrl()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/register?{ExternalAuthFormFields.PendingRegistrationToken}=pending-token&{ExternalAuthFormFields.Email}=google%40example.com&{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings");

        var cut = TestContext.Render<Register>();

        var googleForm = cut.FindAll("form")
            .Single(form => form.GetAttribute("action") == GoogleAuthRoutes.WebRegisterPath);
        var returnUrlInput = googleForm.QuerySelector($"input[name='{ExternalAuthFormFields.ReturnUrl}']");

        Assert.That(returnUrlInput, Is.Not.Null);
        Assert.That(returnUrlInput!.GetAttribute("value"), Is.EqualTo(AppPageRoutes.CreatorSettings));
    }

    [Test]
    public void Register_LoginLink_PreservesReturnUrl()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/register?{ExternalAuthFormFields.ReturnUrl}=%2FCreatorSettings");

        var cut = TestContext.Render<Register>();

        Assert.That(cut.Markup, Does.Contain("href=\"/login?returnUrl=%2FCreatorSettings\""));
    }

    [Test]
    public void Register_HasBrandPanel()
    {
        var cut = TestContext.Render<Register>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Find(".auth-panel"), Is.Not.Null);
            // The transparent mark - logo-dark-small.png has its background baked in.
            Assert.That(cut.Find(".auth-panel-logo").GetAttribute("src"), Does.Contain("logo-mark"));
            Assert.That(cut.Markup, Does.Contain("Join StreamTunes."));
        });
    }

    [Test]
    public void Register_SubmitUsesTheListenerTier_NotTheAccountViolet()
    {
        // AGENTS.md picks a button tier by AUDIENCE and lists "register" under cta-primary
        // alongside subscribe and play. Login's submit is the violet cta-secondary because
        // logging in is an account action; this one must not follow it there.
        var cut = TestContext.Render<Register>();

        var submit = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Register");

        Assert.Multiple(() =>
        {
            Assert.That(submit.ClassList, Does.Contain("cta-primary"));
            Assert.That(submit.ClassList, Does.Not.Contain("cta-secondary"));
            Assert.That(submit.ClassList, Does.Not.Contain("hero-secondary-cta"));
        });
    }

    [Test]
    public void Register_PolicyBlock_RendersOnBothPaths()
    {
        // The two copies of these four rows were byte-identical before they were extracted.
        // This is what stops them drifting apart again.
        var emailPath = TestContext.Render<Register>();
        Assert.That(emailPath.FindAll(".auth-legal-row"), Has.Count.EqualTo(4));

        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/register?{ExternalAuthFormFields.PendingRegistrationToken}=pending-token&{ExternalAuthFormFields.Email}=google%40example.com");

        var googlePath = TestContext.Render<Register>();
        Assert.Multiple(() =>
        {
            Assert.That(googlePath.FindAll(".auth-legal-row"), Has.Count.EqualTo(4));
            Assert.That(googlePath.Markup, Does.Contain("Terms of Use"));
            Assert.That(googlePath.Markup, Does.Contain("Privacy Policy"));
            Assert.That(googlePath.Markup, Does.Contain("Refund Policy"));
        });
    }
}
