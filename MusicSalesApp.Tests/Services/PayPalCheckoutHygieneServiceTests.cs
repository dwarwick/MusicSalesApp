using Microsoft.AspNetCore.Identity;
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
public class PayPalCheckoutHygieneServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private DbContextOptions<AppDbContext> _contextOptions = null!;
    private AppDbContext _context = null!;
    private Mock<IPayPalSubscriptionManagementService> _managementService = null!;
    private Mock<IPayPalSubscriptionAnomalyService> _anomalyService = null!;
    private Mock<ISubscriptionService> _subscriptionService = null!;
    private Mock<UserManager<ApplicationUser>> _userManager = null!;
    private Mock<IEmailService> _emailService = null!;
    private PayPalCheckoutHygieneService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PayPalCheckoutHygieneTestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(_contextOptions);

        var contextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        contextFactory.Setup(factory => factory.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _managementService = new Mock<IPayPalSubscriptionManagementService>();
        _anomalyService = new Mock<IPayPalSubscriptionAnomalyService>();
        _subscriptionService = new Mock<ISubscriptionService>();
        _emailService = new Mock<IEmailService>();
        _emailService.Setup(service => service.GetAppBaseUrl()).Returns("https://streamtunes.example");

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _userManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _service = new PayPalCheckoutHygieneService(
            contextFactory.Object,
            _managementService.Object,
            _anomalyService.Object,
            _subscriptionService.Object,
            _userManager.Object,
            _emailService.Object,
            new TestTimeProvider(Now),
            Mock.Of<ILogger<PayPalCheckoutHygieneService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // --- Stale pending checkout sweep ---

    [Test]
    public async Task CleanupStalePendingCheckoutsAsync_ClosesStaleCheckoutAndResolvesEpisode()
    {
        var subscription = await AddPendingCheckoutAsync(id: 24, ageHours: 96);
        ArrangeUser(subscription.UserId);
        ArrangeAbandonResult(true);

        var closed = await _service.CleanupStalePendingCheckoutsAsync();

        Assert.That(closed, Is.EqualTo(1));
        // The candidate id must be passed through so the abandon re-checks it against the same read
        // it acts on - checking only in this service would leave a race window.
        _managementService.Verify(
            service => service.AbandonPendingCheckoutAsync(
                It.Is<ApplicationUser>(user => user.Id == subscription.UserId),
                subscription.Id,
                It.IsAny<CancellationToken>()),
            Times.Once);
        // The cancel path leaves the row alive as CANCELLED, so the episode must be closed explicitly.
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(subscription.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task CleanupStalePendingCheckoutsAsync_IgnoresRecentCheckout()
    {
        var subscription = await AddPendingCheckoutAsync(id: 25, ageHours: 2);
        ArrangeUser(subscription.UserId);

        var closed = await _service.CleanupStalePendingCheckoutsAsync();

        Assert.That(closed, Is.Zero);
        VerifyNoProviderCalls();
    }

    [Test]
    public async Task CleanupStalePendingCheckoutsAsync_IgnoresNonPayPalAndSettledRows()
    {
        await AddSubscriptionAsync(
            id: 26, ageHours: 96, BillingSources.GooglePlay, SubscriptionStatuses.ApprovalPending, "I-GP");
        await AddSubscriptionAsync(
            id: 27, ageHours: 96, BillingSources.PayPal, SubscriptionStatuses.Active, "I-ACTIVE");
        await AddSubscriptionAsync(
            id: 28, ageHours: 96, BillingSources.PayPal, SubscriptionStatuses.Suspended, "I-SUSPENDED");

        var closed = await _service.CleanupStalePendingCheckoutsAsync();

        Assert.That(closed, Is.Zero);
        VerifyNoProviderCalls();
    }

    [Test]
    public async Task CleanupStalePendingCheckoutsAsync_ProcessesOnlyTheNewestPendingRowPerUser()
    {
        // Every downstream call resolves the user's pending checkout by user id, so older rows are
        // not actionable and must not consume batch slots.
        var older = await AddPendingCheckoutAsync(id: 40, ageHours: 200, payPalId: "I-OLD");
        var newer = await AddPendingCheckoutAsync(id: 41, ageHours: 96, payPalId: "I-NEW");
        ArrangeUser(newer.UserId);
        ArrangeAbandonResult(true);

        var closed = await _service.CleanupStalePendingCheckoutsAsync();

        Assert.That(closed, Is.EqualTo(1));
        _managementService.Verify(
            service => service.AbandonPendingCheckoutAsync(
                It.IsAny<ApplicationUser>(),
                newer.Id,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _managementService.Verify(
            service => service.AbandonPendingCheckoutAsync(
                It.IsAny<ApplicationUser>(),
                older.Id,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task CleanupStalePendingCheckoutsAsync_LeavesRowUntouched_WhenPayPalVerificationFails()
    {
        var subscription = await AddPendingCheckoutAsync(id: 30, ageHours: 96);
        ArrangeUser(subscription.UserId);
        ArrangeAbandonResult(false);

        var closed = await _service.CleanupStalePendingCheckoutsAsync();

        Assert.That(closed, Is.Zero);
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task CleanupStalePendingCheckoutsAsync_SkipsCandidate_WhenUserNoLongerExists()
    {
        await AddPendingCheckoutAsync(id: 34, ageHours: 96, userId: 500);
        _userManager.Setup(manager => manager.FindByIdAsync("500")).ReturnsAsync((ApplicationUser)null!);

        var closed = await _service.CleanupStalePendingCheckoutsAsync();

        Assert.That(closed, Is.Zero);
        VerifyNoProviderCalls();
    }

    [Test]
    public async Task CleanupStalePendingCheckoutsAsync_ContinuesAfterOneCandidateFails()
    {
        var failing = await AddPendingCheckoutAsync(id: 31, ageHours: 120, userId: 101, payPalId: "I-FAIL");
        var succeeding = await AddPendingCheckoutAsync(id: 32, ageHours: 96, userId: 102, payPalId: "I-OK");
        ArrangeUser(failing.UserId);
        ArrangeUser(succeeding.UserId);
        _managementService.Setup(service => service.AbandonPendingCheckoutAsync(
                It.Is<ApplicationUser>(user => user.Id == failing.UserId),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PayPal unavailable"));
        _managementService.Setup(service => service.AbandonPendingCheckoutAsync(
                It.Is<ApplicationUser>(user => user.Id == succeeding.UserId),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var closed = await _service.CleanupStalePendingCheckoutsAsync();

        Assert.That(closed, Is.EqualTo(1));
        _anomalyService.Verify(
            service => service.ResolveOpenEpisodeAsync(succeeding.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- Mismatch episode re-observation ---

    [Test]
    public async Task ReobserveOpenMismatchEpisodesAsync_ReconcilesOnlyOverdueUnnotifiedEpisodes()
    {
        await AddAnomalyAsync(id: 1, payPalId: "I-OVERDUE", detectedMinutesAgo: 120);
        await AddAnomalyAsync(id: 2, payPalId: "I-FRESH", detectedMinutesAgo: 5);
        await AddAnomalyAsync(id: 3, payPalId: "I-NOTIFIED", detectedMinutesAgo: 120, alreadyNotified: true);
        await AddAnomalyAsync(id: 4, payPalId: "I-RESOLVED", detectedMinutesAgo: 120, resolved: true);

        var reobserved = await _service.ReobserveOpenMismatchEpisodesAsync();

        Assert.That(reobserved, Is.EqualTo(1));
        _managementService.Verify(
            service => service.ReconcileSubscriptionAsync(
                "I-OVERDUE",
                "https://streamtunes.example",
                It.IsAny<CancellationToken>()),
            Times.Once);
        _managementService.Verify(
            service => service.ReconcileSubscriptionAsync(
                It.IsIn("I-FRESH", "I-NOTIFIED", "I-RESOLVED"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ReobserveOpenMismatchEpisodesAsync_SkipsEpisodesWithoutAProviderId()
    {
        await AddAnomalyAsync(id: 5, payPalId: string.Empty, detectedMinutesAgo: 120);

        var reobserved = await _service.ReobserveOpenMismatchEpisodesAsync();

        Assert.That(reobserved, Is.Zero);
        _managementService.Verify(
            service => service.ReconcileSubscriptionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ReobserveOpenMismatchEpisodesAsync_ContinuesAfterOneEpisodeThrows()
    {
        await AddAnomalyAsync(id: 6, payPalId: "I-THROWS", detectedMinutesAgo: 200);
        await AddAnomalyAsync(id: 7, payPalId: "I-WORKS", detectedMinutesAgo: 120);
        _managementService.Setup(service => service.ReconcileSubscriptionAsync(
                "I-THROWS",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PayPal unavailable"));

        var reobserved = await _service.ReobserveOpenMismatchEpisodesAsync();

        Assert.That(reobserved, Is.EqualTo(1));
        _managementService.Verify(
            service => service.ReconcileSubscriptionAsync(
                "I-WORKS",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- Helpers ---

    private void VerifyNoProviderCalls() => _managementService.Verify(
        service => service.AbandonPendingCheckoutAsync(
            It.IsAny<ApplicationUser>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()),
        Times.Never);

    private void ArrangeAbandonResult(bool result) => _managementService
        .Setup(service => service.AbandonPendingCheckoutAsync(
            It.IsAny<ApplicationUser>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(result);

    private void ArrangeUser(int userId) => _userManager
        .Setup(manager => manager.FindByIdAsync(userId.ToString()))
        .ReturnsAsync(new ApplicationUser { Id = userId, Email = "listener@example.com" });

    private Task<Subscription> AddPendingCheckoutAsync(
        int id,
        int ageHours,
        int userId = 100,
        string payPalId = "I-PENDING") =>
        AddSubscriptionAsync(
            id, ageHours, BillingSources.PayPal, SubscriptionStatuses.ApprovalPending, payPalId, userId);

    private async Task<Subscription> AddSubscriptionAsync(
        int id,
        int ageHours,
        string billingSource,
        string status,
        string payPalId,
        int userId = 100)
    {
        var createdAt = Now.UtcDateTime.AddHours(-ageHours);
        var subscription = new Subscription
        {
            Id = id,
            UserId = userId,
            BillingSource = billingSource,
            Status = status,
            PayPalSubscriptionId = payPalId,
            StartDate = createdAt,
            CreatedAt = createdAt
        };
        await using var context = new AppDbContext(_contextOptions);
        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync();
        return subscription;
    }

    private async Task AddAnomalyAsync(
        int id,
        string payPalId,
        int detectedMinutesAgo,
        bool alreadyNotified = false,
        bool resolved = false)
    {
        var detectedAt = Now.UtcDateTime.AddMinutes(-detectedMinutesAgo);
        await using var context = new AppDbContext(_contextOptions);
        context.PayPalSubscriptionAnomalies.Add(new PayPalSubscriptionAnomaly
        {
            Id = id,
            SubscriptionId = id,
            UserId = 100,
            PayPalSubscriptionId = payPalId,
            ProviderStatus = SubscriptionStatuses.Active,
            LocalStatus = SubscriptionStatuses.Active,
            CorrelationId = Guid.NewGuid().ToString("D"),
            DetectedAtUtc = detectedAt,
            LastObservedAtUtc = detectedAt,
            ResolvedAtUtc = resolved ? Now.UtcDateTime : null,
            UserEmailSentAtUtc = alreadyNotified ? detectedAt : null,
            AdminEmailSentAtUtc = alreadyNotified ? detectedAt : null
        });
        await context.SaveChangesAsync();
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
