#nullable enable

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PayPalSubscriptionManagementServiceTests
{
    private Mock<IPayPalSubscriptionApiService> _payPalApi = null!;
    private Mock<ISubscriptionService> _subscriptionService = null!;
    private Mock<IAppSettingsService> _appSettingsService = null!;
    private Mock<ISubscriptionConfirmationEmailService> _confirmationEmailService = null!;
    private Mock<IAccountEmailService> _accountEmailService = null!;
    private Mock<IPayPalSubscriptionAnomalyService> _anomalyService = null!;
    private Mock<UserManager<ApplicationUser>> _userManager = null!;
    private PayPalSubscriptionManagementService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _payPalApi = new Mock<IPayPalSubscriptionApiService>();
        _subscriptionService = new Mock<ISubscriptionService>();
        _appSettingsService = new Mock<IAppSettingsService>();
        _confirmationEmailService = new Mock<ISubscriptionConfirmationEmailService>();
        _accountEmailService = new Mock<IAccountEmailService>();
        _anomalyService = new Mock<IPayPalSubscriptionAnomalyService>();

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _userManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PayPal:ReturnBaseUrl"] = "https://subscriptions.example"
            })
            .Build();

        _service = new PayPalSubscriptionManagementService(
            _payPalApi.Object,
            _subscriptionService.Object,
            _appSettingsService.Object,
            _confirmationEmailService.Object,
            _accountEmailService.Object,
            _anomalyService.Object,
            _userManager.Object,
            configuration,
            Mock.Of<ILogger<PayPalSubscriptionManagementService>>());
    }

    [Test]
    public async Task GetOfferQuoteAsync_SelectsTrialOnlyForFirstActivatedSubscription()
    {
        var offer = CreateOffer();
        _appSettingsService.Setup(service => service.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync(offer);
        _subscriptionService.SetupSequence(service => service.HasPriorActivatedSubscriptionAsync(42))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        var firstSubscription = await _service.GetOfferQuoteAsync(42);
        var returningSubscription = await _service.GetOfferQuoteAsync(42);

        Assert.Multiple(() =>
        {
            Assert.That(firstSubscription.PlanId, Is.EqualTo("P-TRIAL"));
            Assert.That(firstSubscription.TrialDays, Is.EqualTo(3));
            Assert.That(firstSubscription.IsFirstTimeSubscriber, Is.True);
            Assert.That(returningSubscription.PlanId, Is.EqualTo("P-NO-TRIAL"));
            Assert.That(returningSubscription.TrialDays, Is.Null);
            Assert.That(returningSubscription.IsFirstTimeSubscriber, Is.False);
            Assert.That(returningSubscription.RegularPrice, Is.EqualTo(firstSubscription.RegularPrice));
        });
    }

    [Test]
    public void GetOfferQuoteAsync_Throws_WhenPayPalWebOfferIsNotConfigured()
    {
        _appSettingsService.Setup(service => service.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync((PayPalWebSubscriptionOffer?)null);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.GetOfferQuoteAsync(42));

        Assert.That(exception!.Message, Does.Contain("has not been configured"));
        _payPalApi.Verify(
            service => service.GetActivePlansAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task CreateSubscriptionAsync_RejectsStaleDisplayedOfferBeforeCallingPayPal()
    {
        var offer = CreateOffer(version: 12);
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.HasPriorActivatedSubscriptionAsync(user.Id))
            .ReturnsAsync(false);
        _appSettingsService.Setup(service => service.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync(offer);

        var result = await _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: 11,
            displayedPlanId: "P-TRIAL",
            fallbackBaseUrl: "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("trial eligibility changed"));
        });
        _payPalApi.Verify(
            service => service.GetPlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _payPalApi.Verify(
            service => service.CreateSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _subscriptionService.Verify(
            service => service.CreateSubscriptionAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<decimal>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task ActivateCurrentSubscriptionAsync_AcceptsProviderConfirmedActiveTrialWithoutPayment()
    {
        var trialStart = DateTime.UtcNow.AddMinutes(-5);
        var trialEnd = DateTime.UtcNow.AddDays(3);
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var pending = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-TRIAL",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };
        var activeTrial = new Subscription
        {
            Id = pending.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = pending.PayPalSubscriptionId,
            PayPalPlanId = pending.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            TrialStartDate = trialStart,
            TrialEndDate = trialEnd,
            NextBillingDate = trialEnd
        };

        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = pending.PayPalSubscriptionId,
                PlanId = pending.PayPalPlanId,
                Status = SubscriptionStatuses.Active,
                StartTime = new DateTimeOffset(trialStart),
                TrialEndTime = new DateTimeOffset(trialEnd),
                NextBillingTime = new DateTimeOffset(trialEnd),
                IsInTrial = true,
                Plan = CreateTrialPlan()
            });
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(pending.PayPalSubscriptionId))
            .ReturnsAsync(pending);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.Active
                    && state.TrialStartDate == trialStart
                    && state.TrialEndDate == trialEnd
                    && state.NextBillingDate == trialEnd
                    && !state.LastPaymentDate.HasValue)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = activeTrial,
                PreviousStatus = SubscriptionStatuses.ApprovalPending,
                BecameActive = true,
                ShouldSendTrialActivationEmail = true
            });
        _userManager.Setup(manager => manager.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _confirmationEmailService.Setup(service => service.SendTrialStartedAsync(
                user,
                activeTrial,
                "https://subscriptions.example"))
            .ReturnsAsync(true);

        var result = await _service.ActivateCurrentSubscriptionAsync(
            user,
            "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.IsTrial, Is.True);
            Assert.That(activeTrial.LastPaymentDate, Is.Null);
        });
        _subscriptionService.Verify(
            service => service.MarkTrialActivationEmailSentAsync(activeTrial.Id),
            Times.Once);
    }

    [TestCase(2)]
    [TestCase(-1)]
    public async Task CancelSubscriptionAsync_CancelsAtPayPalBeforePersistingExactTrialEnd(int trialEndDaysFromNow)
    {
        var trialStart = DateTime.UtcNow.AddHours(-2);
        var exactTrialEnd = DateTime.UtcNow.AddDays(trialEndDaysFromNow).AddSeconds(37);
        var user = new ApplicationUser
        {
            Id = 42,
            Email = "listener@example.com",
            UserName = "listener"
        };
        var activeTrial = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-TRIAL",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.Active,
            TrialStartDate = trialStart,
            TrialEndDate = exactTrialEnd,
            NextBillingDate = exactTrialEnd
        };
        var cancelledTrial = new Subscription
        {
            Id = activeTrial.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = activeTrial.PayPalSubscriptionId,
            PayPalPlanId = activeTrial.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            TrialStartDate = trialStart,
            TrialEndDate = exactTrialEnd,
            NextBillingDate = exactTrialEnd,
            EndDate = exactTrialEnd
        };
        var details = new PayPalSubscriptionDetails
        {
            Id = activeTrial.PayPalSubscriptionId,
            PlanId = activeTrial.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            StartTime = new DateTimeOffset(trialStart),
            TrialEndTime = new DateTimeOffset(exactTrialEnd),
            NextBillingTime = new DateTimeOffset(exactTrialEnd),
            IsInTrial = true,
            Plan = CreateTrialPlan()
        };

        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync(activeTrial);

        var sequence = new MockSequence();
        _payPalApi.InSequence(sequence)
            .Setup(service => service.GetSubscriptionAsync(
                activeTrial.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _subscriptionService.InSequence(sequence)
            .Setup(service => service.ReconcilePayPalSubscriptionAsync(
                activeTrial.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state => state.TrialEndDate == exactTrialEnd)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = activeTrial,
                PreviousStatus = SubscriptionStatuses.Active
            });
        _payPalApi.InSequence(sequence)
            .Setup(service => service.CancelSubscriptionAsync(
                activeTrial.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _subscriptionService.InSequence(sequence)
            .Setup(service => service.CancelPayPalSubscriptionAsync(
                activeTrial.PayPalSubscriptionId,
                exactTrialEnd))
            .ReturnsAsync(true);
        _subscriptionService.InSequence(sequence)
            .Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(cancelledTrial);
        _accountEmailService.Setup(service => service.SendSubscriptionCancelledEmailAsync(
                user.Email!,
                user.UserName!,
                exactTrialEnd,
                BillingSources.PayPal,
                user.TimeZoneId,
                "https://subscriptions.example"))
            .ReturnsAsync(true);

        var result = await _service.CancelSubscriptionAsync(user, "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.EndDate, Is.EqualTo(exactTrialEnd));
        });
        _subscriptionService.Verify(
            service => service.CancelPayPalSubscriptionAsync(
                activeTrial.PayPalSubscriptionId,
                exactTrialEnd),
            Times.Once);
        _subscriptionService.Verify(
            service => service.CancelSubscriptionAsync(user.Id),
            Times.Never);
    }

    [Test]
    public async Task CancelSubscriptionAsync_PaidSubscription_StopsRenewalAndPreservesConfirmedPaidThroughDate()
    {
        var lastPayment = DateTime.UtcNow.AddDays(-20);
        var paidThrough = DateTime.UtcNow.AddDays(10).AddSeconds(19);
        var user = new ApplicationUser
        {
            Id = 42,
            Email = "listener@example.com",
            UserName = "listener"
        };
        var active = new Subscription
        {
            Id = 8,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PAID-CANCEL",
            PayPalPlanId = "P-NO-TRIAL",
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = lastPayment,
            NextBillingDate = paidThrough,
            EndDate = paidThrough
        };
        var cancelled = new Subscription
        {
            Id = active.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = active.PayPalSubscriptionId,
            PayPalPlanId = active.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            LastPaymentDate = lastPayment,
            NextBillingDate = paidThrough,
            EndDate = paidThrough
        };
        var details = new PayPalSubscriptionDetails
        {
            Id = active.PayPalSubscriptionId,
            PlanId = active.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            LastPaymentTime = new DateTimeOffset(lastPayment),
            NextBillingTime = new DateTimeOffset(paidThrough),
            Plan = CreateNoTrialPlan()
        };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync(active);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                active.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                active.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.LastPaymentDate == lastPayment
                    && state.NextBillingDate == paidThrough)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = active,
                PreviousStatus = SubscriptionStatuses.Active,
                PreviousLastPaymentDate = lastPayment
            });
        _payPalApi.Setup(service => service.CancelSubscriptionAsync(
                active.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                active.PayPalSubscriptionId,
                paidThrough))
            .ReturnsAsync(true);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(cancelled);
        _accountEmailService.Setup(service => service.SendSubscriptionCancelledEmailAsync(
                user.Email!,
                user.UserName!,
                paidThrough,
                BillingSources.PayPal,
                user.TimeZoneId,
                "https://subscriptions.example"))
            .ReturnsAsync(true);

        var result = await _service.CancelSubscriptionAsync(user, "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.EndDate, Is.EqualTo(paidThrough));
        });
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
            active.PayPalSubscriptionId,
            PayPalSubscriptionDefaults.UserCancellationReason,
            It.IsAny<CancellationToken>()), Times.Once);
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
            active.PayPalSubscriptionId,
            paidThrough), Times.Once);
    }

    [Test]
    public async Task ReconcileSubscriptionAsync_FirstChargeDeclined_PassesTrialEndRetryAndFailureCountAtomically()
    {
        var trialStart = DateTime.UtcNow.AddDays(-3).AddMinutes(-2);
        var trialEnd = DateTime.UtcNow.AddMinutes(-2);
        var retryDate = DateTime.UtcNow.AddDays(5);
        var local = new Subscription
        {
            Id = 9,
            UserId = 42,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-FIRST-CHARGE-FAILED",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.Active,
            TrialStartDate = trialStart,
            TrialEndDate = trialEnd,
            EndDate = trialEnd
        };
        var expired = new Subscription
        {
            Id = local.Id,
            UserId = local.UserId,
            BillingSource = local.BillingSource,
            PayPalSubscriptionId = local.PayPalSubscriptionId,
            PayPalPlanId = local.PayPalPlanId,
            Status = SubscriptionStatuses.Expired,
            TrialStartDate = trialStart,
            TrialEndDate = trialEnd,
            NextBillingDate = retryDate,
            EndDate = trialEnd
        };
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                local.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = local.PayPalSubscriptionId,
                PlanId = local.PayPalPlanId,
                Status = SubscriptionStatuses.Active,
                StartTime = new DateTimeOffset(trialStart),
                TrialEndTime = new DateTimeOffset(trialEnd),
                NextBillingTime = new DateTimeOffset(retryDate),
                FailedPaymentsCount = 1,
                IsInTrial = false,
                HasBillingInfo = true,
                Plan = CreateTrialPlan(),
                CycleExecutions =
                [
                    new PayPalBillingCycleExecution
                    {
                        TenureType = PayPalBillingTenureTypes.Trial,
                        Sequence = 1,
                        CyclesCompleted = 1,
                        CyclesRemaining = 0,
                        TotalCycles = 1
                    }
                ]
            });
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(local.PayPalSubscriptionId))
            .ReturnsAsync(local);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                local.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.Active
                    && state.TrialEndDate == trialEnd
                    && state.NextBillingDate == retryDate
                    && state.FailedPaymentsCount == 1
                    && state.RegularIntervalUnit == PayPalBillingIntervals.Month
                    && state.RegularIntervalCount == 1
                    && !state.LastPaymentDate.HasValue)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = expired,
                PreviousStatus = SubscriptionStatuses.Active
            });

        var result = await _service.ReconcileSubscriptionAsync(
            local.PayPalSubscriptionId,
            "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Subscription.Status, Is.EqualTo(SubscriptionStatuses.Expired));
            Assert.That(result.Subscription.EndDate, Is.EqualTo(trialEnd));
            Assert.That(result.Subscription.NextBillingDate, Is.EqualTo(retryDate));
            Assert.That(result.Subscription.LastPaymentDate, Is.Null);
        });
    }

    [Test]
    public async Task CreateSubscriptionAsync_WhenPendingCheckoutCannotBeReached_DoesNotMutateOrStartAnotherCheckout()
    {
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var pending = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PENDING",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        // An unreachable PayPal, not a 404: the provider state is genuinely unknown, so the
        // pending row must be left exactly as it is.
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayPalSubscriptionApiException("temporary failure"));

        var result = await _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: 8,
            displayedPlanId: "P-TRIAL",
            fallbackBaseUrl: "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("could not be verified"));
        });
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _subscriptionService.Verify(
            service => service.DeletePendingSubscriptionAsync(user.Id),
            Times.Never);
        _payPalApi.Verify(service => service.CreateSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task CreateSubscriptionAsync_WhenPendingCheckoutNoLongerExistsAtPayPal_DropsItAndStartsANewCheckout()
    {
        // PayPal purges an agreement the buyer never approved within hours, so every later GET
        // 404s. Blocking on that stranded the user behind their own abandoned checkout until the
        // nightly sweep deleted the row - up to 72 hours - and each retry produced another 404.
        var user = new ApplicationUser { Id = 590, Email = "listener@example.com" };
        var offer = CreateOffer();
        var pending = new Subscription
        {
            Id = 44,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-ABANDONED",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending,
            CreatedAt = DateTime.UtcNow.AddHours(-8)
        };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _subscriptionService.Setup(service => service.HasPriorActivatedSubscriptionAsync(user.Id))
            .ReturnsAsync(false);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PayPalSubscriptionDetails?)null!);
        _appSettingsService.Setup(service => service.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync(offer);
        _payPalApi.Setup(service => service.GetPlanAsync("P-TRIAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTrialPlan());
        _payPalApi.Setup(service => service.CreateSubscriptionAsync(
                "P-TRIAL",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalCreatedSubscription("I-NEW", "https://paypal.example/approve"));
        _subscriptionService.Setup(service => service.CreateSubscriptionAsync(
                user.Id,
                "I-NEW",
                "P-TRIAL",
                0.99m,
                PayPalSubscriptionDefaults.UsdCurrencyCode,
                offer.Version,
                It.IsAny<DateTime>()))
            .ReturnsAsync(new Subscription
            {
                UserId = user.Id,
                PayPalSubscriptionId = "I-NEW",
                Status = SubscriptionStatuses.ApprovalPending
            });

        var result = await _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: offer.Version,
            displayedPlanId: offer.PrimaryPlan.Id,
            fallbackBaseUrl: "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.ApprovalUrl, Is.EqualTo("https://paypal.example/approve"));
            Assert.That(result.SubscriptionId, Is.EqualTo("I-NEW"));
        });
        _subscriptionService.Verify(
            service => service.DeletePendingSubscriptionAsync(user.Id),
            Times.Once);
        _payPalApi.Verify(service => service.CreateSubscriptionAsync(
                "P-TRIAL",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task CreateSubscriptionAsync_WhenSuspendedPayPalNoLongerExistsAtProvider_DoesNotBlockNewCheckout()
    {
        // "Still active or suspended with its billing provider" is a claim about the provider. A
        // provider-confirmed 404 refutes it, so the stale local status must not gate checkout.
        var user = new ApplicationUser { Id = 594 };
        var offer = CreateOffer();
        var suspended = new Subscription
        {
            Id = 45,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-GONE",
            Status = SubscriptionStatuses.Suspended
        };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(suspended);
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.HasPriorActivatedSubscriptionAsync(user.Id))
            .ReturnsAsync(false);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                suspended.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PayPalSubscriptionDetails?)null!);
        _appSettingsService.Setup(service => service.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync(offer);
        _payPalApi.Setup(service => service.GetPlanAsync("P-TRIAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTrialPlan());
        _payPalApi.Setup(service => service.CreateSubscriptionAsync(
                "P-TRIAL",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalCreatedSubscription("I-NEW", "https://paypal.example/approve"));
        _subscriptionService.Setup(service => service.CreateSubscriptionAsync(
                user.Id,
                "I-NEW",
                "P-TRIAL",
                0.99m,
                PayPalSubscriptionDefaults.UsdCurrencyCode,
                offer.Version,
                It.IsAny<DateTime>()))
            .ReturnsAsync(new Subscription
            {
                UserId = user.Id,
                PayPalSubscriptionId = "I-NEW",
                Status = SubscriptionStatuses.ApprovalPending
            });

        var result = await _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: offer.Version,
            displayedPlanId: offer.PrimaryPlan.Id,
            fallbackBaseUrl: "https://fallback.example");

        Assert.That(result.Success, Is.True);
        _payPalApi.Verify(service => service.CreateSubscriptionAsync(
                "P-TRIAL",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AbandonPendingCheckoutAsync_WhenProviderIsActive_PreservesHistoryAndDoesNotCancelFromStaleLocalState()
    {
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var pending = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-ACTIVE",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };
        var active = new Subscription
        {
            Id = pending.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = pending.PayPalSubscriptionId,
            PayPalPlanId = pending.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = DateTime.UtcNow.AddMinutes(-1),
            NextBillingDate = DateTime.UtcNow.AddMonths(1)
        };
        var details = new PayPalSubscriptionDetails
        {
            Id = pending.PayPalSubscriptionId,
            PlanId = pending.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            LastPaymentTime = new DateTimeOffset(active.LastPaymentDate!.Value),
            NextBillingTime = new DateTimeOffset(active.NextBillingDate!.Value),
            Plan = CreateTrialPlan()
        };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(pending.PayPalSubscriptionId))
            .ReturnsAsync(pending);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.Active)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = active,
                PreviousStatus = SubscriptionStatuses.ApprovalPending,
                BecameActive = true
            });

        var result = await _service.AbandonPendingCheckoutAsync(user);

        Assert.That(result, Is.False);
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _subscriptionService.Verify(
            service => service.DeletePendingSubscriptionAsync(user.Id),
            Times.Never);
    }

    [Test]
    public async Task AbandonPendingCheckoutAsync_WhenProviderLookupFails_LeavesPendingRecordUntouched()
    {
        var user = new ApplicationUser { Id = 42 };
        var pending = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PENDING",
            Status = SubscriptionStatuses.ApprovalPending
        };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayPalSubscriptionApiException("temporary failure"));

        var result = await _service.AbandonPendingCheckoutAsync(user);

        Assert.That(result, Is.False);
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _subscriptionService.Verify(
            service => service.DeletePendingSubscriptionAsync(user.Id),
            Times.Never);
    }

    [Test]
    public async Task AbandonPendingCheckoutAsync_WhenPayPalAcceptsPendingCancellation_CleansLocalRecordWithoutWaitingForStatusPropagation()
    {
        var user = new ApplicationUser { Id = 42 };
        var pending = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-ABANDONED-PENDING",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };
        var details = new PayPalSubscriptionDetails
        {
            Id = pending.PayPalSubscriptionId,
            PlanId = pending.PayPalPlanId,
            Status = SubscriptionStatuses.ApprovalPending,
            Plan = CreateTrialPlan()
        };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(pending.PayPalSubscriptionId))
            .ReturnsAsync(pending);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.ApprovalPending
                    && !state.LastPaymentDate.HasValue
                    && !state.TrialEndDate.HasValue)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = pending,
                PreviousStatus = SubscriptionStatuses.ApprovalPending
            });
        _payPalApi.Setup(service => service.CancelSubscriptionAsync(
                pending.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                pending.PayPalSubscriptionId,
                null))
            .ReturnsAsync(true);

        var result = await _service.AbandonPendingCheckoutAsync(user);

        Assert.That(result, Is.True);
        _payPalApi.Verify(service => service.GetSubscriptionAsync(
            pending.PayPalSubscriptionId,
            It.IsAny<CancellationToken>()), Times.Once);
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
            pending.PayPalSubscriptionId,
            null), Times.Once);
        _subscriptionService.Verify(service => service.DeletePendingSubscriptionAsync(user.Id), Times.Never);
    }

    [Test]
    public async Task AbandonPendingCheckoutAsync_ProviderCancelledWithoutActivation_ClearsStaleEntitlementDates()
    {
        var staleDate = DateTime.UtcNow.AddDays(3);
        var user = new ApplicationUser { Id = 42 };
        var pending = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-ABANDONED",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending,
            NextBillingDate = staleDate,
            EndDate = staleDate
        };
        var reconciledCancelled = new Subscription
        {
            Id = pending.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = pending.PayPalSubscriptionId,
            PayPalPlanId = pending.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            NextBillingDate = staleDate,
            EndDate = staleDate
        };
        var details = new PayPalSubscriptionDetails
        {
            Id = pending.PayPalSubscriptionId,
            PlanId = pending.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            NextBillingTime = new DateTimeOffset(staleDate),
            Plan = CreateTrialPlan()
        };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pending);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(pending.PayPalSubscriptionId))
            .ReturnsAsync(pending);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                pending.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.Cancelled
                    && !state.NextBillingDate.HasValue
                    && !state.TrialEndDate.HasValue)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = reconciledCancelled,
                PreviousStatus = SubscriptionStatuses.ApprovalPending
            });
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                pending.PayPalSubscriptionId,
                null))
            .ReturnsAsync(true);

        var result = await _service.AbandonPendingCheckoutAsync(user);

        Assert.That(result, Is.True);
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                pending.PayPalSubscriptionId,
                null),
            Times.Once);
        _subscriptionService.Verify(
            service => service.DeletePendingSubscriptionAsync(user.Id),
            Times.Never);
    }

    [Test]
    public async Task CancelSubscriptionAsync_WhenProviderAlreadyExpired_SkipsProviderCancelAndPersistsExactRow()
    {
        var providerEnd = DateTime.UtcNow.AddHours(-2);
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var expired = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-EXPIRED",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.Expired,
            NextBillingDate = providerEnd,
            EndDate = providerEnd
        };
        var cancelled = new Subscription
        {
            Id = expired.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = expired.PayPalSubscriptionId,
            PayPalPlanId = expired.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            EndDate = providerEnd
        };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(expired);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                expired.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = expired.PayPalSubscriptionId,
                PlanId = expired.PayPalPlanId,
                Status = SubscriptionStatuses.Expired,
                NextBillingTime = new DateTimeOffset(providerEnd),
                Plan = CreateTrialPlan()
            });
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                expired.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.Expired)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = expired,
                PreviousStatus = SubscriptionStatuses.Expired
            });
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                expired.PayPalSubscriptionId,
                providerEnd))
            .ReturnsAsync(true);
        _subscriptionService.SetupSequence(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(expired)
            .ReturnsAsync(cancelled);

        var result = await _service.CancelSubscriptionAsync(user, "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.EndDate, Is.EqualTo(providerEnd));
        });
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                expired.PayPalSubscriptionId,
                providerEnd),
            Times.Once);
    }

    [Test]
    public async Task CreateSubscriptionAsync_ReconcilesLocallyExpiredPayPalAndBlocksWhenProviderIsStillActive()
    {
        var user = new ApplicationUser { Id = 42 };
        var paymentDate = DateTime.UtcNow.AddMonths(-1);
        var nextBillingDate = DateTime.UtcNow.AddDays(2);
        var expired = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-RETRYING",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.Expired,
            EndDate = DateTime.UtcNow.AddMinutes(-1)
        };
        var providerActive = new Subscription
        {
            Id = expired.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = expired.PayPalSubscriptionId,
            PayPalPlanId = expired.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = paymentDate,
            NextBillingDate = nextBillingDate,
            EndDate = nextBillingDate
        };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(expired);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                expired.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = expired.PayPalSubscriptionId,
                PlanId = expired.PayPalPlanId,
                Status = SubscriptionStatuses.Active,
                LastPaymentTime = new DateTimeOffset(paymentDate),
                NextBillingTime = new DateTimeOffset(nextBillingDate),
                Plan = CreateTrialPlan()
            });
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(expired.PayPalSubscriptionId))
            .ReturnsAsync(expired);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                expired.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state => state.Status == SubscriptionStatuses.Active)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = providerActive,
                PreviousStatus = SubscriptionStatuses.Expired
            });

        var result = await _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: 8,
            displayedPlanId: "P-TRIAL",
            fallbackBaseUrl: "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("still active or suspended"));
        });
        _payPalApi.Verify(service => service.CreateSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task CreateSubscriptionAsync_WhenExpiredPayPalCannotBeRefreshed_BlocksSecondCheckout()
    {
        var user = new ApplicationUser { Id = 42 };
        var expired = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-UNKNOWN",
            Status = SubscriptionStatuses.Expired
        };
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(expired);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                expired.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayPalSubscriptionApiException("temporary failure"));

        var result = await _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: 8,
            displayedPlanId: "P-TRIAL",
            fallbackBaseUrl: "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("could not be verified"));
        });
        _payPalApi.Verify(service => service.CreateSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task CreateSubscriptionAsync_ConcurrentRequests_StartOnlyOneProviderCheckout()
    {
        var user = new ApplicationUser { Id = 73 };
        var offer = CreateOffer();
        var createEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource<PayPalCreatedSubscription>(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _subscriptionService.Setup(service => service.HasPriorActivatedSubscriptionAsync(user.Id))
            .ReturnsAsync(false);
        _appSettingsService.Setup(service => service.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync(offer);
        _payPalApi.Setup(service => service.GetPlanAsync("P-TRIAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTrialPlan());
        _payPalApi.Setup(service => service.CreateSubscriptionAsync(
                "P-TRIAL",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                createEntered.TrySetResult(true);
                return await releaseCreate.Task;
            });
        _subscriptionService.Setup(service => service.CreateSubscriptionAsync(
                user.Id,
                "I-NEW",
                "P-TRIAL",
                0.99m,
                PayPalSubscriptionDefaults.UsdCurrencyCode,
                offer.Version,
                It.IsAny<DateTime>()))
            .ReturnsAsync(new Subscription
            {
                UserId = user.Id,
                PayPalSubscriptionId = "I-NEW",
                Status = SubscriptionStatuses.ApprovalPending
            });

        var firstTask = _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: offer.Version,
            displayedPlanId: offer.PrimaryPlan.Id,
            fallbackBaseUrl: "https://fallback.example");
        await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await _service.CreateSubscriptionAsync(
            user,
            agreeToTerms: true,
            displayedOfferVersion: offer.Version,
            displayedPlanId: offer.PrimaryPlan.Id,
            fallbackBaseUrl: "https://fallback.example");
        releaseCreate.SetResult(new PayPalCreatedSubscription("I-NEW", "https://paypal.example/approve"));
        var first = await firstTask;

        Assert.Multiple(() =>
        {
            Assert.That(first.Success, Is.True);
            Assert.That(second.Success, Is.False);
            Assert.That(second.Error, Does.Contain("already being started"));
        });
        _payPalApi.Verify(service => service.CreateSubscriptionAsync(
                "P-TRIAL",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _subscriptionService.Verify(service => service.CreateSubscriptionAsync(
                user.Id,
                "I-NEW",
                "P-TRIAL",
                0.99m,
                PayPalSubscriptionDefaults.UsdCurrencyCode,
                offer.Version,
                It.Is<DateTime>(acceptedAt => acceptedAt.Kind == DateTimeKind.Utc)),
            Times.Once);
    }

    [Test]
    public async Task ActivateCurrentSubscriptionAsync_WithDifferentStoreEntitlement_StopsProviderActivePayPalAndPreservesHistory()
    {
        var trialStart = DateTime.UtcNow.AddHours(-1);
        var trialEnd = DateTime.UtcNow.AddDays(2);
        var user = new ApplicationUser { Id = 42 };
        var pendingPayPal = new Subscription
        {
            Id = 8,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-OVERLAP",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };
        var activeGoogle = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = "google-token",
            Status = SubscriptionStatuses.Active,
            TrialEndDate = trialEnd,
            EndDate = trialEnd
        };
        var cancelledPayPal = new Subscription
        {
            Id = pendingPayPal.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = pendingPayPal.PayPalSubscriptionId,
            PayPalPlanId = pendingPayPal.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            TrialStartDate = trialStart,
            TrialEndDate = trialEnd,
            EndDate = trialEnd
        };
        var activeDetails = new PayPalSubscriptionDetails
        {
            Id = pendingPayPal.PayPalSubscriptionId,
            PlanId = pendingPayPal.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            StartTime = new DateTimeOffset(trialStart),
            TrialEndTime = new DateTimeOffset(trialEnd),
            NextBillingTime = new DateTimeOffset(trialEnd),
            HasBillingInfo = true,
            IsInTrial = true,
            Plan = CreateTrialPlan()
        };
        var cancelledDetails = new PayPalSubscriptionDetails
        {
            Id = pendingPayPal.PayPalSubscriptionId,
            PlanId = pendingPayPal.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            StartTime = new DateTimeOffset(trialStart),
            TrialEndTime = new DateTimeOffset(trialEnd),
            NextBillingTime = new DateTimeOffset(trialEnd),
            HasBillingInfo = true,
            IsInTrial = true,
            Plan = CreateTrialPlan()
        };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pendingPayPal);
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync(activeGoogle);
        _payPalApi.SetupSequence(service => service.GetSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeDetails)
            .ReturnsAsync(cancelledDetails);
        _payPalApi.Setup(service => service.CancelSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(pendingPayPal.PayPalSubscriptionId))
            .ReturnsAsync(pendingPayPal);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.Cancelled
                    && state.TrialStartDate == trialStart
                    && state.TrialEndDate == trialEnd)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = cancelledPayPal,
                PreviousStatus = SubscriptionStatuses.ApprovalPending
            });
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                trialEnd))
            .ReturnsAsync(true);

        var result = await _service.ActivateCurrentSubscriptionAsync(
            user,
            "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("overlapping PayPal agreement was stopped"));
        });
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                trialEnd),
            Times.Once);
        _subscriptionService.Verify(service => service.DeletePendingSubscriptionAsync(user.Id), Times.Never);
    }

    [Test]
    public async Task ActivateCurrentSubscriptionAsync_WithDifferentStoreEntitlement_ClosesStillPendingPayPal()
    {
        var user = new ApplicationUser { Id = 42 };
        var pendingPayPal = new Subscription
        {
            Id = 8,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PENDING-OVERLAP",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };
        var activeApple = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.Apple,
            AppStoreOriginalTransactionId = "apple-active",
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = DateTime.UtcNow.AddDays(-1),
            EndDate = DateTime.UtcNow.AddDays(29)
        };
        var pendingDetails = new PayPalSubscriptionDetails
        {
            Id = pendingPayPal.PayPalSubscriptionId,
            PlanId = pendingPayPal.PayPalPlanId,
            Status = SubscriptionStatuses.ApprovalPending,
            Plan = CreateTrialPlan()
        };
        var cancelledDetails = new PayPalSubscriptionDetails
        {
            Id = pendingPayPal.PayPalSubscriptionId,
            PlanId = pendingPayPal.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled,
            Plan = CreateTrialPlan()
        };
        var cancelledPayPal = new Subscription
        {
            Id = pendingPayPal.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = pendingPayPal.PayPalSubscriptionId,
            PayPalPlanId = pendingPayPal.PayPalPlanId,
            Status = SubscriptionStatuses.Cancelled
        };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(pendingPayPal);
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync(activeApple);
        _payPalApi.SetupSequence(service => service.GetSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(pendingDetails)
            .ReturnsAsync(cancelledDetails);
        _payPalApi.Setup(service => service.CancelSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(pendingPayPal.PayPalSubscriptionId))
            .ReturnsAsync(pendingPayPal);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.Status == SubscriptionStatuses.Cancelled
                    && !state.TrialStartDate.HasValue
                    && !state.TrialEndDate.HasValue)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = cancelledPayPal,
                PreviousStatus = SubscriptionStatuses.ApprovalPending
            });
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                null))
            .ReturnsAsync(true);

        var result = await _service.ActivateCurrentSubscriptionAsync(
            user,
            "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("overlapping PayPal agreement was stopped"));
        });
        _payPalApi.Verify(service => service.CancelSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                PayPalSubscriptionDefaults.UserCancellationReason,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                pendingPayPal.PayPalSubscriptionId,
                null),
            Times.Once);
        _subscriptionService.Verify(service => service.DeletePendingSubscriptionAsync(user.Id), Times.Never);
    }

    [Test]
    public async Task ReconcileSubscriptionAsync_DuplicateRenewalSendsConversionEmailOnlyOnce()
    {
        var paymentTime = DateTime.UtcNow.AddMinutes(-1);
        var renewalDate = DateTime.UtcNow.AddMonths(1);
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var local = new Subscription
        {
            Id = 7,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-CONVERTED",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.Active,
            TrialStartDate = DateTime.UtcNow.AddDays(-3),
            TrialEndDate = paymentTime
        };
        var converted = new Subscription
        {
            Id = local.Id,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = local.PayPalSubscriptionId,
            PayPalPlanId = local.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            TrialStartDate = local.TrialStartDate,
            TrialEndDate = local.TrialEndDate,
            LastPaymentDate = paymentTime,
            NextBillingDate = renewalDate,
            TrialConvertedAt = paymentTime
        };
        var details = new PayPalSubscriptionDetails
        {
            Id = local.PayPalSubscriptionId,
            PlanId = local.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            StartTime = new DateTimeOffset(local.TrialStartDate!.Value),
            LastPaymentTime = new DateTimeOffset(paymentTime),
            NextBillingTime = new DateTimeOffset(renewalDate),
            IsInTrial = false,
            Plan = CreateTrialPlan()
        };

        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                local.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(local.PayPalSubscriptionId))
            .ReturnsAsync(local);
        _subscriptionService.SetupSequence(service => service.ReconcilePayPalSubscriptionAsync(
                local.PayPalSubscriptionId,
                It.IsAny<PayPalSubscriptionReconciliation>()))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = converted,
                PreviousStatus = SubscriptionStatuses.Active,
                PreviousLastPaymentDate = null,
                BecamePaid = true,
                TrialConverted = true,
                ShouldSendTrialConversionEmail = true
            })
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = converted,
                PreviousStatus = SubscriptionStatuses.Active,
                PreviousLastPaymentDate = paymentTime
            });
        _userManager.Setup(manager => manager.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _confirmationEmailService.Setup(service => service.SendTrialConvertedAsync(
                user,
                converted,
                "https://subscriptions.example"))
            .ReturnsAsync(true);

        var first = await _service.ReconcileSubscriptionAsync(
            local.PayPalSubscriptionId,
            "https://fallback.example");
        var duplicate = await _service.ReconcileSubscriptionAsync(
            local.PayPalSubscriptionId,
            "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(duplicate, Is.Not.Null);
            Assert.That(first!.TrialConverted, Is.True);
            Assert.That(duplicate!.TrialConverted, Is.False);
        });
        _confirmationEmailService.Verify(
            service => service.SendTrialConvertedAsync(
                user,
                converted,
                "https://subscriptions.example"),
            Times.Once);
        _subscriptionService.Verify(
            service => service.MarkTrialConversionEmailSentAsync(converted.Id),
            Times.Once);
    }

    [Test]
    public async Task ReconcileSubscriptionAsync_MissedTrialActivation_RecordsHistoricalTrialAndConversion()
    {
        var trialStart = DateTime.UtcNow.AddDays(-3);
        var trialEnd = DateTime.UtcNow.AddMinutes(-2);
        var paymentTime = trialEnd.AddSeconds(5);
        var nextBilling = paymentTime.AddMonths(1);
        var local = new Subscription
        {
            Id = 9,
            UserId = 42,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-MISSED-CONVERSION",
            PayPalPlanId = "P-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };
        var converted = new Subscription
        {
            Id = local.Id,
            UserId = local.UserId,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = local.PayPalSubscriptionId,
            PayPalPlanId = local.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            TrialStartDate = trialStart,
            TrialEndDate = trialEnd,
            LastPaymentDate = paymentTime,
            TrialConvertedAt = paymentTime,
            NextBillingDate = nextBilling
        };

        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                local.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = local.PayPalSubscriptionId,
                PlanId = local.PayPalPlanId,
                Status = SubscriptionStatuses.Active,
                StartTime = new DateTimeOffset(trialStart),
                TrialEndTime = new DateTimeOffset(trialEnd),
                LastPaymentTime = new DateTimeOffset(paymentTime),
                NextBillingTime = new DateTimeOffset(nextBilling),
                HasBillingInfo = true,
                Plan = CreateTrialPlan()
            });
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(local.PayPalSubscriptionId))
            .ReturnsAsync(local);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                local.PayPalSubscriptionId,
                It.Is<PayPalSubscriptionReconciliation>(state =>
                    state.TrialStartDate == trialStart
                    && state.TrialEndDate == trialEnd
                    && state.LastPaymentDate == paymentTime
                    && state.NextBillingDate == nextBilling)))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = converted,
                PreviousStatus = SubscriptionStatuses.ApprovalPending,
                BecameActive = true,
                BecamePaid = true,
                TrialConverted = true
            });

        var result = await _service.ReconcileSubscriptionAsync(
            local.PayPalSubscriptionId,
            "https://fallback.example");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Subscription.TrialStartDate, Is.EqualTo(trialStart));
            Assert.That(result.Subscription.TrialEndDate, Is.EqualTo(trialEnd));
            Assert.That(result.Subscription.TrialConvertedAt, Is.EqualTo(paymentTime));
        });
    }

    [Test]
    public async Task ResolveCurrentMismatchAsync_RestoresEntitlement_WhenPayPalProvidesPaymentEvidence()
    {
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var subscription = new Subscription
        {
            Id = 19,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-RESTORE",
            PayPalPlanId = "P-NO-TRIAL",
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = DateTime.UtcNow.AddDays(-1),
            EndDate = DateTime.UtcNow.AddDays(29)
        };
        var details = new PayPalSubscriptionDetails
        {
            Id = subscription.PayPalSubscriptionId,
            PlanId = subscription.PayPalPlanId,
            Status = SubscriptionStatuses.Active,
            LastPaymentTime = new DateTimeOffset(subscription.LastPaymentDate.Value),
            NextBillingTime = new DateTimeOffset(subscription.EndDate.Value),
            Plan = CreateNoTrialPlan()
        };
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(subscription);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(subscription.PayPalSubscriptionId))
            .ReturnsAsync(subscription);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<PayPalSubscriptionReconciliation>()))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult { Subscription = subscription });
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync(subscription);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);

        var result = await _service.ResolveCurrentMismatchAsync(user, "https://streamtunes.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PayPalMismatchResolutionStatuses.EntitlementRestored));
            Assert.That(result.EntitlementEndDate, Is.EqualTo(subscription.EndDate));
        });
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(subscription.Id, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    [TestCase(SubscriptionStatuses.Active, PayPalMismatchResolutionStatuses.ActiveWithoutEntitlement)]
    [TestCase(SubscriptionStatuses.Suspended, PayPalMismatchResolutionStatuses.Suspended)]
    public async Task ResolveCurrentMismatchAsync_KeepsSubscriptionSignupBlocked_WhenMismatchRemains(
        string providerStatus,
        string expectedStatus)
    {
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var subscription = new Subscription
        {
            Id = 20,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-UNRESOLVED",
            PayPalPlanId = "P-NO-TRIAL",
            Status = providerStatus
        };
        var episode = new PayPalSubscriptionAnomaly
        {
            Id = 3,
            SubscriptionId = subscription.Id,
            CorrelationId = "d7339bcf-80aa-4770-97e2-d8f6c657d11b"
        };
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(subscription);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(subscription.PayPalSubscriptionId))
            .ReturnsAsync(subscription);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<PayPalSubscriptionReconciliation>()))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult { Subscription = subscription });
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = subscription.PayPalSubscriptionId,
                PlanId = subscription.PayPalPlanId,
                Status = providerStatus,
                FailedPaymentsCount = providerStatus == SubscriptionStatuses.Suspended ? 1 : 0,
                Plan = CreateNoTrialPlan()
            });
        _userManager.Setup(manager => manager.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _anomalyService.Setup(service => service.RecordMismatchAsync(
                subscription,
                user,
                It.IsAny<PayPalSubscriptionDetails>(),
                null,
                "https://streamtunes.example",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(episode);
        _anomalyService.Setup(service => service.GetOpenEpisodeAsync(
                subscription.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(episode);

        var result = await _service.ResolveCurrentMismatchAsync(user, "https://streamtunes.example");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(result.CorrelationId, Is.EqualTo(episode.CorrelationId));
        });
    }

    [Test]
    public async Task ResolveCurrentMismatchAsync_ReturnsAgreementEnded_AndResolvesEpisode()
    {
        var user = new ApplicationUser { Id = 42 };
        var subscription = new Subscription
        {
            Id = 21,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-ENDED",
            PayPalPlanId = "P-NO-TRIAL",
            Status = SubscriptionStatuses.Expired,
            EndDate = DateTime.UtcNow.AddDays(-1)
        };
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(subscription);
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(subscription.PayPalSubscriptionId))
            .ReturnsAsync(subscription);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<PayPalSubscriptionReconciliation>()))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult { Subscription = subscription });
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = subscription.PayPalSubscriptionId,
                PlanId = subscription.PayPalPlanId,
                Status = SubscriptionStatuses.Expired,
                Plan = CreateNoTrialPlan()
            });

        var result = await _service.ResolveCurrentMismatchAsync(user, "https://streamtunes.example");

        Assert.That(result.Status, Is.EqualTo(PayPalMismatchResolutionStatuses.AgreementEnded));
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(subscription.Id, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    [TestCase(SubscriptionStatuses.ApprovalPending)]
    [TestCase(SubscriptionStatuses.Approved)]
    public async Task ReconcileSubscriptionAsync_DoesNotEscalateMismatch_ForUnapprovedProviderStatus(
        string providerStatus)
    {
        var (user, subscription) = ArrangePendingCheckout(providerStatus);

        await _service.ReconcileSubscriptionAsync(
            subscription.PayPalSubscriptionId!,
            "https://streamtunes.example");

        // PayPal cannot charge an agreement the buyer never approved, so there is nothing to warn
        // anyone about. Any episode opened by the previous over-broad gate must also be closed.
        _anomalyService.Verify(
            service => service.RecordMismatchAsync(
                It.IsAny<Subscription>(),
                It.IsAny<ApplicationUser>(),
                It.IsAny<PayPalSubscriptionDetails>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(subscription.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RefreshIfNeededAsync_DoesNotEscalateMismatch_ForFreshPendingCheckout()
    {
        // The production entry point behind api/subscription/status, mobile login, and the
        // home-page offer island: a pending row always has a null active subscription, so it
        // reconciles on every single poll.
        var (user, subscription) = ArrangePendingCheckout(SubscriptionStatuses.ApprovalPending);
        _subscriptionService.Setup(service => service.GetLatestSubscriptionAsync(user.Id))
            .ReturnsAsync(subscription);

        await _service.RefreshIfNeededAsync(user.Id, "https://streamtunes.example");

        _anomalyService.Verify(
            service => service.RecordMismatchAsync(
                It.IsAny<Subscription>(),
                It.IsAny<ApplicationUser>(),
                It.IsAny<PayPalSubscriptionDetails>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ReconcileSubscriptionAsync_DoesNotEscalateMismatch_WhenPendingReconciliationFails()
    {
        var (_, subscription) = ArrangePendingCheckout(SubscriptionStatuses.ApprovalPending);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                subscription.PayPalSubscriptionId!,
                It.IsAny<PayPalSubscriptionReconciliation>()))
            .ReturnsAsync((PayPalSubscriptionReconciliationResult?)null);

        await _service.ReconcileSubscriptionAsync(
            subscription.PayPalSubscriptionId!,
            "https://streamtunes.example");

        _anomalyService.Verify(
            service => service.RecordMismatchAsync(
                It.IsAny<Subscription>(),
                It.IsAny<ApplicationUser>(),
                It.IsAny<PayPalSubscriptionDetails>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(subscription.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestCase(SubscriptionStatuses.Active)]
    [TestCase(SubscriptionStatuses.Suspended)]
    public async Task ReconcileSubscriptionAsync_EscalatesMismatch_WhenProviderIsBillableWithoutEntitlement(
        string providerStatus)
    {
        // Regression guard: narrowing the gate to ACTIVE/SUSPENDED must not disable the feature.
        var (user, subscription) = ArrangePendingCheckout(providerStatus);
        _anomalyService.Setup(service => service.RecordMismatchAsync(
                It.IsAny<Subscription>(),
                user,
                It.IsAny<PayPalSubscriptionDetails>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionAnomaly
            {
                Id = 77,
                SubscriptionId = subscription.Id,
                CorrelationId = "bd6a5c0e-1a2b-4c3d-9e8f-0a1b2c3d4e5f"
            });

        await _service.ReconcileSubscriptionAsync(
            subscription.PayPalSubscriptionId!,
            "https://streamtunes.example");

        _anomalyService.Verify(
            service => service.RecordMismatchAsync(
                It.IsAny<Subscription>(),
                user,
                It.IsAny<PayPalSubscriptionDetails>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ReconcileSubscriptionAsync_StillForceCancelsPendingAgreement_WhenOtherProviderGrantsAccess()
    {
        // Pins IsPotentiallyBillableStatus's other caller. It must keep treating APPROVAL_PENDING as
        // billable so an overlapping agreement is force-cancelled, even though the mismatch gate no
        // longer escalates that status. Do not merge the two predicates.
        var (user, subscription) = ArrangePendingCheckout(SubscriptionStatuses.ApprovalPending);
        _subscriptionService.Setup(service => service.GetCurrentSubscriptionFromOtherProviderAsync(
                user.Id,
                BillingSources.PayPal))
            .ReturnsAsync(new Subscription
            {
                Id = 91,
                UserId = user.Id,
                BillingSource = BillingSources.GooglePlay,
                Status = SubscriptionStatuses.Active
            });
        _payPalApi.Setup(service => service.CancelSubscriptionAsync(
                subscription.PayPalSubscriptionId!,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                subscription.PayPalSubscriptionId!,
                It.IsAny<DateTime?>()))
            .ReturnsAsync(true);

        await _service.ReconcileSubscriptionAsync(
            subscription.PayPalSubscriptionId!,
            "https://streamtunes.example");

        _payPalApi.Verify(
            service => service.CancelSubscriptionAsync(
                subscription.PayPalSubscriptionId!,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AbandonPendingCheckoutAsync_RefusesToTouchANewerCheckout_WhenAnExpectedIdIsGiven()
    {
        // The background sweep decides a row is stale, then calls this. If the user started a fresh
        // checkout in between, tearing down "the newest pending row" would kill the new one.
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync(new Subscription
            {
                Id = 99,
                UserId = user.Id,
                BillingSource = BillingSources.PayPal,
                PayPalSubscriptionId = "I-BRAND-NEW",
                Status = SubscriptionStatuses.ApprovalPending
            });

        var result = await _service.AbandonPendingCheckoutAsync(user, expectedPendingSubscriptionId: 24);

        Assert.That(result, Is.False);
        _subscriptionService.Verify(
            service => service.CancelPayPalSubscriptionAsync(It.IsAny<string>(), It.IsAny<DateTime?>()),
            Times.Never);
        _payPalApi.Verify(
            service => service.CancelSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _subscriptionService.Verify(
            service => service.DeletePendingSubscriptionAsync(It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task AbandonPendingCheckoutAsync_ReportsFailure_WhenTheTargetedRowIsNoLongerPending()
    {
        // Reporting success for a row we did not touch would let the nightly sweep count it as
        // cleaned up - and resolve its anomaly episode - on every run, forever.
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription)null!);

        var result = await _service.AbandonPendingCheckoutAsync(user, expectedPendingSubscriptionId: 24);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AbandonPendingCheckoutAsync_TreatsNoPendingCheckoutAsSuccess_ForInteractiveCallers()
    {
        // Interactive callers (the PayPal cancel-return page, api/subscription/delete-pending) pass
        // no id and just want "there is nothing left to abandon" to be a success.
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        _subscriptionService.Setup(service => service.GetPendingSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription)null!);

        var result = await _service.AbandonPendingCheckoutAsync(user);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task ReconcileSubscriptionAsync_ResolvesOpenEpisode_WhenPayPalNoLongerKnowsTheAgreement()
    {
        // A provider-confirmed 404 means the agreement cannot bill anyone, so there is no mismatch
        // left to escalate. This path never reaches SynchronizeAnomalyEpisodeAsync, so without an
        // explicit resolve the episode would be re-observed hourly forever and never notified.
        var subscription = new Subscription
        {
            Id = 24,
            UserId = 42,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-GONE",
            Status = SubscriptionStatuses.Active
        };
        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync("I-GONE"))
            .ReturnsAsync(subscription);
        _payPalApi.Setup(service => service.GetSubscriptionAsync("I-GONE", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PayPalSubscriptionDetails)null!);

        var result = await _service.ReconcileSubscriptionAsync("I-GONE", "https://streamtunes.example");

        Assert.That(result, Is.Null);
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(subscription.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Arranges a local APPROVAL_PENDING row that PayPal reports with <paramref name="providerStatus"/>,
    /// with no current entitlement — the shape that used to trigger the mismatch emails.
    /// </summary>
    private (ApplicationUser User, Subscription Subscription) ArrangePendingCheckout(string providerStatus)
    {
        var user = new ApplicationUser { Id = 42, Email = "listener@example.com" };
        var subscription = new Subscription
        {
            Id = 24,
            UserId = user.Id,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PENDING",
            PayPalPlanId = "P-NO-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };

        _subscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync(subscription.PayPalSubscriptionId))
            .ReturnsAsync(subscription);
        _subscriptionService.Setup(service => service.ReconcilePayPalSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<PayPalSubscriptionReconciliation>()))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult { Subscription = subscription });
        _subscriptionService.Setup(service => service.GetActiveSubscriptionAsync(user.Id))
            .ReturnsAsync((Subscription?)null);
        _payPalApi.Setup(service => service.GetSubscriptionAsync(
                subscription.PayPalSubscriptionId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionDetails
            {
                Id = subscription.PayPalSubscriptionId,
                PlanId = subscription.PayPalPlanId,
                Status = providerStatus,
                FailedPaymentsCount = providerStatus == SubscriptionStatuses.Suspended ? 1 : 0,
                Plan = CreateNoTrialPlan()
            });
        _userManager.Setup(manager => manager.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);

        return (user, subscription);
    }

    private static PayPalWebSubscriptionOffer CreateOffer(int version = 8)
    {
        return new PayPalWebSubscriptionOffer
        {
            Version = version,
            UpdatedAtUtc = DateTime.UtcNow,
            PrimaryPlan = new PayPalWebPlanSnapshot
            {
                Id = "P-TRIAL",
                Name = "Three days free, then $0.99 per month",
                Status = PayPalPlanStatuses.Active,
                RegularPrice = 0.99m,
                CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode,
                IntervalUnit = PayPalBillingIntervals.Month,
                IntervalCount = 1,
                TrialDays = 3
            },
            ResubscriberPlan = new PayPalWebPlanSnapshot
            {
                Id = "P-NO-TRIAL",
                Name = "$0.99 per month",
                Status = PayPalPlanStatuses.Active,
                RegularPrice = 0.99m,
                CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode,
                IntervalUnit = PayPalBillingIntervals.Month,
                IntervalCount = 1
            }
        };
    }

    private static PayPalPlan CreateTrialPlan()
    {
        return new PayPalPlan
        {
            Id = "P-TRIAL",
            Name = "Three days free, then $0.99 per month",
            Status = PayPalPlanStatuses.Active,
            BillingCycles =
            [
                new PayPalBillingCycle
                {
                    TenureType = PayPalBillingTenureTypes.Trial,
                    Sequence = 1,
                    TotalCycles = 1,
                    IntervalUnit = PayPalBillingIntervals.Day,
                    IntervalCount = 3,
                    FixedPrice = 0m,
                    CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode
                },
                new PayPalBillingCycle
                {
                    TenureType = PayPalBillingTenureTypes.Regular,
                    Sequence = 2,
                    TotalCycles = 0,
                    IntervalUnit = PayPalBillingIntervals.Month,
                    IntervalCount = 1,
                    FixedPrice = 0.99m,
                    CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode
                }
            ]
        };
    }

    private static PayPalPlan CreateNoTrialPlan()
    {
        return new PayPalPlan
        {
            Id = "P-NO-TRIAL",
            Name = "$0.99 per month",
            Status = PayPalPlanStatuses.Active,
            BillingCycles =
            [
                new PayPalBillingCycle
                {
                    TenureType = PayPalBillingTenureTypes.Regular,
                    Sequence = 1,
                    TotalCycles = 0,
                    IntervalUnit = PayPalBillingIntervals.Month,
                    IntervalCount = 1,
                    FixedPrice = 0.99m,
                    CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode
                }
            ]
        };
    }
}
