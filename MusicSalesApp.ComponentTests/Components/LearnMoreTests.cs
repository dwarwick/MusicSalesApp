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
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupRendererInfo();
    }

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

    [Test]
    public void NewCreatorSignUp_DrawsTheContactIconAsSvg_NotAnEmoji()
    {
        // It was a U+1F4E7 emoji at font-size:2.5em. An emoji ignores currentColor and renders in
        // whatever the platform font decides, so it could not follow the theme with everything else.
        var cut = TestContext.Render<NewCreatorSignUp>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("\U0001F4E7"));
            Assert.That(cut.Find(".contact-line svg.contact-icon"), Is.Not.Null);
            Assert.That(cut.Find("svg.contact-icon").GetAttribute("stroke"), Is.EqualTo("currentColor"));
        });
    }

    [Test]
    public void NewCreatorSignUp_KeepsItsCallsToAction_OnTheVioletCtaClasses()
        => AssertCreatorActionsAreViolet(TestContext.Render<NewCreatorSignUp>().FindAll("a.cta-button-register, a.cta-button-login"));

    [Test]
    public void NewCreatorSignupQuestions_KeepsItsCallsToAction_OnTheVioletCtaClasses()
        => AssertCreatorActionsAreViolet(TestContext.Render<NewCreatorSignupQuestions>().FindAll("a.cta-button-register, a.cta-button-login"));

    /// <summary>
    /// AGENTS.md: creator and account actions use <c>cta-secondary hero-secondary-cta</c>. Without
    /// those, these buttons fall through to the bare <c>.cta-button</c> rule - the blue accent the
    /// LISTENER subscription CTAs use - and a creator action becomes indistinguishable from a
    /// subscribe button. The two pages are consecutive steps of one flow, so they must agree.
    /// </summary>
    private static void AssertCreatorActionsAreViolet(IEnumerable<AngleSharp.Dom.IElement> creatorActions)
    {
        var actions = creatorActions.ToList();
        Assert.That(actions, Is.Not.Empty, "expected at least one creator call to action");

        Assert.Multiple(() =>
        {
            foreach (var action in actions)
            {
                Assert.That(action.ClassList, Does.Contain("cta-secondary"), action.TextContent.Trim());
                Assert.That(action.ClassList, Does.Contain("hero-secondary-cta"), action.TextContent.Trim());
            }
        });
    }
}
