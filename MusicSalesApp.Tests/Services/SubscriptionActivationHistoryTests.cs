#nullable enable

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Trial eligibility must depend on whether a subscription was ever activated, not on what its
/// status happens to be now. Reading the current status meant any cancellation - a webhook, the
/// entitlement drift sweep, an admin action - silently handed the user another free trial.
/// </summary>
[TestFixture]
public class SubscriptionActivationHistoryTests
{
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private SubscriptionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ActivationHistoryTestDb_{Guid.NewGuid()}")
            .Options;

        var factory = new Mock<IDbContextFactory<AppDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_dbOptions));

        _service = new SubscriptionService(factory.Object, Mock.Of<ILogger<SubscriptionService>>());
    }

    [TearDown]
    public void TearDown()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Database.EnsureDeleted();
    }

    // --- The bug this exists to prevent ---

    [Test]
    public async Task HasPriorActivatedSubscriptionAsync_StaysTrue_AfterAnActivatedSubscriptionIsCancelled()
    {
        // Previously the answer flipped to false the moment the status left ACTIVE, so cancelling
        // a subscriber - by any route - made them eligible for a second free trial.
        var subscription = await AddAsync(new Subscription
        {
            Id = 1,
            UserId = 7,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-ACTIVATED",
            Status = SubscriptionStatuses.Active
        });

        Assert.That(await _service.HasPriorActivatedSubscriptionAsync(7), Is.True);

        await SetStatusAsync(subscription.Id, SubscriptionStatuses.Cancelled);

        Assert.That(
            await _service.HasPriorActivatedSubscriptionAsync(7),
            Is.True,
            "cancelling a subscription must not restore free-trial eligibility");
    }

    [Test]
    public async Task HasPriorActivatedSubscriptionAsync_StaysFalse_ForACancelledCheckoutThatNeverActivated()
    {
        // The other direction, and the reason this is a new column rather than a CancelledAt check:
        // an abandoned checkout is cancelled too. Treating that as proof of a prior subscription
        // would deny a first trial to someone who merely bailed out on PayPal once.
        await AddAsync(new Subscription
        {
            Id = 2,
            UserId = 8,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-ABANDONED",
            Status = SubscriptionStatuses.Cancelled,
            CancelledAt = DateTime.UtcNow
        });

        Assert.That(await _service.HasPriorActivatedSubscriptionAsync(8), Is.False);
    }

    [Test]
    public async Task HasPriorActivatedSubscriptionAsync_IsFalse_ForAUserWithNoSubscriptions()
    {
        Assert.That(await _service.HasPriorActivatedSubscriptionAsync(9), Is.False);
    }

    [TestCase(SubscriptionStatuses.Suspended)]
    [TestCase(SubscriptionStatuses.Expired)]
    public async Task HasPriorActivatedSubscriptionAsync_IsTrue_ForRowsCarryingDatedEvidence(string status)
    {
        // A payment or a trial is proof on its own, independent of the new column.
        await AddAsync(new Subscription
        {
            Id = 3,
            UserId = 10,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PAID",
            Status = status,
            LastPaymentDate = DateTime.UtcNow.AddMonths(-2)
        });

        Assert.That(await _service.HasPriorActivatedSubscriptionAsync(10), Is.True);
    }

    // --- The stamp itself ---

    [Test]
    public async Task SaveChanges_StampsActivation_WhenASubscriptionFirstBecomesActive()
    {
        var subscription = await AddAsync(new Subscription
        {
            Id = 4,
            UserId = 11,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PENDING",
            Status = SubscriptionStatuses.ApprovalPending
        });

        Assert.That(await ReadActivatedAtAsync(subscription.Id), Is.Null);

        await SetStatusAsync(subscription.Id, SubscriptionStatuses.Active);

        Assert.That(await ReadActivatedAtAsync(subscription.Id), Is.Not.Null);
    }

    [Test]
    public async Task SaveChanges_StampsActivation_ForARowInsertedAlreadyActive()
    {
        // The store billing paths insert an already-active row rather than transitioning one.
        await AddAsync(new Subscription
        {
            Id = 5,
            UserId = 12,
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = "token",
            Status = SubscriptionStatuses.Active
        });

        Assert.That(await ReadActivatedAtAsync(5), Is.Not.Null);
    }

    [Test]
    public async Task SaveChanges_NeverOverwritesAnExistingActivation()
    {
        // Write-once: a resubscribe on the same row must keep the original activation, or the
        // column would drift forward and stop being a fact about the past.
        var original = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await AddAsync(new Subscription
        {
            Id = 6,
            UserId = 13,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-REACTIVATED",
            Status = SubscriptionStatuses.Cancelled,
            ActivatedAtUtc = original
        });

        await SetStatusAsync(6, SubscriptionStatuses.Active);

        Assert.That(await ReadActivatedAtAsync(6), Is.EqualTo(original));
    }

    [Test]
    public async Task SaveChanges_DoesNotStampANonActivatedRow()
    {
        await AddAsync(new Subscription
        {
            Id = 7,
            UserId = 14,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-STILL-PENDING",
            Status = SubscriptionStatuses.ApprovalPending
        });

        Assert.That(await ReadActivatedAtAsync(7), Is.Null);
    }

    // --- helpers ---

    private async Task<Subscription> AddAsync(Subscription subscription)
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();
        return subscription;
    }

    private async Task SetStatusAsync(int id, string status)
    {
        await using var context = new AppDbContext(_dbOptions);
        var row = await context.Subscriptions.SingleAsync(s => s.Id == id);
        row.Status = status;
        await context.SaveChangesAsync();
    }

    private async Task<DateTime?> ReadActivatedAtAsync(int id)
    {
        await using var context = new AppDbContext(_dbOptions);
        return (await context.Subscriptions.SingleAsync(s => s.Id == id)).ActivatedAtUtc;
    }
}
