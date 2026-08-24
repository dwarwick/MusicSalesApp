using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Reflection;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class CreatorSettingsTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
    }

    [Test]
    public void CreatorSettings_InactiveCreator_RendersAgreementOnlyActivationCard()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: false));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Become a creator"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("testuser@test.com"));
        Assert.That(cut.Markup, Does.Contain("Review and accept the Creator Agreement"));
        Assert.That(cut.Markup, Does.Contain("Creator Agreement"));
        Assert.That(cut.Markup, Does.Contain("Become a Creator"));
        Assert.That(cut.FindAll(".settings-card").Count, Is.EqualTo(1));
        Assert.That(cut.Markup, Does.Not.Contain("PayPal email address"));
        Assert.That(cut.Markup, Does.Not.Contain("Complete W-9/W-8 Tax Form"));
    }

    [Test]
    public void CreatorSettings_BecomeCreatorButton_RequiresAgreementAcceptance()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: false));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Become a Creator"), TimeSpan.FromSeconds(5));

        Assert.That(GetNonPublicProperty<bool>(cut.Instance, "CanStartOnboarding"), Is.False);
        Assert.That(ButtonIsDisabled(FindButtonContaining(cut, "Become a Creator")), Is.True);

        SetField(cut.Instance, "_creatorAgreementAccepted", true);

        Assert.That(GetNonPublicProperty<bool>(cut.Instance, "CanStartOnboarding"), Is.True);
    }

    [Test]
    public void CreatorSettings_InactiveCreator_DoesNotPrecheckHistoricalAgreementAcceptance()
    {
        var creator = CreateCreator(isActive: false);
        creator.CreatorAgreementAccepted = true;
        creator.CreatorAgreementAcceptedAtUtc = DateTime.UtcNow.AddDays(-1);
        creator.AcknowledgmentAccepted = true;

        SetupCreatorSettingsPage(creator);

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Become a Creator"), TimeSpan.FromSeconds(5));

        Assert.That(GetNonPublicProperty<bool>(cut.Instance, "CanStartOnboarding"), Is.False);
        Assert.That(ButtonIsDisabled(FindButtonContaining(cut, "Become a Creator")), Is.True);
    }

    [Test]
    public void CreatorSettings_MainActions_UseHomePagePurpleCtaClasses()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: false));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Become a Creator"), TimeSpan.FromSeconds(5));

        var becomeCreatorButton = FindButtonContaining(cut, "Become a Creator");
        Assert.That(becomeCreatorButton.ClassList.Contains("settings-btn-violet"), Is.True);
        Assert.That(becomeCreatorButton.ClassList.Contains("settings-btn"), Is.True);
        Assert.That(becomeCreatorButton.ClassList.Contains("e-btn"), Is.True);
        Assert.That(becomeCreatorButton.ClassList.Contains("e-primary"), Is.False);
    }

    [Test]
    public async Task CreatorSettings_AgreementActivation_StartsOnboardingAndRefreshesSignIn()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: false));
        MockCreatorService
            .Setup(x => x.StartOnboardingAsync(It.Is<CreatorOnboardingInput>(input =>
                input.UserId == 1
                && input.CreatorAgreementAccepted
                && string.IsNullOrEmpty(input.PayPalEmail)
                && !input.PayPalAccountAffirmed
                && !input.SubmitTaxFormNow)))
            .ReturnsAsync(new StartOnboardingResult { Success = true, IsActive = true });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Become a Creator"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_creatorAgreementAccepted", true);
        await InvokeNonPublicTask(cut.Instance, "StartCreatorOnboarding");

        MockCreatorService.Verify(x => x.StartOnboardingAsync(It.IsAny<CreatorOnboardingInput>()), Times.Once);
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        Assert.That(navigationManager.Uri, Does.Contain(AppPageRoutes.RefreshSignIn));
        Assert.That(Uri.UnescapeDataString(navigationManager.Uri), Does.Contain($"{AppPageRoutes.CreatorSettings}?{CreatorSettingsQueryKeys.CreatorActivated}=true"));
    }

    [Test]
    public void CreatorSettings_ActiveCreator_ShowsStatusViewAndPrimaryActions()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.NotStarted,
            payPalEmail: null,
            payPalAccountAffirmed: false));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Where you stand"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Creator role"));
        Assert.That(cut.Markup, Does.Contain("Music uploads"));
        Assert.That(cut.Markup, Does.Contain("Payout setup"));
        Assert.That(cut.Markup, Does.Contain("Needed before payout"));
        Assert.That(cut.Markup, Does.Contain("Upload Music"));
        Assert.That(cut.Markup, Does.Contain("Manage My Songs"));
        Assert.That(cut.Markup, Does.Contain("View Earnings"));
        Assert.That(cut.Markup, Does.Contain("Set Up Payouts"));
    }

    [Test]
    public void CreatorSettings_UsesNoSyncfusionIconFont()
    {
        // The icon font this page used to carry is gone: the redesign standard is inline SVG
        // with fill="currentColor", because a glyph font cannot follow the theme. What the
        // original version of this test was really guarding is the line below - Syncfusion
        // ships its own e-icons glyphs, and one appearing here would be an unthemed icon
        // nobody chose. Scoped to buttons: Syncfusion builds its own checkbox and toast chrome
        // out of e-icons spans, and those are not ours to remove.
        SetupCreatorSettingsPage(CreateCreator(isActive: true, taxFormStatus: TaxFormStatus.NotStarted));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Where you stand"), TimeSpan.FromSeconds(5));

        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("Save Payout Email"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll("button .e-icons"), Is.Empty, "Syncfusion glyph icons cannot follow the theme");
            Assert.That(cut.FindAll(".streamtunes-button-icon"), Is.Empty,
                "the page-specific icon font was retired in favour of inline SVG");
            foreach (var svg in cut.FindAll("svg"))
            {
                Assert.That(svg.GetAttribute("fill") ?? svg.GetAttribute("stroke"), Is.Not.Null,
                    "an icon that hard-codes no colour source cannot follow the theme");
            }
        });
    }

    [Test]
    public void CreatorSettings_PayoutPanel_TogglesOpenAndClosed()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Not.Contain("PayPal email address"));

        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("PayPal email address"), TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Contain("owned or controlled by you, or by your authorised creator business"));
        Assert.That(cut.Markup, Does.Contain("I affirm that I own or am authorised to use this PayPal account"));

        FindButtonContaining(cut, "Hide Payout Setup").Click();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Not.Contain("PayPal email address")), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void CreatorSettings_ShowsTaxFormError_WhenPendingWithErrorMessage()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.Pending,
            lastTaxFormErrorMessage: "Middle Name is Invalid. The Middle Name can have Alphabets, Numbers and Special Characters ( & - ).",
            payPalEmail: "artist@example.com",
            payPalAccountAffirmed: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("Middle Name is Invalid"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Middle Name is Invalid"));
        Assert.That(cut.Markup, Does.Contain("previous tax form submission had an error"));
        Assert.That(cut.Markup, Does.Contain("info-strip-warn"));
    }

    [Test]
    public void CreatorSettings_ShowsNormalPendingTaxMessage_WhenNoErrorMessage()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.Pending,
            lastTaxFormErrorMessage: null,
            payPalEmail: "artist@example.com",
            payPalAccountAffirmed: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("Your tax form is pending"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Your tax form is pending"));
        Assert.That(cut.Markup, Does.Not.Contain("previous tax form submission had an error"));
    }

    [Test]
    public void CreatorSettings_TaxFormErrorAlert_HasAccessibilityAttributes()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.Pending,
            lastTaxFormErrorMessage: "Middle Name is Invalid.",
            payPalEmail: "artist@example.com",
            payPalAccountAffirmed: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("Middle Name is Invalid"), TimeSpan.FromSeconds(5));

        var alertDiv = cut.Find("div.info-strip-warn");
        Assert.That(alertDiv.GetAttribute("role"), Is.EqualTo("alert"));
        Assert.That(alertDiv.GetAttribute("aria-live"), Is.EqualTo("assertive"));
    }

    [Test]
    public async Task CreatorSettings_TaxFormAction_UsesSubmitTaxFormFlow()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true, taxFormStatus: TaxFormStatus.NotStarted));
        MockCreatorService
            .Setup(x => x.InitiateTaxFormUpdateAsync(1, "testuser@test.com"))
            .ReturnsAsync(new InitiateTaxFormUpdateResult { Success = true });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("Complete W-9/W-8 Tax Form"), TimeSpan.FromSeconds(5));

        await InvokeNonPublicTask(cut.Instance, "InitiateTaxFormUpdate");

        MockCreatorService.Verify(x => x.InitiateTaxFormUpdateAsync(1, "testuser@test.com"), Times.Once);
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        Assert.That(navigationManager.Uri, Does.EndWith(AppPageRoutes.SubmitTaxForm));
    }

    [Test]
    public void CreatorSettings_CompletedPayoutSetup_ShowsManagePayoutInfo()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.Completed,
            payPalEmail: "artist@example.com",
            payPalAccountAffirmed: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Manage Payout Info"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Complete"));
        Assert.That(cut.Markup, Does.Not.Contain("Getting paid, in three steps"));
        Assert.That(cut.Markup, Does.Not.Contain("Add a PayPal payout email"));
        Assert.That(cut.Markup, Does.Not.Contain("Complete your tax form"));

        FindButtonContaining(cut, "Manage Payout Info").Click();
        cut.WaitForState(() => cut.Markup.Contains("Manage payout information"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Confirmed"));
        Assert.That(cut.Markup, Does.Contain("Your tax form is on file"));
        Assert.That(cut.Markup, Does.Contain("Update W-9/W-8 Tax Form"));
    }

    [Test]
    public async Task CreatorSettings_SavePayoutEmail_UsesCreatorService()
    {
        var originalCreator = CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.NotStarted,
            payPalEmail: "old@example.com",
            payPalAccountAffirmed: false);
        var savedCreator = CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.NotStarted,
            payPalEmail: "payout@example.com",
            payPalAccountAffirmed: true);
        var currentCreator = originalCreator;

        SetupCreatorSettingsPage(originalCreator);

        MockCreatorService
            .Setup(x => x.GetCreatorByUserIdAsync(1))
            .Returns(() => Task.FromResult(currentCreator));

        MockCreatorService
            .Setup(x => x.UpdateCreatorPayoutEmailAsync(1, "payout@example.com", true))
            .Returns(() =>
            {
                currentCreator = savedCreator;
                return Task.FromResult(savedCreator);
            });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("PayPal email address"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_paypalEmail", "payout@example.com");
        SetField(cut.Instance, "_paypalAccountAffirmed", true);
        await InvokeNonPublicTask(cut.Instance, "SavePayPalEmail");
        cut.Render();

        MockCreatorService.Verify(x => x.UpdateCreatorPayoutEmailAsync(1, "payout@example.com", true), Times.Once);
        Assert.That(cut.Markup, Does.Contain("PayPal payout email saved."));
        Assert.That(GetNonPublicProperty<bool>(cut.Instance, "IsPayoutPayPalReady"), Is.True);
    }

    [Test]
    public async Task CreatorSettings_SavePayoutEmail_RejectsInvalidPayPalEmail()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.NotStarted,
            payPalEmail: "old@example.com",
            payPalAccountAffirmed: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("PayPal email address"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_paypalEmail", "@angelaomalley72");
        SetField(cut.Instance, "_paypalAccountAffirmed", true);
        await InvokeNonPublicTask(cut.Instance, "SavePayPalEmail");
        cut.Render();

        Assert.That(cut.Markup, Does.Contain(PayoutEmailValidator.InvalidPayPalEmailMessage));
        MockCreatorService.Verify(
            x => x.UpdateCreatorPayoutEmailAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Test]
    public async Task CreatorSettings_SavePayoutEmail_ClearsPayoutInfo_WhenEmailEmptyAndUnaffirmed()
    {
        var originalCreator = CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.Completed,
            payPalEmail: "old@example.com",
            payPalAccountAffirmed: true);
        var clearedCreator = CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.Completed,
            payPalEmail: null,
            payPalAccountAffirmed: false);
        var currentCreator = originalCreator;

        SetupCreatorSettingsPage(originalCreator);

        MockCreatorService
            .Setup(x => x.GetCreatorByUserIdAsync(1))
            .Returns(() => Task.FromResult(currentCreator));

        MockCreatorService
            .Setup(x => x.UpdateCreatorPayoutEmailAsync(1, It.Is<string>(value => string.IsNullOrWhiteSpace(value)), false))
            .Returns(() =>
            {
                currentCreator = clearedCreator;
                return Task.FromResult(clearedCreator);
            });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Manage Payout Info"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Manage Payout Info").Click();
        cut.WaitForState(() => cut.Markup.Contains("PayPal email address"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_paypalEmail", string.Empty);
        SetField(cut.Instance, "_paypalAccountAffirmed", false);
        await InvokeNonPublicTask(cut.Instance, "SavePayPalEmail");
        cut.Render();

        MockCreatorService.Verify(
            x => x.UpdateCreatorPayoutEmailAsync(1, It.Is<string>(value => string.IsNullOrWhiteSpace(value)), false),
            Times.Once);
        Assert.That(cut.Markup, Does.Contain("PayPal payout email cleared."));
        Assert.That(GetNonPublicProperty<bool>(cut.Instance, "IsPayoutPayPalReady"), Is.False);
        Assert.That(GetNonPublicProperty<bool>(cut.Instance, "IsPayoutReady"), Is.False);
    }

    [Test]
    public void CreatorSettings_PayPalStatus_UsesPersistedPayoutValues()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.NotStarted,
            payPalEmail: null,
            payPalAccountAffirmed: false));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        FindButtonContaining(cut, "Set Up Payouts").Click();

        SetField(cut.Instance, "_paypalEmail", "draft@example.com");
        SetField(cut.Instance, "_paypalAccountAffirmed", true);
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("Needed before payout"));
        Assert.That(GetNonPublicProperty<bool>(cut.Instance, "IsPayoutPayPalReady"), Is.False);
    }

    [Test]
    public void CreatorSettings_ActivationReturn_TracksCreatorSignupConversion()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true, taxFormStatus: TaxFormStatus.Completed));
        NavigateToCreatorActivationReturn();

        var cut = TestContext.Render<CreatorSettings>();

        cut.WaitForAssertion(() =>
        {
            var trackingInvocations = GetGoogleAdsTrackingInvocations();
            Assert.That(trackingInvocations, Has.Count.EqualTo(1));

            var trackingInvocation = trackingInvocations.Single();
            Assert.That(trackingInvocation.Arguments[0]?.ToString(), Is.EqualTo("AW-18188763957/zvw_CJ6in74cELWGiuFD"));
            Assert.That(trackingInvocation.Arguments[1]?.ToString(), Is.EqualTo("creator-7"));
        }, TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Contain("Creator account activated"));
    }

    [Test]
    public void CreatorSettings_ActivationReturn_WithCompletePayoutSetup_HidesIncompleteSetupGuidance()
    {
        SetupCreatorSettingsPage(CreateCreator(
            isActive: true,
            taxFormStatus: TaxFormStatus.Completed,
            payPalEmail: "artist@example.com",
            payPalAccountAffirmed: true));
        NavigateToCreatorActivationReturn();

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Creator account activated"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Payout setup"));
        Assert.That(cut.Markup, Does.Contain("Complete"));
        Assert.That(cut.Markup, Does.Not.Contain("Getting paid, in three steps"));
        Assert.That(cut.Markup, Does.Not.Contain("Add a PayPal payout email"));
        Assert.That(cut.Markup, Does.Not.Contain("Complete your tax form"));
    }

    [Test]
    public async Task CreatorSettings_ActivationReturn_DoesNotTrackConversionTwice()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true, taxFormStatus: TaxFormStatus.Completed));
        NavigateToCreatorActivationReturn();

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForAssertion(() => Assert.That(GetGoogleAdsTrackingInvocations(), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        await InvokeNonPublicTask(cut.Instance, "ShowCreatorActivatedDialog");
        await InvokeNonPublicTask(cut.Instance, "ShowCreatorActivatedDialog");

        Assert.That(GetGoogleAdsTrackingInvocations(), Has.Count.EqualTo(1));
    }

    [Test]
    public void CreatorSettings_ActivationReturn_DoesNotTrackConversion_WhenHostIsNotAllowed()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true, taxFormStatus: TaxFormStatus.Completed), "davidtest.dev");
        NavigateToCreatorActivationReturn();

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Creator account activated"), TimeSpan.FromSeconds(5));

        Assert.That(GetGoogleAdsTrackingInvocations(), Is.Empty);
    }

    [Test]
    public void CreatorSettings_InactiveCreator_DoesNotTrackCreatorSignupConversion()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: false));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Become a creator"), TimeSpan.FromSeconds(5));

        Assert.That(GetGoogleAdsTrackingInvocations(), Is.Empty);
    }

    [Test]
    public void CreatorSettings_DeactivationReturn_ShowsStopCreatorSuccess()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: false));
        NavigateToCreatorDeactivationReturn();

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("You are no longer a creator"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Become a creator"));
        Assert.That(cut.Markup, Does.Contain("You are no longer a creator. All your music has been removed from the platform."));
        Assert.That(GetGoogleAdsTrackingInvocations(), Is.Empty);
    }

    [Test]
    public async Task CreatorSettings_StopBeingCreator_TrimsConfirmationEmail()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true, taxFormStatus: TaxFormStatus.Completed));
        MockCreatorService.Setup(x => x.StopBeingCreatorAsync(1))
            .ReturnsAsync(true);

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Stop Being a Creator"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_stopSellingConfirmEmail", "  testuser@test.com  ");
        await InvokeNonPublicTask(cut.Instance, "ConfirmStopSelling");

        MockCreatorService.Verify(x => x.StopBeingCreatorAsync(1), Times.Once);
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        Assert.That(navigationManager.Uri, Does.Contain(AppPageRoutes.RefreshSignIn));
        Assert.That(Uri.UnescapeDataString(navigationManager.Uri), Does.Contain($"{AppPageRoutes.CreatorSettings}?{CreatorSettingsQueryKeys.CreatorDeactivated}=true"));
    }

    [Test]
    public void CreatorSettings_SectionNavLinks_SpellOutTheRoute()
    {
        // A bare href="#status" does not stay on this page. Blazor intercepts internal anchor
        // clicks and resolves the target against <base href="/">, so a fragment-only link
        // navigates to the HOME page carrying the fragment. Manage Account shipped that bug
        // once already.
        SetupCreatorSettingsPage(CreateCreator(isActive: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Where you stand"), TimeSpan.FromSeconds(5));

        var navLinks = cut.FindAll(".settings-nav-link");
        Assert.That(navLinks, Is.Not.Empty, "the section nav should render for an active creator");

        Assert.Multiple(() =>
        {
            foreach (var link in navLinks)
            {
                var label = link.TextContent.Trim();
                var href = link.GetAttribute("href");
                Assert.That(href, Does.StartWith(AppPageRoutes.CreatorSettings),
                    $"{label} must name the route, or the link lands on the home page");

                var section = href![(href.IndexOf("#", StringComparison.Ordinal) + 1)..];
                Assert.That(cut.FindAll($"#{section}"), Is.Not.Empty,
                    $"{label} points at #{section}, which nothing on the page renders");
            }
        });
    }

    [TestCase(true, true, 3, TestName = "CreatorSettings_Steps_AllDone_HidesTheCard")]
    [TestCase(false, true, 2, TestName = "CreatorSettings_Steps_PayPalMissing_TicksTheOtherTwo")]
    [TestCase(true, false, 2, TestName = "CreatorSettings_Steps_TaxMissing_TicksTheOtherTwo")]
    public void CreatorSettings_StepsReflectRealState(bool payPalReady, bool taxReady, int expectedTicks)
    {
        // The point of rebuilding this card: it used to be three static numbered tiles that
        // never changed, whatever the creator had actually done.
        var creator = CreateCreator(
            isActive: true,
            taxFormStatus: taxReady ? TaxFormStatus.Completed : TaxFormStatus.NotStarted);
        creator.PayPalEmail = payPalReady ? "payouts@test.com" : string.Empty;
        creator.PayPalAccountAffirmed = payPalReady;
        SetupCreatorSettingsPage(creator);

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Where you stand"), TimeSpan.FromSeconds(5));

        var allDone = payPalReady && taxReady;
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup.Contains("Getting paid, in three steps"), Is.EqualTo(!allDone),
                "with nothing outstanding the card is a wall of ticks between the reader and the rest");
            Assert.That(cut.FindAll(".step-badge-done").Count, Is.EqualTo(allDone ? 0 : expectedTicks));
        });
    }

    [Test]
    public void CreatorSettings_Personas_ListThemAndLinkAcross()
    {
        // Nothing on this page linked to /creator/personas before - the only route in was the
        // nav menu, which is a poor place to learn that the artist name a listener sees is not
        // the display name set two sections above.
        var creator = CreateCreator(isActive: true);
        SetupCreatorSettingsPage(creator);

        MockCreatorPersonaService.Setup(x => x.GetPersonasByCreatorIdAsync(creator.Id))
            .ReturnsAsync(new List<CreatorPersona>
            {
                new() { Id = 7, CreatorId = creator.Id, Name = "Nightshift Radio", Bio = "Slow and late.", IsEnabled = true },
                new() { Id = 8, CreatorId = creator.Id, Name = "Warwick", IsEnabled = true },
            });
        MockCreatorPersonaService.Setup(x => x.GetPersonaSongCountsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, int> { [7] = 14 });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Your artist identities"), TimeSpan.FromSeconds(5));

        var rows = cut.FindAll(".persona-row");
        Assert.Multiple(() =>
        {
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows[0].TextContent, Does.Contain("Nightshift Radio"));
            Assert.That(rows[0].TextContent, Does.Contain("14 songs"));
            // A persona with no linked songs is omitted from the count dictionary, not zero.
            Assert.That(rows[1].TextContent, Does.Contain("0 songs"));
            Assert.That(rows[1].TextContent, Does.Contain("No website"));
        });

        FindButtonContaining(cut, "Manage Personas").Click();

        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        Assert.That(navigationManager.Uri, Does.EndWith(AppPageRoutes.CreatorPersonas));
    }

    [Test]
    public void CreatorSettings_NoPersonas_NamesTheDisplayNameThatWillBeUsedInstead()
    {
        var creator = CreateCreator(isActive: true);
        creator.DisplayName = "Dave Warwick";
        SetupCreatorSettingsPage(creator);

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Your artist identities"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".persona-row"), Is.Empty);
            Assert.That(cut.Find(".settings-empty-body").TextContent, Does.Contain("Dave Warwick"),
                "the empty state has to say what listeners see instead");
            Assert.That(FindButtonContaining(cut, "Create a Persona"), Is.Not.Null);
        });
    }

    [Test]
    public void CreatorSettings_StopBeingACreator_SaysHowManySongsGo()
    {
        // This deletes the audio files, not just the listings, so the dialog has to name the
        // size of what is about to go rather than describing it in the abstract. The singular
        // matters: "1 songs" is the classic tell that nobody read the screen.
        SetupCreatorSettingsPage(CreateCreator(isActive: true));
        MockCreatorService.Setup(x => x.GetCreatorSongCountAsync(It.IsAny<int>())).ReturnsAsync(1);

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Where you stand"), TimeSpan.FromSeconds(5));

        FindButtonContaining(cut, "Stop Being a Creator").Click();
        cut.WaitForState(() => cut.Markup.Contains("Type your email to confirm"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("remove <strong>1 song</strong> from StreamTunes"));
    }
    private void SetupCreatorSettingsPage(Creator creator, params string[] enabledHosts)
    {
        // Default to a creator who has uploaded. A zero count is a real state, but it is a
        // different test from any of these, and leaving it at zero keeps the next-steps card
        // on screen in every scenario.
        MockCreatorService.Setup(x => x.GetCreatorSongCountAsync(It.IsAny<int>()))
            .ReturnsAsync(4);

        if (enabledHosts.Length == 0)
        {
            enabledHosts = new[] { "localhost" };
        }

        ConfigureRequestHost("localhost");
        SetupAuthorizedUser(creator.UserId, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = creator.UserId,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        MockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(creator.UserId))
            .ReturnsAsync(creator);

        MockAppSettingsService.Setup(x => x.ShouldShowTaxBanditsMaintenanceWarningAsync())
            .ReturnsAsync(false);

        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var configValues = new Dictionary<string, string>
        {
            ["Facebook:AppId"] = "test-facebook-app-id",
            [GoogleAdsTrackingConfigKeys.Enabled] = "true",
            [GoogleAdsTrackingConfigKeys.TagId] = "AW-18188763957",
            [GoogleAdsTrackingConfigKeys.CreatorSignupConversionLabel] = "zvw_CJ6in74cELWGiuFD"
        };

        for (var i = 0; i < enabledHosts.Length; i++)
        {
            configValues[$"{GoogleAdsTrackingConfigKeys.EnabledHosts}:{i}"] = enabledHosts[i];
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
        TestContext.Services.AddSingleton<IConfiguration>(configuration);

        SetupRendererInfo();
    }

    private static Creator CreateCreator(
        bool isActive,
        TaxFormStatus taxFormStatus = TaxFormStatus.NotStarted,
        string payPalEmail = null,
        bool payPalAccountAffirmed = false,
        string lastTaxFormErrorMessage = null)
    {
        return new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = isActive,
            OnboardingStatus = isActive ? CreatorOnboardingStatus.Completed : CreatorOnboardingStatus.NotStarted,
            TaxFormStatus = taxFormStatus,
            PayPalEmail = payPalEmail,
            PayPalAccountAffirmed = payPalAccountAffirmed,
            CreatorAgreementAccepted = isActive,
            LastTaxFormErrorMessage = lastTaxFormErrorMessage,
            DisplayName = "Test Creator",
            Bio = "Test bio"
        };
    }

    private void ConfigureRequestHost(string host)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);

        MockHttpContextAccessor
            .Setup(x => x.HttpContext)
            .Returns(httpContext);
    }

    private void NavigateToCreatorActivationReturn()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"{AppPageRoutes.CreatorSettings}?{CreatorSettingsQueryKeys.CreatorActivated}=true");
    }

    private void NavigateToCreatorDeactivationReturn()
    {
        var navigationManager = TestContext.Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"{AppPageRoutes.CreatorSettings}?{CreatorSettingsQueryKeys.CreatorDeactivated}=true");
    }

    private static IElement FindButtonContaining(IRenderedComponent<CreatorSettings> cut, string text)
    {
        var button = cut.FindAll("button")
            .FirstOrDefault(element => element.TextContent.Contains(text, StringComparison.OrdinalIgnoreCase));
        Assert.That(button, Is.Not.Null, $"Expected to find a button containing '{text}'.");
        return button!;
    }

    private static bool ButtonIsDisabled(IElement button) =>
        button.HasAttribute("disabled")
        || button.ClassList.Contains("e-disabled")
        || string.Equals(button.GetAttribute("aria-disabled"), "true", StringComparison.OrdinalIgnoreCase);

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = typeof(CreatorSettingsModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Expected field {fieldName} to exist.");
        field!.SetValue(instance, value);
    }

    private static T GetNonPublicProperty<T>(object instance, string propertyName)
    {
        var property = typeof(CreatorSettingsModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(property, Is.Not.Null, $"Expected property {propertyName} to exist.");
        return (T)property!.GetValue(instance)!;
    }

    private static Task InvokeNonPublicTask(object instance, string methodName)
    {
        var method = typeof(CreatorSettingsModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Expected method {methodName} to exist.");
        return (Task)method!.Invoke(instance, null)!;
    }

    private List<Bunit.JSRuntimeInvocation> GetGoogleAdsTrackingInvocations()
        => TestContext.JSInterop.Invocations
            .Where(invocation => invocation.Identifier == GoogleAdsTrackingConfigKeys.TrackConversionFunctionName)
            .ToList();
}
