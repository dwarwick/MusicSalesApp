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
        cut.WaitForState(() => cut.Markup.Contains("Creator activation"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Account Email: testuser@test.com"));
        Assert.That(cut.Markup, Does.Contain("Review and accept the Creator Agreement"));
        Assert.That(cut.Markup, Does.Contain("Creator Agreement"));
        Assert.That(cut.Markup, Does.Contain("Become a Creator"));
        Assert.That(cut.FindAll(".creator-settings-card").Count, Is.EqualTo(1));
        Assert.That(cut.Markup, Does.Not.Contain("PayPal Email Address"));
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
        Assert.That(becomeCreatorButton.ClassList.Contains("cta-secondary"), Is.True);
        Assert.That(becomeCreatorButton.ClassList.Contains("hero-secondary-cta"), Is.True);
        Assert.That(becomeCreatorButton.ClassList.Contains("creator-settings-cta"), Is.True);
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
        cut.WaitForState(() => cut.Markup.Contains("Your creator account is active"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Creator role"));
        Assert.That(cut.Markup, Does.Contain("Music uploads"));
        Assert.That(cut.Markup, Does.Contain("Payout setup"));
        Assert.That(cut.Markup, Does.Contain("Required before payout"));
        Assert.That(cut.Markup, Does.Contain("Upload Music"));
        Assert.That(cut.Markup, Does.Contain("Manage My Songs"));
        Assert.That(cut.Markup, Does.Contain("View Earnings"));
        Assert.That(cut.Markup, Does.Contain("Set Up Payouts"));
    }

    [Test]
    public void CreatorSettings_ButtonsUseStableAppOwnedIcons()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true, taxFormStatus: TaxFormStatus.NotStarted));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Your creator account is active"), TimeSpan.FromSeconds(5));

        AssertButtonHasIcon(cut, "Upload Music", "streamtunes-button-icon-upload-music");
        AssertButtonHasIcon(cut, "Manage My Songs", "streamtunes-button-icon-song-list");
        AssertButtonHasIcon(cut, "View Earnings", "streamtunes-button-icon-earnings");
        AssertButtonHasIcon(cut, "Set Up Payouts", "streamtunes-button-icon-payout");
        AssertButtonHasIcon(cut, "Stop Being a Creator", "streamtunes-button-icon-warning");

        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("Save PayPal Email"), TimeSpan.FromSeconds(5));

        AssertButtonHasIcon(cut, "Save PayPal Email", "streamtunes-button-icon-save");
        AssertButtonHasIcon(cut, "Complete W-9/W-8 Tax Form", "streamtunes-button-icon-tax-form");

        Assert.That(cut.FindAll("button .e-icons"), Is.Empty);
    }

    [Test]
    public void CreatorSettings_PayoutPanel_TogglesOpenAndClosed()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: true));

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Set Up Payouts"), TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Not.Contain("PayPal Email Address"));

        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForState(() => cut.Markup.Contains("PayPal Email Address"), TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Contain("owned or controlled by you or your authorized creator business"));
        Assert.That(cut.Markup, Does.Contain("I affirm that I own or am authorized to use this PayPal account"));

        FindButtonContaining(cut, "Set Up Payouts").Click();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Not.Contain("PayPal Email Address")), TimeSpan.FromSeconds(5));
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
        Assert.That(cut.Markup, Does.Contain("creator-settings-alert-danger"));
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

        var alertDiv = cut.Find("div.creator-settings-alert-danger");
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
        Assert.That(cut.Markup, Does.Not.Contain("Payout setup comes later"));
        Assert.That(cut.Markup, Does.Not.Contain("Reach the payout threshold"));
        Assert.That(cut.Markup, Does.Not.Contain("Complete payout setup"));

        FindButtonContaining(cut, "Manage Payout Info").Click();
        cut.WaitForState(() => cut.Markup.Contains("Manage payout information"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Confirmed"));
        Assert.That(cut.Markup, Does.Contain("Your tax form is complete"));
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
        cut.WaitForState(() => cut.Markup.Contains("PayPal Email Address"), TimeSpan.FromSeconds(5));

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
        cut.WaitForState(() => cut.Markup.Contains("PayPal Email Address"), TimeSpan.FromSeconds(5));

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
        cut.WaitForState(() => cut.Markup.Contains("PayPal Email Address"), TimeSpan.FromSeconds(5));

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
        Assert.That(cut.Markup, Does.Not.Contain("Payout setup comes later"));
        Assert.That(cut.Markup, Does.Not.Contain("Reach the payout threshold"));
        Assert.That(cut.Markup, Does.Not.Contain("Complete payout setup"));
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
        cut.WaitForState(() => cut.Markup.Contains("Creator activation"), TimeSpan.FromSeconds(5));

        Assert.That(GetGoogleAdsTrackingInvocations(), Is.Empty);
    }

    [Test]
    public void CreatorSettings_DeactivationReturn_ShowsStopCreatorSuccess()
    {
        SetupCreatorSettingsPage(CreateCreator(isActive: false));
        NavigateToCreatorDeactivationReturn();

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("You are no longer a creator"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Creator activation"));
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

    private void SetupCreatorSettingsPage(Creator creator, params string[] enabledHosts)
    {
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

    private static void AssertButtonHasIcon(
        IRenderedComponent<CreatorSettings> cut,
        string buttonText,
        string iconClass)
    {
        var button = FindButtonContaining(cut, buttonText);
        Assert.That(
            button.QuerySelector($".streamtunes-button-icon.{iconClass}"),
            Is.Not.Null,
            $"Expected the '{buttonText}' button to use {iconClass}.");
        Assert.That(
            button.QuerySelector(".e-icons"),
            Is.Null,
            $"Expected the '{buttonText}' button to avoid Syncfusion e-icons glyph classes.");
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
