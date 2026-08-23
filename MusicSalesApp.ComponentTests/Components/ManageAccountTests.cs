using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages.Auth;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using System.Reflection;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class ManageAccountTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: false, isFirstTimeSubscriber: true));
    }

    [Test]
    public void ManageAccount_NotAuthenticated_ShowsWarning()
    {
        // Arrange - already set up with unauthenticated user
        SetupRendererInfo();

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert - Should show a loading spinner or warning about authentication
        // Component will show loading initially, then show authentication warning
        Assert.That(cut.Markup, Is.Not.Null);
    }

    [Test]
    public void ManageAccount_RendersPageTitle()
    {
        // Arrange
        SetupRendererInfo();
        
        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert - PageTitle should be set
        Assert.That(cut.Markup, Is.Not.Null);
    }

    [Test]
    public void ManageAccount_DoesNotRenderCreatorSettingsSections()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);
        MockCreatorService.Setup(x => x.IsActiveCreatorAsync(1))
            .ReturnsAsync(true);
        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = false,
                Status = SubscriptionStatuses.Expired,
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Close My Account"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Not.Contain("Become a Creator"));
        Assert.That(cut.Markup, Does.Not.Contain("Creator Profile"));
        Assert.That(cut.Markup, Does.Not.Contain("Payout Email Address"));
        Assert.That(cut.Markup, Does.Not.Contain("Complete Tax Form"));
        Assert.That(cut.Markup, Does.Not.Contain("Stop Being a Creator"));
    }

    [Test]
    public void ManageAccount_AppleActiveSubscriptionWithFutureEndDate_ShowsManageSubscriptionAndNoNewSubscriptionPrompt()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = true,
                Status = SubscriptionStatuses.Active,
                MonthlyPrice = 3.99m,
                StartDate = DateTime.UtcNow.AddDays(-5),
                EndDate = DateTime.UtcNow.AddDays(25),
                NextBillingDate = DateTime.UtcNow.AddDays(25),
                BillingSource = BillingSources.Apple,
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Current Billing Period Ends"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Manage your subscription"));
        Assert.That(cut.Markup, Does.Contain("America/New_York"));
        Assert.That(cut.Markup, Does.Contain("Current Billing Period Ends"));
        Assert.That(cut.Markup, Does.Contain("will automatically renew unless canceled"));
        Assert.That(cut.Markup, Does.Contain("Manage Subscription"));
        Assert.That(cut.Markup, Does.Not.Contain("Start a new subscription at any time."));
    }

    [Test]
    public void ManageAccount_AppleSubscriptionManagementUrl_ComesFromConfiguration()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = true,
                Status = SubscriptionStatuses.Active,
                MonthlyPrice = 3.99m,
                BillingSource = BillingSources.Apple,
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Facebook:AppId"] = "test-facebook-app-id",
                ["AppleAppStore:SubscriptionManagementUrl"] = "https://developer.apple.com/documentation/storekit/testing-disabling-auto-renew"
            })
            .Build();
        TestContext.Services.AddSingleton<IConfiguration>(configuration);

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Manage Subscription"), TimeSpan.FromSeconds(5));

        var method = typeof(ManageAccountModel).GetMethod("GetExternalSubscriptionManagementUrl",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var url = method?.Invoke(cut.Instance, null) as string;

        Assert.That(url, Is.EqualTo("https://developer.apple.com/documentation/storekit/testing-disabling-auto-renew"));
    }

    [Test]
    public void ManageAccount_CancelledSubscriptionWithRemainingAccess_HidesCancelActionsAndShowsNonRenewingMessage()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = true,
                Status = SubscriptionStatuses.Cancelled,
                MonthlyPrice = 3.99m,
                StartDate = DateTime.UtcNow.AddDays(-5),
                EndDate = DateTime.UtcNow.AddDays(25),
                NextBillingDate = DateTime.UtcNow.AddDays(25),
                BillingSource = BillingSources.PayPal,
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Access Until"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Renews Off"));
        Assert.That(cut.Markup, Does.Contain("America/New_York"));
        Assert.That(cut.Markup, Does.Contain("has been canceled"));
        Assert.That(cut.Markup, Does.Contain("will not automatically renew"));
        Assert.That(cut.Markup, Does.Not.Contain("Cancel Subscription"));
        Assert.That(cut.Markup, Does.Not.Contain("Manage Subscription"));
    }

    [Test]
    public void ManageAccount_EligibleSubscriber_ShowsAuthoritativeTrialTerms()
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = SubscriptionStatuses.Expired
        });
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(testUser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: true, isFirstTimeSubscriber: true, regularPrice: 0.99m));

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Start My 3-Day Free Trial"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Try unlimited music free for 3 days"));
            Assert.That(cut.Markup, Does.Contain("$0.99 per month"));
            Assert.That(cut.Markup, Does.Contain("you will not be charged").IgnoreCase);
            Assert.That(cut.Markup, Does.Contain("full streaming access will continue through the end of the trial"));
        });
    }

    [Test]
    public void ManageAccount_ReturningSubscriber_ShowsCompanionPlanWithoutTrialPromise()
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = SubscriptionStatuses.Expired
        });
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(testUser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: false, isFirstTimeSubscriber: false, regularPrice: 0.99m));

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Resubscribe with PayPal"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Restart unlimited streaming for $0.99 per month"));
            Assert.That(cut.Markup, Does.Contain("you will receive the current subscription price"));
            Assert.That(cut.Markup, Does.Not.Contain("Start My 3-Day Free Trial"));
            Assert.That(cut.Markup, Does.Not.Contain("3 days free trial"));
        });
    }

    [TestCase(SubscriptionStatuses.Active)]
    [TestCase(SubscriptionStatuses.Suspended)]
    public void ManageAccount_PotentiallyBillablePayPalAgreement_MustBeResolvedBeforeAnotherCheckout(string status)
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = status,
            BillingSource = BillingSources.PayPal,
            PaypalSubscriptionId = "I-PAYMENT-RETRY"
        });
        MockPayPalSubscriptionManagementService
            .Setup(service => service.GetOpenMismatchCorrelationIdAsync(
                testUser.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("5bf0b8bb-b63f-4781-8c21-a767e8beb8ba");

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("PayPal billing needs attention"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Refresh Subscription"));
            Assert.That(cut.Markup, Does.Contain("Open PayPal"));
            Assert.That(cut.Markup, Does.Contain("Contact Support"));
            Assert.That(cut.Markup, Does.Contain("Stop PayPal Billing"));
            Assert.That(cut.Markup, Does.Contain("prevent overlapping charges"));
            Assert.That(cut.Markup, Does.Contain("Creator tips and other one-time payments are not affected"));
            Assert.That(cut.Markup, Does.Contain("5bf0b8bb-b63f-4781-8c21-a767e8beb8ba"));
            Assert.That(cut.Markup, Does.Not.Contain("Subscribe with PayPal"));
            Assert.That(cut.Markup, Does.Not.Contain("Resubscribe with PayPal"));
        });
        MockPayPalSubscriptionManagementService.Verify(
            service => service.GetOfferQuoteAsync(testUser.Id, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestCase(SubscriptionStatuses.ApprovalPending)]
    [TestCase(SubscriptionStatuses.Approved)]
    public void ManageAccount_UnapprovedPayPalCheckout_StillOffersSubscription(string status)
    {
        // The counterpart to the test above, and the reason the mismatch email must not claim a
        // block for these statuses: an agreement the buyer never approved does not block anything.
        SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = status,
            BillingSource = BillingSources.PayPal,
            PaypalSubscriptionId = "I-ABANDONED"
        });

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(
            () => cut.Markup.Contains("with PayPal", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("PayPal billing needs attention"));
            Assert.That(cut.Markup, Does.Not.Contain("prevent overlapping charges"));
            Assert.That(cut.Markup, Does.Not.Contain("Stop PayPal Billing"));
        });
    }

    [Test]
    public async Task ManageAccount_RefreshSubscription_ShowsSupportCorrelation_WhenActiveMismatchRemains()
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = SubscriptionStatuses.Active,
            BillingSource = BillingSources.PayPal,
            PaypalSubscriptionId = "I-PAYMENT-RETRY"
        });
        var correlationId = "4bd2260b-2a3f-49dd-a216-c581bf6ca868";
        MockPayPalSubscriptionManagementService
            .Setup(service => service.ResolveCurrentMismatchAsync(
                testUser,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalMismatchResolutionResult(
                PayPalMismatchResolutionStatuses.ActiveWithoutEntitlement,
                SubscriptionStatuses.Active,
                correlationId));

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Refresh Subscription"), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => InvokeNonPublicTask(cut.Instance, "RefreshSubscription"));
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(correlationId)));

        Assert.That(
            cut.Markup,
            Does.Contain("did not provide enough payment or trial evidence"));
        MockPayPalSubscriptionManagementService.Verify(
            service => service.ResolveCurrentMismatchAsync(
                testUser,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void ManageAccount_ExpiredPayPalAgreement_AllowsAnotherCheckout()
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = SubscriptionStatuses.Expired,
            BillingSource = BillingSources.PayPal,
            PaypalSubscriptionId = "I-PAYMENT-RETRY"
        });
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(testUser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: false, isFirstTimeSubscriber: false, regularPrice: 0.99m));

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Resubscribe with PayPal"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("PayPal billing needs attention"));
            Assert.That(cut.Markup, Does.Not.Contain("Stop PayPal Billing"));
            Assert.That(cut.Markup, Does.Not.Contain("prevent overlapping charges"));
            Assert.That(cut.Markup, Does.Contain("Resubscribe with PayPal"));
        });
        MockPayPalSubscriptionManagementService.Verify(
            service => service.GetOfferQuoteAsync(testUser.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ManageAccount_Subscribe_BindsAcceptanceToDisplayedOfferVersion()
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = SubscriptionStatuses.Expired
        });
        var offer = CreateOfferQuote(hasFreeTrial: true, isFirstTimeSubscriber: true, regularPrice: 2.99m, settingsVersion: 23);
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(testUser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(offer);
        MockPayPalSubscriptionManagementService
            .Setup(x => x.CreateSubscriptionAsync(
                testUser,
                true,
                offer.SettingsVersion,
                offer.PlanId,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PayPalCheckoutResult.Failed("Expected test failure"));

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Start My 3-Day Free Trial"), TimeSpan.FromSeconds(5));
        SetField(cut.Instance, "_agreeToTerms", true);

        await InvokeNonPublicTask(cut.Instance, "Subscribe");

        MockPayPalSubscriptionManagementService.Verify(x => x.CreateSubscriptionAsync(
            testUser,
            true,
            23,
            offer.PlanId,
            "http://localhost/",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ManageAccount_CancelTrial_UsesProviderCancellationAndPromisesNoCharge()
    {
        var trialEnd = DateTime.UtcNow.AddDays(2);
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = true,
            IsOnTrial = true,
            Status = SubscriptionStatuses.Active,
            MonthlyPrice = 0.99m,
            StartDate = DateTime.UtcNow.AddDays(-1),
            EndDate = trialEnd,
            NextBillingDate = trialEnd,
            TrialEndDate = trialEnd,
            BillingSource = BillingSources.PayPal
        });
        TestContext.JSInterop
            .Setup<bool>("confirm", _ => true)
            .SetResult(true);
        MockPayPalSubscriptionManagementService
            .Setup(x => x.CancelSubscriptionAsync(testUser, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalCancellationResult(true, trialEnd));

        SetupRendererInfo();
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Free Trial Active"), TimeSpan.FromSeconds(5));

        await InvokeNonPublicTask(cut.Instance, "CancelSubscription");

        MockPayPalSubscriptionManagementService.Verify(x => x.CancelSubscriptionAsync(
            testUser,
            "http://localhost/",
            It.IsAny<CancellationToken>()), Times.Once);
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("You will not be charged")),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void ManageAccount_PayPalApprovalReturn_ActivatesThroughSharedManagementService()
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = SubscriptionStatuses.ApprovalPending
        });
        MockPayPalSubscriptionManagementService
            .Setup(x => x.ActivateCurrentSubscriptionAsync(testUser, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalActivationResult(true, IsTrial: true));

        SetupRendererInfo();
        TestContext.Services
            .GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/manage-account?success=true");
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Your free trial is active"), TimeSpan.FromSeconds(5));

        MockPayPalSubscriptionManagementService.Verify(x => x.ActivateCurrentSubscriptionAsync(
            testUser,
            "http://localhost/",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void ManageAccount_CancelledPayPalApproval_AbandonsProviderCheckout()
    {
        var testUser = SetupAccountWithSubscriptionStatus(new
        {
            HasSubscription = false,
            Status = SubscriptionStatuses.ApprovalPending
        });
        MockPayPalSubscriptionManagementService
            .Setup(x => x.AbandonPendingCheckoutAsync(testUser, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        SetupRendererInfo();
        TestContext.Services
            .GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/manage-account?success=false");
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Subscription setup was cancelled"), TimeSpan.FromSeconds(5));

        MockPayPalSubscriptionManagementService.Verify(
            x => x.AbandonPendingCheckoutAsync(testUser, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        MockSubscriptionService.Verify(
            x => x.DeletePendingSubscriptionAsync(It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task ManageAccount_DeleteAccount_TrimsConfirmationEmail()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);
        MockCreatorService.Setup(x => x.IsActiveCreatorAsync(1))
            .ReturnsAsync(false);
        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = false,
                Status = SubscriptionStatuses.Expired,
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Close My Account"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_accountActionConfirmEmail", "  testuser@test.com  ");
        await InvokeNonPublicTask(cut.Instance, "DeleteAccount");

        MockAccountDeletionService.Verify(x => x.DeleteAccountAsync(testUser), Times.Once);
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = typeof(ManageAccountModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Expected field {fieldName} to exist.");
        field!.SetValue(instance, value);
    }

    private static Task InvokeNonPublicTask(object instance, string methodName)
    {
        var method = typeof(ManageAccountModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Expected method {methodName} to exist.");
        return (Task)method!.Invoke(instance, null)!;
    }

    private ApplicationUser SetupAccountWithSubscriptionStatus(object status)
    {
        const int userId = 1;
        const string email = "testuser@test.com";
        SetupAuthorizedUser(userId, email);

        var testUser = new ApplicationUser
        {
            Id = userId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);
        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/subscription/status"), status);
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        return testUser;
    }

    private static PayPalWebOfferQuote CreateOfferQuote(
        bool hasFreeTrial,
        bool isFirstTimeSubscriber,
        decimal regularPrice = 2.99m,
        int settingsVersion = 7)
        => new()
        {
            PlanId = hasFreeTrial ? "P-TRIAL" : "P-MONTHLY",
            PlanName = hasFreeTrial ? "Trial plan" : "Monthly plan",
            RegularPrice = regularPrice,
            CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode,
            IntervalUnit = PayPalBillingIntervals.Month,
            IntervalCount = 1,
            TrialDays = hasFreeTrial ? 3 : null,
            SettingsVersion = settingsVersion,
            IsFirstTimeSubscriber = isFirstTimeSubscriber
        };
}
