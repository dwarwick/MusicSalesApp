using Bunit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Admin;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;

#nullable enable

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AdminSettingsPayPalOfferTests : BUnitTestBase
{
    private PayPalPlan _trialPlan = default!;
    private PayPalPlan _noTrialPlan = default!;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        _trialPlan = CreateMonthlyPlan("P-TRIAL", "Trial plan", 0.99m, trialDays: 3);
        _noTrialPlan = CreateMonthlyPlan("P-NO-TRIAL", "No-trial plan", 0.99m);

        TestContext.Services.AddSingleton(new Mock<IHubContext<MaintenanceHub>>().Object);
        SetupRendererInfo();
    }

    [Test]
    public void AdminSettings_RendersLiveAuthoritativePayPalTermsAndPlanIds()
    {
        ConfigureLivePlans(_trialPlan, _noTrialPlan);
        MockAppSettingsService
            .Setup(service => service.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync(new PayPalWebSubscriptionOffer
            {
                Version = 2,
                UpdatedAtUtc = DateTime.UtcNow,
                PrimaryPlan = CreateSnapshot(_trialPlan),
                ResubscriberPlan = CreateSnapshot(_noTrialPlan)
            });

        var cut = TestContext.Render<AdminSettings>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("PayPal Web Subscription Offer"));
            Assert.That(cut.Markup, Does.Contain("3 days free, then $0.99 USD/month"));
            Assert.That(cut.Markup, Does.Contain("P-TRIAL"));
            Assert.That(cut.Markup, Does.Contain("P-NO-TRIAL"));
            Assert.That(cut.Markup, Does.Not.Contain("Subscription Price (USD)"));
        });

        MockAppSettingsService.Verify(
            service => service.GetSubscriptionPriceAsync(),
            Times.Never);
    }

    [Test]
    public async Task SavePayPalOfferAsync_SavesValidatedTrialAndMatchingNoTrialPlan()
    {
        ConfigureLivePlans(_trialPlan, _noTrialPlan);
        PayPalWebSubscriptionOffer? capturedOffer = null;
        MockAppSettingsService
            .Setup(service => service.SetPayPalWebSubscriptionOfferAsync(It.IsAny<PayPalWebSubscriptionOffer>()))
            .Callback((PayPalWebSubscriptionOffer offer) => capturedOffer = offer)
            .ReturnsAsync((PayPalWebSubscriptionOffer offer) => offer with
            {
                Version = 4,
                UpdatedAtUtc = DateTime.UtcNow
            });

        var cut = TestContext.Render<AdminSettingsLogicTestComponent>();
        cut.WaitForAssertion(() => Assert.That(cut.Instance.IsLoaded, Is.True));
        await cut.InvokeAsync(() => cut.Instance.SelectAndSaveAsync(_trialPlan.Id, _noTrialPlan.Id));

        Assert.Multiple(() =>
        {
            Assert.That(capturedOffer, Is.Not.Null);
            Assert.That(capturedOffer!.PrimaryPlan.TrialDays, Is.EqualTo(3));
            Assert.That(capturedOffer.PrimaryPlan.RegularPrice, Is.EqualTo(0.99m));
            Assert.That(capturedOffer.ResubscriberPlan!.TrialDays, Is.Null);
            Assert.That(capturedOffer.ResubscriberPlan.Id, Is.EqualTo(_noTrialPlan.Id));
            Assert.That(cut.Instance.SavedVersion, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task SavePayPalOfferAsync_RejectsMismatchedNoTrialCompanion()
    {
        var mismatchedPlan = CreateMonthlyPlan("P-WRONG-PRICE", "Wrong price", 2.99m);
        ConfigureLivePlans(_trialPlan, mismatchedPlan);

        var cut = TestContext.Render<AdminSettingsLogicTestComponent>();
        cut.WaitForAssertion(() => Assert.That(cut.Instance.IsLoaded, Is.True));
        await cut.InvokeAsync(() => cut.Instance.SelectAndSaveAsync(_trialPlan.Id, mismatchedPlan.Id));

        Assert.That(
            cut.Instance.ValidationErrors,
            Has.Some.Contains("must match the primary plan's regular price, currency, and billing cadence"));
        MockAppSettingsService.Verify(
            service => service.SetPayPalWebSubscriptionOfferAsync(It.IsAny<PayPalWebSubscriptionOffer>()),
            Times.Never);
    }

    [Test]
    public async Task SavePayPalOfferAsync_AllowsMatchingDailyPlansInSandboxMode()
    {
        ConfigureSandboxMode(true);
        var dailyTrial = CreatePlan(
            "P-DAILY-TRIAL",
            "One-day sandbox trial",
            0.99m,
            PayPalBillingIntervals.Day,
            regularIntervalCount: 1,
            trialDays: 1);
        var dailyNoTrial = CreatePlan(
            "P-DAILY-NO-TRIAL",
            "Daily sandbox renewal",
            0.99m,
            PayPalBillingIntervals.Day,
            regularIntervalCount: 1);
        ConfigureLivePlans(dailyTrial, dailyNoTrial);
        PayPalWebSubscriptionOffer? capturedOffer = null;
        MockAppSettingsService
            .Setup(service => service.SetPayPalWebSubscriptionOfferAsync(It.IsAny<PayPalWebSubscriptionOffer>()))
            .Callback((PayPalWebSubscriptionOffer offer) => capturedOffer = offer)
            .ReturnsAsync((PayPalWebSubscriptionOffer offer) => offer with
            {
                Version = 5,
                UpdatedAtUtc = DateTime.UtcNow
            });

        var cut = TestContext.Render<AdminSettingsLogicTestComponent>();
        cut.WaitForAssertion(() => Assert.That(cut.Instance.IsLoaded, Is.True));
        await cut.InvokeAsync(() => cut.Instance.SelectAndSaveAsync(dailyTrial.Id, dailyNoTrial.Id));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Instance.ValidationErrors, Is.Empty);
            Assert.That(capturedOffer, Is.Not.Null);
            Assert.That(capturedOffer!.PrimaryPlan.TrialDays, Is.EqualTo(1));
            Assert.That(capturedOffer.PrimaryPlan.IntervalUnit, Is.EqualTo(PayPalBillingIntervals.Day));
            Assert.That(capturedOffer.PrimaryPlan.IntervalCount, Is.EqualTo(1));
            Assert.That(capturedOffer.ResubscriberPlan!.IntervalUnit, Is.EqualTo(PayPalBillingIntervals.Day));
            Assert.That(capturedOffer.ResubscriberPlan.IntervalCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SavePayPalOfferAsync_RejectsDailyPlansOutsideSandboxMode()
    {
        ConfigureSandboxMode(false);
        var dailyTrial = CreatePlan(
            "P-DAILY-TRIAL",
            "One-day trial",
            0.99m,
            PayPalBillingIntervals.Day,
            regularIntervalCount: 1,
            trialDays: 1);
        var dailyNoTrial = CreatePlan(
            "P-DAILY-NO-TRIAL",
            "Daily renewal",
            0.99m,
            PayPalBillingIntervals.Day,
            regularIntervalCount: 1);
        ConfigureLivePlans(dailyTrial, dailyNoTrial);

        var cut = TestContext.Render<AdminSettingsLogicTestComponent>();
        cut.WaitForAssertion(() => Assert.That(cut.Instance.IsLoaded, Is.True));
        await cut.InvokeAsync(() => cut.Instance.SelectAndSaveAsync(dailyTrial.Id, dailyNoTrial.Id));

        Assert.Multiple(() =>
        {
            Assert.That(
                cut.Instance.ValidationErrors,
                Has.Some.Contains("first-time subscriber plan must bill once per month outside PayPal sandbox mode"));
            Assert.That(
                cut.Instance.ValidationErrors,
                Has.Some.Contains("returning-subscriber plan must bill once per month outside PayPal sandbox mode"));
        });
        MockAppSettingsService.Verify(
            service => service.SetPayPalWebSubscriptionOfferAsync(It.IsAny<PayPalWebSubscriptionOffer>()),
            Times.Never);
    }

    [Test]
    public async Task RefreshPayPalPlansAsync_PreservesSelectionsAndOptions_WhenPayPalFails()
    {
        ConfigureLivePlans(_trialPlan, _noTrialPlan);

        var cut = TestContext.Render<AdminSettingsLogicTestComponent>();
        cut.WaitForAssertion(() => Assert.That(cut.Instance.IsLoaded, Is.True));
        cut.Instance.Select(_trialPlan.Id, _noTrialPlan.Id);
        var originalOptionCount = cut.Instance.OptionCount;

        MockPayPalSubscriptionApiService
            .Setup(service => service.GetActivePlansAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PayPal unavailable"));

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        Assert.Multiple(() =>
        {
            Assert.That(cut.Instance.PrimaryPlanId, Is.EqualTo(_trialPlan.Id));
            Assert.That(cut.Instance.ResubscriberPlanId, Is.EqualTo(_noTrialPlan.Id));
            Assert.That(cut.Instance.OptionCount, Is.EqualTo(originalOptionCount));
            Assert.That(cut.Instance.ErrorMessage, Does.Contain("preserved"));
        });
    }

    private void ConfigureLivePlans(params PayPalPlan[] plans)
    {
        MockPayPalSubscriptionApiService
            .Setup(service => service.GetActivePlansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(plans);

        foreach (var plan in plans)
        {
            MockPayPalSubscriptionApiService
                .Setup(service => service.GetPlanAsync(plan.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plan);
        }
    }

    private static PayPalPlan CreateMonthlyPlan(
        string id,
        string name,
        decimal price,
        int? trialDays = null)
        => CreatePlan(
            id,
            name,
            price,
            PayPalBillingIntervals.Month,
            regularIntervalCount: 1,
            trialDays: trialDays);

    private static PayPalPlan CreatePlan(
        string id,
        string name,
        decimal price,
        string regularIntervalUnit,
        int regularIntervalCount,
        int? trialDays = null)
    {
        var billingCycles = new List<PayPalBillingCycle>();
        if (trialDays.HasValue)
        {
            billingCycles.Add(new PayPalBillingCycle
            {
                TenureType = PayPalBillingTenureTypes.Trial,
                Sequence = 1,
                TotalCycles = 1,
                IntervalUnit = PayPalBillingIntervals.Day,
                IntervalCount = trialDays.Value,
                FixedPrice = decimal.Zero,
                CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode
            });
        }

        billingCycles.Add(new PayPalBillingCycle
        {
            TenureType = PayPalBillingTenureTypes.Regular,
            Sequence = trialDays.HasValue ? 2 : 1,
            TotalCycles = 0,
            IntervalUnit = regularIntervalUnit,
            IntervalCount = regularIntervalCount,
            FixedPrice = price,
            CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode
        });

        return new PayPalPlan
        {
            Id = id,
            ProductId = $"PRODUCT-{id}",
            Name = name,
            Status = PayPalPlanStatuses.Active,
            BillingCycles = billingCycles
        };
    }

    private void ConfigureSandboxMode(bool sandboxMode)
    {
        var configuration = TestContext.Services.GetRequiredService<IConfiguration>();
        configuration[PayPalConfigurationKeys.SandboxMode] = sandboxMode.ToString();
    }

    private static PayPalWebPlanSnapshot CreateSnapshot(PayPalPlan plan)
    {
        return new PayPalWebPlanSnapshot
        {
            Id = plan.Id,
            Name = plan.Name,
            Status = plan.Status,
            RegularPrice = plan.RegularPrice,
            CurrencyCode = plan.CurrencyCode,
            IntervalUnit = plan.IntervalUnit,
            IntervalCount = plan.IntervalCount,
            TrialDays = plan.TrialDays
        };
    }

    private sealed class AdminSettingsLogicTestComponent : AdminSettingsModel
    {
        public bool IsLoaded => !_isLoading;
        public IReadOnlyList<string> ValidationErrors => _payPalOfferValidationErrors;
        public int OptionCount => _payPalPlanOptions.Count;
        public string? PrimaryPlanId => _selectedPrimaryPayPalPlanId;
        public string? ResubscriberPlanId => _selectedResubscriberPayPalPlanId;
        public string? ErrorMessage => _payPalOfferErrorMessage;
        public int? SavedVersion => _savedPayPalWebOffer?.Version;

        public void Select(string primaryPlanId, string? resubscriberPlanId)
        {
            _selectedPrimaryPayPalPlanId = primaryPlanId;
            _selectedResubscriberPayPalPlanId = resubscriberPlanId;
        }

        public async Task SelectAndSaveAsync(string primaryPlanId, string? resubscriberPlanId)
        {
            Select(primaryPlanId, resubscriberPlanId);
            await SavePayPalOfferAsync();
        }

        public Task RefreshAsync() => RefreshPayPalPlansAsync();
    }
}
