using Bunit;
using MusicSalesApp.Components.Pages;
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
    public void Register_DisplaysTermsAndPrivacyCheckboxes()
    {
        var cut = TestContext.Render<Register>();
        // Verify the legal agreement section is rendered
        Assert.That(cut.Markup, Does.Contain("Terms of Use"));
        Assert.That(cut.Markup, Does.Contain("Privacy Policy"));
        Assert.That(cut.Markup, Does.Contain("legal-agreements"));
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
        
        // Should show error message about accepting terms
        Assert.That(cut.Markup, Does.Contain("accept the Terms of Use and Privacy Policy"));
    }
}
