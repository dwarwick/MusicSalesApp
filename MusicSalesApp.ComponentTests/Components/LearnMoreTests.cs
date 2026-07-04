using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.Models;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class NewCreatorSignUpTests : BUnitTestBase
{
    [Test]
    public void NewCreatorSignUp_VerifiedNonCreatorGetStarted_LinksToCreatorSettings()
    {
        const int userId = 1;
        SetupAuthorizedUser(userId, "test@user.com");

        var testUser = new ApplicationUser
        {
            Id = userId,
            UserName = "test@user.com",
            Email = "test@user.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(testUser);
        MockUserManager.Setup(x => x.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>())).ReturnsAsync(false);

        var cut = TestContext.Render<NewCreatorSignUp>();
        cut.WaitForState(() => cut.Markup.Contains("Get Started"), TimeSpan.FromSeconds(5));

        Assert.That(cut.FindAll("a.cta-button-register[href='/CreatorSettings']").Count, Is.EqualTo(1));
        Assert.That(cut.Markup, Does.Contain("href=\"/CreatorSettings\""));
        Assert.That(cut.Markup, Does.Not.Contain("href=\"/manage-account\" class=\"e-control e-btn cta-button cta-button-register\""));
    }

    [Test]
    public void NewCreatorSignUp_AnonymousUser_ShowsCreatorAuthButtons()
    {
        var cut = TestContext.Render<NewCreatorSignUp>();
        cut.WaitForState(() => cut.Markup.Contains("Start Creator Signup"), TimeSpan.FromSeconds(5));

        Assert.That(cut.FindAll("a.cta-button-register[href='/register?returnUrl=%2FCreatorSettings']").Count, Is.EqualTo(1));
        Assert.That(cut.FindAll("a.cta-button-login[href='/login?returnUrl=%2FCreatorSettings']").Count, Is.EqualTo(1));
    }

    [Test]
    public void NewCreatorSignUp_DoesNotIncludeMovedQuestionSections()
    {
        var cut = TestContext.Render<NewCreatorSignUp>();

        Assert.That(cut.Markup, Does.Not.Contain("Why Streamtunes Exists"));
        Assert.That(cut.Markup, Does.Not.Contain("How to Become a Creator"));
    }

    [Test]
    public void NewCreatorSignupQuestions_IncludesMovedQuestionSections()
    {
        var cut = TestContext.Render<NewCreatorSignupQuestions>();

        Assert.That(cut.Markup, Does.Contain("Why Streamtunes Exists"));
        Assert.That(cut.Markup, Does.Contain("How to Become a Creator"));
        Assert.That(cut.Markup, Does.Contain("Creator / Artist Settings"));
        Assert.That(cut.Markup, Does.Contain("href=\"/CreatorSettings\""));
    }

    [Test]
    public void NewCreatorSignupQuestions_AnonymousUser_ShowsBottomCreatorAuthButtons()
    {
        var cut = TestContext.Render<NewCreatorSignupQuestions>();
        cut.WaitForState(() => cut.Markup.Contains("Start Creator Signup"), TimeSpan.FromSeconds(5));

        Assert.That(cut.FindAll("a.cta-button-register[href='/register?returnUrl=%2FCreatorSettings']").Count, Is.EqualTo(1));
        Assert.That(cut.FindAll("a.cta-button-login[href='/login?returnUrl=%2FCreatorSettings']").Count, Is.EqualTo(1));
    }
}
