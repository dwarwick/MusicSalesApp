using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

#nullable enable

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PayPalEntitlementDriftServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private DbContextOptions<AppDbContext> _contextOptions = null!;
    private AppDbContext _context = null!;
    private Mock<IPayPalSubscriptionManagementService> _managementService = null!;
    private Mock<ISubscriptionService> _subscriptionService = null!;
    private Mock<IEmailService> _emailService = null!;
    private PayPalEntitlementDriftService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PayPalEntitlementDriftTestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(_contextOptions);

        var contextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        contextFactory.Setup(factory => factory.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _managementService = new Mock<IPayPalSubscriptionManagementService>();
        _subscriptionService = new Mock<ISubscriptionService>();
        _subscriptionService.Setup(service => service.CancelPayPalSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime?>()))
            .ReturnsAsync(true);
        _emailService = new Mock<IEmailService>();
        _emailService.Setup(service => service.GetAppBaseUrl()).Returns("https://streamtunes.example");

        _service = new PayPalEntitlementDriftService(
            contextFactory.Object,
            _managementService.Object,
            _subscriptionService.Object,
            _emailService.Object,
            new TestTimeProvider(Now),
            Mock.Of<ILogger<PayPalEntitlementDriftService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // --- What the sweep exists for ---

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_SettlesActiveRowThatTheProviderHasCancelled()
    {
        // The production case: a BILLING.SUBSCRIPTION.CANCELLED webhook never arrived, so a
        // cancelled agreement kept granting access with no EndDate to ever expire.
        var subscription = await AddSubscriptionAsync(
            id: 8,
            status: SubscriptionStatuses.Active,
            lastPaymentDate: Now.UtcDateTime.AddMonths(-5));
        ArrangeProviderStatus(subscription.PayPalSubscriptionId!, SubscriptionStatuses.Cancelled);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.EqualTo(1));
        _managementService.Verify(service => service.TryReconcileSubscriptionAsync(
                subscription.PayPalSubscriptionId!,
                "https://streamtunes.example",
                false,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the backlog must settle without emailing the subscriber");
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_ReportsNothingWhenTheProviderAgrees()
    {
        var subscription = await AddSubscriptionAsync(id: 4, status: SubscriptionStatuses.Active);
        ArrangeProviderStatus(subscription.PayPalSubscriptionId!, SubscriptionStatuses.Active);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_VerifiesSuspendedRows()
    {
        var subscription = await AddSubscriptionAsync(id: 11, status: SubscriptionStatuses.Suspended);
        ArrangeProviderStatus(subscription.PayPalSubscriptionId!, SubscriptionStatuses.Expired);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.EqualTo(1));
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_VerifiesCancelledRowsStillInsideTheirPaidPeriod()
    {
        var subscription = await AddSubscriptionAsync(
            id: 21,
            status: SubscriptionStatuses.Cancelled,
            endDate: Now.UtcDateTime.AddDays(10));
        ArrangeProviderStatus(subscription.PayPalSubscriptionId!, SubscriptionStatuses.Active);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.EqualTo(1));
    }

    // --- What it must leave alone ---

    [TestCase(SubscriptionStatuses.Expired, null)]
    [TestCase(SubscriptionStatuses.ApprovalPending, null)]
    public async Task ReconcileDriftedSubscriptionsAsync_IgnoresRowsThatGrantNothing(
        string status,
        DateTime? endDate)
    {
        await AddSubscriptionAsync(id: 30, status: status, endDate: endDate);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
        VerifyNothingWasVerified();
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_IgnoresCancelledRowsWhosePeriodHasEnded()
    {
        // NormalizeExpiredSubscriptionsAsync already settles these without a provider round trip.
        await AddSubscriptionAsync(
            id: 32,
            status: SubscriptionStatuses.Cancelled,
            endDate: Now.UtcDateTime.AddDays(-1));

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
        VerifyNothingWasVerified();
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_IgnoresStoreBilledSubscriptions()
    {
        await AddSubscriptionAsync(
            id: 45,
            status: SubscriptionStatuses.Active,
            billingSource: BillingSources.GooglePlay,
            payPalSubscriptionId: null);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
        VerifyNothingWasVerified();
    }

    // --- Telling the two provider failures apart ---

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_LeavesTheRowAloneWhenPayPalCannotBeReached()
    {
        // Unverifiable means the provider state is unknown. Revoking a paying subscriber's access
        // on an outage would be far worse than waiting for the next run.
        var subscription = await AddSubscriptionAsync(id: 9, status: SubscriptionStatuses.Active);
        ArrangeAttempt(subscription.PayPalSubscriptionId!, PayPalReconcileAttempt.Unverifiable);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(settled, Is.Zero);
            Assert.That(ReadStatus(9), Is.EqualTo(SubscriptionStatuses.Active));
        });
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime?>()),
            Times.Never);
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_SettlesRowsTheProviderConfirmsAreMissing()
    {
        // A 404 is definitive: the agreement cannot bill anyone, so the row's claim to entitlement
        // is refuted. Cancelling revokes access; the alternative of leaving it alone would re-alert
        // every single night forever.
        var missing = await AddSubscriptionAsync(id: 12, status: SubscriptionStatuses.Active);
        var healthy = await AddSubscriptionAsync(id: 13, status: SubscriptionStatuses.Active);
        ArrangeAttempt(missing.PayPalSubscriptionId!, PayPalReconcileAttempt.ProviderConfirmedMissing);
        ArrangeProviderStatus(healthy.PayPalSubscriptionId!, SubscriptionStatuses.Active);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.EqualTo(1));
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                missing.PayPalSubscriptionId!,
                null),
            Times.Once,
            "cancel, never delete: deleting would erase the billing record and hand out a second free trial");
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_SettlesNothingWhenEveryAgreementIsReportedMissing()
    {
        // A whole batch of 404s is a broken connection to PayPal - credentials pointed at the wrong
        // environment, or an app moved between accounts - not a batch of dead agreements. Acting on
        // it would cancel every subscriber in turn.
        var first = await AddSubscriptionAsync(id: 14, status: SubscriptionStatuses.Active);
        var second = await AddSubscriptionAsync(id: 15, status: SubscriptionStatuses.Active);
        ArrangeAttempt(first.PayPalSubscriptionId!, PayPalReconcileAttempt.ProviderConfirmedMissing);
        ArrangeAttempt(second.PayPalSubscriptionId!, PayPalReconcileAttempt.ProviderConfirmedMissing);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
        VerifyNothingWasCancelled();
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_SettlesNothingWhenTheOnlyRowCheckedIsReportedMissing()
    {
        // A lone 404 with nothing to compare it against is not evidence the agreement is dead - it
        // is equally consistent with credentials that cannot see any agreement at all. Requiring
        // one recognised agreement is what makes the difference observable; a rule phrased as
        // "were they all missing?" would exempt a batch of one entirely.
        var only = await AddSubscriptionAsync(id: 16, status: SubscriptionStatuses.Active);
        ArrangeAttempt(only.PayPalSubscriptionId!, PayPalReconcileAttempt.ProviderConfirmedMissing);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
        VerifyNothingWasCancelled();
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_SettlesNothingWhenMissingIsMixedWithUnverifiable()
    {
        // One unrelated network blip must not license every 404 in the batch. At the real batch
        // size that is 199 subscribers cancelled off the back of a single dropped connection.
        var missing = await AddSubscriptionAsync(id: 17, status: SubscriptionStatuses.Active);
        var blip = await AddSubscriptionAsync(id: 18, status: SubscriptionStatuses.Active);
        ArrangeAttempt(missing.PayPalSubscriptionId!, PayPalReconcileAttempt.ProviderConfirmedMissing);
        ArrangeAttempt(blip.PayPalSubscriptionId!, PayPalReconcileAttempt.Unverifiable);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
        VerifyNothingWasCancelled();
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_PreservesAPaidThroughDateWhenSettlingAMissingAgreement()
    {
        // Someone who cancelled but paid through keeps the days they bought. Passing null here
        // would confiscate them, and this row is a candidate precisely because that end date is
        // still in the future.
        var paidThrough = Now.UtcDateTime.AddDays(20);
        var missing = await AddSubscriptionAsync(
            id: 19,
            status: SubscriptionStatuses.Cancelled,
            endDate: paidThrough);
        var healthy = await AddSubscriptionAsync(id: 20, status: SubscriptionStatuses.Active);
        ArrangeAttempt(missing.PayPalSubscriptionId!, PayPalReconcileAttempt.ProviderConfirmedMissing);
        ArrangeProviderStatus(healthy.PayPalSubscriptionId!, SubscriptionStatuses.Active);

        await _service.ReconcileDriftedSubscriptionsAsync();

        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                missing.PayPalSubscriptionId!,
                paidThrough),
            Times.Once);
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_TreatsAnUnarrangedProviderAnswerAsUnverifiable()
    {
        // PayPalReconcileAttempt is a struct crossing an interface, so anything that does not
        // answer - a stub, a mock with a missed Setup - hands back default(T). That default must be
        // the outcome which touches nothing, not a "Reconciled" carrying a null Result.
        await AddSubscriptionAsync(id: 22, status: SubscriptionStatuses.Active);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.Zero);
        VerifyNothingWasCancelled();
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_KeepsGoingAfterOneSubscriptionThrows()
    {
        var failing = await AddSubscriptionAsync(id: 50, status: SubscriptionStatuses.Active);
        var healthy = await AddSubscriptionAsync(id: 51, status: SubscriptionStatuses.Active);
        _managementService.Setup(service => service.TryReconcileSubscriptionAsync(
                failing.PayPalSubscriptionId!,
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayPalSubscriptionApiException("provider outage"));
        ArrangeProviderStatus(healthy.PayPalSubscriptionId!, SubscriptionStatuses.Cancelled);

        var settled = await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(settled, Is.EqualTo(1), "the second subscription must still be verified");
    }

    // --- Batch rotation: the reason the sweep does not starve at scale ---

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_RecordsWhenEachRowWasChecked()
    {
        var subscription = await AddSubscriptionAsync(id: 60, status: SubscriptionStatuses.Active);
        ArrangeProviderStatus(subscription.PayPalSubscriptionId!, SubscriptionStatuses.Active);

        await _service.ReconcileDriftedSubscriptionsAsync();

        using var context = new AppDbContext(_contextOptions);
        Assert.That(
            context.Subscriptions.Single(row => row.Id == 60).LastProviderCheckAtUtc,
            Is.EqualTo(Now.UtcDateTime),
            "an unstamped row would monopolise the batch and starve everything behind it");
    }

    [Test]
    public async Task ReconcileDriftedSubscriptionsAsync_ChecksNeverCheckedRowsBeforeReCheckingOldOnes()
    {
        // Ordering by Id instead would re-read the same lowest ids every night and never reach
        // anything past the batch cap.
        var checkedRecently = await AddSubscriptionAsync(
            id: 70,
            status: SubscriptionStatuses.Active,
            lastProviderCheckAtUtc: Now.UtcDateTime.AddDays(-1));
        var checkedLongAgo = await AddSubscriptionAsync(
            id: 71,
            status: SubscriptionStatuses.Active,
            lastProviderCheckAtUtc: Now.UtcDateTime.AddDays(-30));
        var neverChecked = await AddSubscriptionAsync(id: 72, status: SubscriptionStatuses.Active);

        var order = new List<string>();
        _managementService.Setup(service => service.TryReconcileSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback((string id, string _, bool __, CancellationToken ___) => order.Add(id))
            .ReturnsAsync(PayPalReconcileAttempt.Unverifiable);

        await _service.ReconcileDriftedSubscriptionsAsync();

        Assert.That(order, Is.EqualTo(new[]
        {
            neverChecked.PayPalSubscriptionId!,
            checkedLongAgo.PayPalSubscriptionId!,
            checkedRecently.PayPalSubscriptionId!
        }));
    }

    // --- helpers ---

    private string ReadStatus(int id)
    {
        using var context = new AppDbContext(_contextOptions);
        return context.Subscriptions.Single(row => row.Id == id).Status;
    }

    private void VerifyNothingWasCancelled() =>
        _subscriptionService.Verify(service => service.CancelPayPalSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<DateTime?>()),
            Times.Never);

    private void VerifyNothingWasVerified() =>
        _managementService.Verify(service => service.TryReconcileSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

    private void ArrangeAttempt(string payPalSubscriptionId, PayPalReconcileAttempt attempt) =>
        _managementService.Setup(service => service.TryReconcileSubscriptionAsync(
                payPalSubscriptionId,
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(attempt);

    private void ArrangeProviderStatus(string payPalSubscriptionId, string providerStatus) =>
        ArrangeAttempt(
            payPalSubscriptionId,
            PayPalReconcileAttempt.Reconciled(new PayPalSubscriptionReconciliationResult
            {
                Subscription = new Subscription
                {
                    PayPalSubscriptionId = payPalSubscriptionId,
                    Status = providerStatus
                },
                PreviousStatus = SubscriptionStatuses.Active
            }));

    private async Task<Subscription> AddSubscriptionAsync(
        int id,
        string status,
        string billingSource = BillingSources.PayPal,
        string? payPalSubscriptionId = "I-DRIFTED",
        DateTime? endDate = null,
        DateTime? lastPaymentDate = null,
        DateTime? lastProviderCheckAtUtc = null)
    {
        var subscription = new Subscription
        {
            Id = id,
            UserId = id * 10,
            BillingSource = billingSource,
            PayPalSubscriptionId = payPalSubscriptionId == "I-DRIFTED"
                ? $"I-DRIFTED{id}"
                : payPalSubscriptionId,
            Status = status,
            StartDate = Now.UtcDateTime.AddMonths(-6),
            CreatedAt = Now.UtcDateTime.AddMonths(-6),
            EndDate = endDate,
            LastPaymentDate = lastPaymentDate,
            LastProviderCheckAtUtc = lastProviderCheckAtUtc
        };

        _context.Subscriptions.Add(subscription);
        await _context.SaveChangesAsync();
        return subscription;
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
