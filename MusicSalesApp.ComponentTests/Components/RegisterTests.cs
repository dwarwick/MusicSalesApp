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
        Assert.That(cut.Markup, Does.Contain("Create Account"));
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

        Assert.That(cut.Markup, Does.Contain("Finish creating your Google account"));
        Assert.That(cut.Markup, Does.Contain("google@example.com"));
        Assert.That(cut.Markup, Does.Contain("Complete Google Sign Up"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("input#password"));
    }
}
