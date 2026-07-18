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
                "customerservice@example.com",
                It.Is<string>(subject => subject.Contains(first.CorrelationId)),
                It.Is<string>(body => body.Contains("Missing entitlement boundary")
                    && body.Contains("I-MISMATCH"))),
            Times.Once);
    }

    [Test]
    public async Task ResolveOpenEpisodeAsync_AllowsLaterMismatchToCreateNewEpisode()
    {
        var first = await _service.RecordMismatchAsync(
            _subscription,
            _user,
            CreateDetails(SubscriptionStatuses.Suspended),
            null,
            "https://streamtunes.example");

        await _service.ResolveOpenEpisodeAsync(_subscription.Id);
        _timeProvider.Advance(TimeSpan.FromDays(1));
        var second = await _service.RecordMismatchAsync(
            _subscription,
            _user,
            CreateDetails(SubscriptionStatuses.Active),
            null,
            "https://streamtunes.example");

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
        await _service.RecordMismatchAsync(
            _subscription,
            _user,
            details,
            null,
            "https://streamtunes.example");
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
            service => service.SendEmailAsync("customerservice@example.com", It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

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
