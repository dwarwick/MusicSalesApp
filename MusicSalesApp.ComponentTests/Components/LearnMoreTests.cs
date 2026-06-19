using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.Models;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class LearnMoreTests : BUnitTestBase
{
    [Test]
    public void LearnMore_VerifiedNonCreatorGetStarted_LinksToCreatorSettings()
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

        var cut = TestContext.Render<LearnMore>();
        cut.WaitForState(() => cut.Markup.Contains("Get Started"), TimeSpan.FromSeconds(5));

        Assert.That(cut.FindAll("a.cta-button-register[href='/CreatorSettings']").Count, Is.EqualTo(2));
        Assert.That(cut.Markup, Does.Contain("href=\"/CreatorSettings\""));
        Assert.That(cut.Markup, Does.Not.Contain("href=\"/manage-account\" class=\"e-control e-btn cta-button cta-button-register\""));
    }

    [Test]
    public void LearnMore_AnonymousUser_ShowsTopAndBottomCreatorAuthButtons()
    {
        var cut = TestContext.Render<LearnMore>();
        cut.WaitForState(() => cut.Markup.Contains("Create Free Account"), TimeSpan.FromSeconds(5));

        Assert.That(cut.FindAll("a.cta-button-register[href='/register?returnUrl=%2FCreatorSettings']").Count, Is.EqualTo(2));
        Assert.That(cut.FindAll("a.cta-button-login[href='/login?returnUrl=%2FCreatorSettings']").Count, Is.EqualTo(2));
    }

    [Test]
    public void LearnMore_IncludesHowToBecomeCreatorSection()
    {
        var cut = TestContext.Render<LearnMore>();

        Assert.That(cut.Markup, Does.Contain("How to Become a Creator"));
        Assert.That(cut.Markup, Does.Contain("Creator / Artist Settings"));
        Assert.That(cut.Markup, Does.Contain("href=\"/CreatorSettings\""));
    }
}
