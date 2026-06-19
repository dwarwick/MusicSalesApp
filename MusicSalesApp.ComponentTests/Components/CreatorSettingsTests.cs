using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
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
    public void CreatorSettings_ShowsTaxFormError_WhenPendingWithErrorMessage()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 1,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = "Middle Name is Invalid. The Middle Name can have Alphabets, Numbers and Special Characters ( & - ).",
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Middle Name is Invalid"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Middle Name is Invalid"));
        Assert.That(cut.Markup, Does.Contain("previous tax form submission had an error"));
        Assert.That(cut.Markup, Does.Contain("alert-danger"));
    }

    [Test]
    public void CreatorSettings_ShowsNormalPendingMessage_WhenNoErrorMessage()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 1,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = null,
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Complete Tax Form"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Please complete your tax form"));
        Assert.That(cut.Markup, Does.Not.Contain("alert-danger"));
        Assert.That(cut.Markup, Does.Not.Contain("previous tax form submission had an error"));
    }

    [Test]
    public void CreatorSettings_TaxFormErrorAlert_HasAccessibilityAttributes()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 1,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = "Middle Name is Invalid.",
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Middle Name is Invalid"), TimeSpan.FromSeconds(5));

        var alertDiv = cut.Find("div.alert-danger");
        Assert.That(alertDiv.GetAttribute("role"), Is.EqualTo("alert"));
        Assert.That(alertDiv.GetAttribute("aria-live"), Is.EqualTo("assertive"));
    }

    [Test]
    public void CreatorSettings_ActiveCreator_ShowsProfileAndPayoutSettings()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalEmail = "artist@example.com",
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Payout Email Address"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Creator Profile"));
        Assert.That(cut.Markup, Does.Contain("Upload Music"));
        Assert.That(cut.Markup, Does.Contain("Manage My Songs"));
        Assert.That(cut.Markup, Does.Contain("Update W8/W9 Tax Form"));
    }

    [Test]
    public async Task CreatorSettings_SavePayoutEmail_UsesCreatorService()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalEmail = "old@example.com",
            PayPalAccountAffirmed = true
        });

        MockCreatorService
            .Setup(x => x.UpdateCreatorPayoutEmailAsync(1, "payout@example.com"))
            .ReturnsAsync(new Creator { Id = 7, UserId = 1, PayPalEmail = "payout@example.com" });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Payout Email Address"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_paypalEmail", "payout@example.com");
        await InvokeNonPublicTask(cut.Instance, "SavePayPalEmail");

        MockCreatorService.Verify(x => x.UpdateCreatorPayoutEmailAsync(1, "payout@example.com"), Times.Once);
    }

    [Test]
    public void CreatorSettings_HasCreatorActivatedDialogRef()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 1,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.NotStarted,
            TaxFormStatus = TaxFormStatus.NotStarted
        });

        var cut = TestContext.Render<CreatorSettings>();

        Assert.That(cut.Instance, Is.Not.Null);
        Assert.That(cut.Markup, Is.Not.Null);
    }

    [Test]
    public void CreatorSettings_ActiveCreator_TracksCreatorSignupConversion()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();

        cut.WaitForAssertion(() =>
        {
            var trackingInvocations = GetGoogleAdsTrackingInvocations();
            Assert.That(trackingInvocations, Has.Count.EqualTo(1));

            var trackingInvocation = trackingInvocations.Single();
            Assert.That(trackingInvocation.Arguments[0]?.ToString(), Is.EqualTo("AW-18188763957/zvw_CJ6in74cELWGiuFD"));
            Assert.That(trackingInvocation.Arguments[1]?.ToString(), Is.EqualTo("creator-7"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task CreatorSettings_ActiveCreator_DoesNotTrackConversionTwice()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForAssertion(() => Assert.That(GetGoogleAdsTrackingInvocations(), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        await InvokeNonPublicTask(cut.Instance, "ShowCreatorActivatedDialog");
        await InvokeNonPublicTask(cut.Instance, "ShowCreatorActivatedDialog");

        Assert.That(GetGoogleAdsTrackingInvocations(), Has.Count.EqualTo(1));
    }

    [Test]
    public void CreatorSettings_ActiveCreator_DoesNotTrackConversion_WhenHostIsNotAllowed()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            PayPalAccountAffirmed = true
        }, "davidtest.dev");

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Payout Email Address"), TimeSpan.FromSeconds(5));

        Assert.That(GetGoogleAdsTrackingInvocations(), Is.Empty);
    }

    [Test]
    public void CreatorSettings_PendingCreator_DoesNotTrackCreatorSignupConversion()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("Complete Tax Form"), TimeSpan.FromSeconds(5));

        Assert.That(GetGoogleAdsTrackingInvocations(), Is.Empty);
    }

    [Test]
    public void CreatorSettings_IneligibleCreator_DoesNotTrackCreatorSignupConversion()
    {
        SetupCreatorSettingsPage(new Creator
        {
            Id = 7,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Ineligible,
            TaxFormStatus = TaxFormStatus.NotStarted,
            PayPalAccountAffirmed = true
        });

        var cut = TestContext.Render<CreatorSettings>();
        cut.WaitForState(() => cut.Markup.Contains("not eligible to register as a creator"), TimeSpan.FromSeconds(5));

        Assert.That(GetGoogleAdsTrackingInvocations(), Is.Empty);
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

        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var configValues = new Dictionary<string, string>
        {
            ["Facebook:AppId"] = "test-facebook-app-id",
            ["PayPal:SubscriptionPrice"] = "3.99",
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

    private void ConfigureRequestHost(string host)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);

        MockHttpContextAccessor
            .Setup(x => x.HttpContext)
            .Returns(httpContext);
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = typeof(CreatorSettingsModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Expected field {fieldName} to exist.");
        field!.SetValue(instance, value);
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
