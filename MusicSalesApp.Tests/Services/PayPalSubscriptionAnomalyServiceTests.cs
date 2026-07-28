using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

#nullable enable

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public sealed class PayPalSubscriptionAnomalyServiceTests
{
    private SqliteConnection _connection = null!;
    private TestDbContextFactory _contextFactory = null!;
    private Mock<IEmailService> _emailService = null!;
    private TestTimeProvider _timeProvider = null!;
    private PayPalSubscriptionAnomalyService _service = null!;
    private ApplicationUser _user = null!;
    private Subscription _subscription = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _contextFactory = new TestDbContextFactory(options);

        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
            _user = new ApplicationUser
            {
                Id = 42,
                UserName = "listener@example.com",
                NormalizedUserName = "LISTENER@EXAMPLE.COM",
                Email = "listener@example.com",
                NormalizedEmail = "LISTENER@EXAMPLE.COM"
            };
            _subscription = new Subscription
            {
                Id = 8,
                UserId = _user.Id,
                User = _user,
                BillingSource = BillingSources.PayPal,
                PayPalSubscriptionId = "I-MISMATCH",
                Status = SubscriptionStatuses.Active,
                StartDate = DateTime.UtcNow.AddDays(-3)
            };
            context.Users.Add(_user);
            context.Subscriptions.Add(_subscription);
            await context.SaveChangesAsync();
        }

        _emailService = new Mock<IEmailService>();
        _emailService
            .Setup(service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppSettingKeys.EmailAdminEmail] = "admin@example.com",
                [AppSettingKeys.EmailCustomerServiceEmail] = "customerservice@example.com",
                [AppSettingKeys.PayPalAccountManagementUrl] = "https://www.paypal.com/myaccount/autopay/"
            })
            .Build();
        _service = new PayPalSubscriptionAnomalyService(
            _contextFactory,
            _emailService.Object,
            configuration,
            _timeProvider,
            Mock.Of<ILogger<PayPalSubscriptionAnomalyService>>());
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    [Test]
    public async Task RecordMismatchAsync_ReusesOpenEpisodeAndSendsEachEmailOnce()
    {
        var details = CreateDetails(SubscriptionStatuses.Active);

        var first = await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            "Missing entitlement boundary",
            "https://streamtunes.example");
        AdvancePastGrace();
        await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            "Missing entitlement boundary",
            "https://streamtunes.example");
        // A third observation, after the emails have already gone out, is what actually proves the
        // episode is not re-notified. Two observations only prove the grace window opened once.
        AdvancePastGrace();
        var second = await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            "Missing entitlement boundary",
            "https://streamtunes.example");

        Assert.Multiple(() =>
        {
            Assert.That(second.Id, Is.EqualTo(first.Id));
            Assert.That(first.CorrelationId, Is.Not.Empty);
            Assert.That(second.UserEmailSentAtUtc, Is.Not.Null);
            Assert.That(second.AdminEmailSentAtUtc, Is.Not.Null);
        });
        _emailService.Verify(
            service => service.SendEmailAsync(
                "listener@example.com",
                It.Is<string>(subject => subject.Contains("needs attention")),
                It.Is<string>(body => body.Contains(first.CorrelationId)
                    && body.Contains("Creator tips and other one-time payments are not affected"))),
            Times.Once);
        _emailService.Verify(
            service => service.SendEmailAsync(
                "admin@example.com",
                It.Is<string>(subject => subject.Contains(first.CorrelationId)),
                It.Is<string>(body => body.Contains("Missing entitlement boundary")
                    && body.Contains("I-MISMATCH"))),
            Times.Once);
    }

    [Test]
    public async Task RecordMismatchAsync_SendsInternalAlertToAdminRatherThanCustomerService()
    {
        await RecordAfterGraceAsync(CreateDetails(SubscriptionStatuses.Active));

        _emailService.Verify(
            service => service.SendEmailAsync(
                "admin@example.com",
                It.Is<string>(subject => subject.Contains("mismatch")),
                It.IsAny<string>()),
            Times.Once);
        _emailService.Verify(
            service => service.SendEmailAsync(
                "customerservice@example.com",
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task RecordMismatchAsync_WithholdsNotifications_UntilMismatchPersists()
    {
        var details = CreateDetails(SubscriptionStatuses.Active);

        var immediate = await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            null,
            "https://streamtunes.example");

        // The episode exists right away so its correlation ID is available to the UI, but a
        // mismatch that might still self-heal must not email anyone yet.
        Assert.Multiple(() =>
        {
            Assert.That(immediate.CorrelationId, Is.Not.Empty);
            Assert.That(immediate.UserEmailSentAtUtc, Is.Null);
            Assert.That(immediate.AdminEmailSentAtUtc, Is.Null);
        });
        _emailService.Verify(
            service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

        AdvancePastGrace();
        var persisted = await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            null,
            "https://streamtunes.example");

        Assert.Multiple(() =>
        {
            Assert.That(persisted.Id, Is.EqualTo(immediate.Id));
            Assert.That(persisted.UserEmailSentAtUtc, Is.Not.Null);
            Assert.That(persisted.AdminEmailSentAtUtc, Is.Not.Null);
        });
        _emailService.Verify(
            service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(2));
    }

    [TestCase(SubscriptionStatuses.Active)]
    [TestCase(SubscriptionStatuses.Suspended)]
    public async Task RecordMismatchAsync_TellsUserSubscriptionsAreBlocked_ForBillableStatuses(string status)
    {
        await RecordAfterGraceAsync(CreateDetails(status));

        _emailService.Verify(
            service => service.SendEmailAsync(
                "listener@example.com",
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("temporarily blocked"))),
            Times.Once);
    }

    [TestCase(SubscriptionStatuses.ApprovalPending)]
    [TestCase(SubscriptionStatuses.Approved)]
    public async Task RecordMismatchAsync_DoesNotClaimSubscriptionsAreBlocked_ForUnapprovedStatuses(string status)
    {
        await RecordAfterGraceAsync(CreateDetails(status));

        _emailService.Verify(
            service => service.SendEmailAsync(
                "listener@example.com",
                It.IsAny<string>(),
                It.Is<string>(body => !body.Contains("temporarily blocked")
                    && body.Contains("does not stop you from starting a new StreamTunes subscription"))),
            Times.Once);
    }

    [Test]
    public async Task ResolveOpenEpisodeAsync_AllowsLaterMismatchToCreateNewEpisode()
    {
        var first = await RecordAfterGraceAsync(CreateDetails(SubscriptionStatuses.Suspended));

        await _service.ResolveOpenEpisodeAsync(_subscription.Id);
        _timeProvider.Advance(TimeSpan.FromDays(1));
        var second = await RecordAfterGraceAsync(CreateDetails(SubscriptionStatuses.Active));

        Assert.Multiple(() =>
        {
            Assert.That(second.Id, Is.Not.EqualTo(first.Id));
            Assert.That(second.CorrelationId, Is.Not.EqualTo(first.CorrelationId));
        });
        _emailService.Verify(
            service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(4));
    }

    [Test]
    public async Task RecordMismatchAsync_RetriesOnlyFailedDelivery()
    {
        _emailService
            .SetupSequence(service => service.SendEmailAsync(
                "listener@example.com",
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        var details = CreateDetails(SubscriptionStatuses.Active);
        await RecordAfterGraceAsync(details);

        // The episode is already past the grace window, so this second observation retries
        // immediately without advancing the clock again.
        var retried = await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            null,
            "https://streamtunes.example");

        Assert.Multiple(() =>
        {
            Assert.That(retried.UserEmailSentAtUtc, Is.Not.Null);
            Assert.That(retried.AdminEmailSentAtUtc, Is.Not.Null);
        });
        _emailService.Verify(
            service => service.SendEmailAsync("listener@example.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(2));
        _emailService.Verify(
            service => service.SendEmailAsync("admin@example.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Opens an episode, advances past the notification grace window, then re-observes it so the
    /// withheld emails are released.
    /// </summary>
    private async Task<PayPalSubscriptionAnomaly> RecordAfterGraceAsync(PayPalSubscriptionDetails details)
    {
        await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            null,
            "https://streamtunes.example");
        AdvancePastGrace();
        return await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            null,
            "https://streamtunes.example");
    }

    private void AdvancePastGrace() => _timeProvider.Advance(
        TimeSpan.FromMinutes(PayPalSubscriptionDefaults.AnomalyNotificationGraceMinutes + 1));

    private static PayPalSubscriptionDetails CreateDetails(string status) => new()
    {
        Id = "I-MISMATCH",
        PlanId = "P-PLAN",
        Status = status,
        StartTime = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
        NextBillingTime = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        FailedPaymentsCount = status == SubscriptionStatuses.Suspended ? 1 : 0
    };

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
