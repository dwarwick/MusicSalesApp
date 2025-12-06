using Bunit;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;
using Moq;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class RegisterTests : BUnitTestBase
{
    [Test]
    public void Register_RendersForm()
    {
        var cut = TestContext.Render<Register>();
        // SfCard renders the title in CardHeader
        Assert.That(cut.Markup, Does.Contain("Create Account"));
        Assert.That(cut.Find("input#email"), Is.Not.Null);
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
    public void Register_CallsService_OnValidSubmit()
    {
        MockAuthService.Setup(a => a.RegisterAsync("test@example.com", "Password_1!")).ReturnsAsync((true, ""));
        var cut = TestContext.Render<Register>();
        cut.Find("input#email").Change("test@example.com");
        cut.Find("input#password").Change("Password_1!");
        cut.Find("input#confirm").Change("Password_1!");
        cut.Find("form").Submit();
        MockAuthService.Verify(a => a.RegisterAsync("test@example.com", "Password_1!"), Times.Once);
    }
}
