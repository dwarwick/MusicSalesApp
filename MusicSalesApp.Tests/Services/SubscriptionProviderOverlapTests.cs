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
/// Overlap detection decides whether a PayPal agreement is redundant and should be cancelled at
/// the provider, so a false positive cancels a paying subscriber. A row whose BillingSource is
/// unknown must never be the thing that triggers it.
/// </summary>
[TestFixture]
public class SubscriptionProviderOverlapTests
{
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private SubscriptionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ProviderOverlapTestDb_{Guid.NewGuid()}")
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

    [Test]
    public async Task GetCurrentSubscriptionFromOtherProviderAsync_IgnoresARowWithNoBillingSource()
    {
        // "BillingSource != 'PayPal'" matches anything that is not literally PayPal, so a row with
        // no real provider matched as a competitor to every provider - including itself - and the
        // caller responds by cancelling that agreement at PayPal as a redundant overlap.
        //
        // The empty string rather than null, because null is no longer reachable: BillingSource is
        // [Required], so EF refuses to store one. [Required] does not reject an empty string
        // though, and an empty string differs from every provider exactly as a null did - so this
        // is the shape the guard still has to catch.
        await AddAsync(new Subscription
        {
            Id = 1,
            UserId = 6,
            BillingSource = string.Empty,
            PayPalSubscriptionId = "I-LEGACY",
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = DateTime.UtcNow.AddMonths(-5)
        });

        var other = await _service.GetCurrentSubscriptionFromOtherProviderAsync(6, BillingSources.PayPal);

        Assert.That(
            other,
            Is.Null,
            "a subscription must never be found to overlap with itself");
    }

    [Test]
    public async Task GetCurrentSubscriptionFromOtherProviderAsync_StillFindsAGenuineOtherProvider()
    {
        // The guard must not blind real overlap detection: a live store subscription alongside a
        // PayPal agreement is exactly what the caller needs to know about.
        await AddAsync(new Subscription
        {
            Id = 2,
            UserId = 7,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-PAYPAL",
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = DateTime.UtcNow.AddDays(-3)
        });
        await AddAsync(new Subscription
        {
            Id = 3,
            UserId = 7,
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = "token",
            Status = SubscriptionStatuses.Active,
            LastPaymentDate = DateTime.UtcNow.AddDays(-1)
        });

        var other = await _service.GetCurrentSubscriptionFromOtherProviderAsync(7, BillingSources.PayPal);

        Assert.Multiple(() =>
        {
            Assert.That(other, Is.Not.Null);
            Assert.That(other!.BillingSource, Is.EqualTo(BillingSources.GooglePlay));
        });
    }

    [Test]
    public async Task GetCurrentSubscriptionFromOtherProviderAsync_IgnoresAnExpiredOtherProvider()
    {
        await AddAsync(new Subscription
        {
            Id = 4,
            UserId = 8,
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = "token",
            Status = SubscriptionStatuses.Expired,
            EndDate = DateTime.UtcNow.AddDays(-2)
        });

        var other = await _service.GetCurrentSubscriptionFromOtherProviderAsync(8, BillingSources.PayPal);

        Assert.That(other, Is.Null);
    }

    private async Task AddAsync(Subscription subscription)
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();
    }
}
