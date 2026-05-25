using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ContactRequestRateLimitServiceTests
{
    private DbContextOptions<AppDbContext> _options;
    private TestTimeProvider _timeProvider;
    private ContactRequestRateLimitService _service;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ContactRateLimitTests_{Guid.NewGuid()}")
            .Options;
        _timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));
        _service = new ContactRequestRateLimitService(
            new TestDbContextFactory(_options),
            _timeProvider,
            Mock.Of<ILogger<ContactRequestRateLimitService>>());
    }

    [Test]
    public async Task TryReserveSubmissionAsync_FirstSubmission_IsAllowedAndStoresMetadataOnly()
    {
        var result = await _service.TryReserveSubmissionAsync(7, "user@example.com", "Bug Report", 123, "192.0.2.1");

        Assert.That(result.IsAllowed, Is.True);
        await using var context = new AppDbContext(_options);
        var submission = await context.ContactRequestSubmissions.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(submission.UserId, Is.EqualTo(7));
            Assert.That(submission.UserEmail, Is.EqualTo("user@example.com"));
            Assert.That(submission.Subject, Is.EqualTo("Bug Report"));
            Assert.That(submission.MessageLength, Is.EqualTo(123));
            Assert.That(submission.IpAddress, Is.EqualTo("192.0.2.1"));
        });
    }

    [Test]
    public async Task TryReserveSubmissionAsync_BlocksSubmissionWithinShortWindow()
    {
        await _service.TryReserveSubmissionAsync(7, "user@example.com", "Bug Report", 123, "192.0.2.1");
        _timeProvider.UtcNow = _timeProvider.UtcNow.AddMinutes(5);

        var result = await _service.TryReserveSubmissionAsync(7, "user@example.com", "Bug Report", 124, "192.0.2.1");

        Assert.That(result.IsAllowed, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("10 minutes"));
    }

    [Test]
    public async Task TryReserveSubmissionAsync_BlocksUserDailyLimit()
    {
        await SeedSubmissionsAsync(userId: 7, ipAddress: "192.0.2.1", count: ContactRequestRateLimitService.MaxSubmissionsPerUserPerDay, useDistinctUsers: false);

        var result = await _service.TryReserveSubmissionAsync(7, "user@example.com", "Bug Report", 123, "192.0.2.1");

        Assert.That(result.IsAllowed, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("daily contact form limit"));
    }

    [Test]
    public async Task TryReserveSubmissionAsync_BlocksIpDailyLimit()
    {
        await SeedSubmissionsAsync(userId: 100, ipAddress: "192.0.2.9", count: ContactRequestRateLimitService.MaxSubmissionsPerIpPerDay, useDistinctUsers: true);

        var result = await _service.TryReserveSubmissionAsync(7, "user@example.com", "Bug Report", 123, "192.0.2.9");

        Assert.That(result.IsAllowed, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("network"));
    }

    [Test]
    public async Task MarkEmailResultAsync_UpdatesEmailStatus()
    {
        var reservation = await _service.TryReserveSubmissionAsync(7, "user@example.com", "Bug Report", 123, "192.0.2.1");

        await _service.MarkEmailResultAsync(reservation.SubmissionId!.Value, userEmailSent: true, adminEmailSent: false);

        await using var context = new AppDbContext(_options);
        var submission = await context.ContactRequestSubmissions.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(submission.UserEmailSent, Is.True);
            Assert.That(submission.AdminEmailSent, Is.False);
            Assert.That(submission.EmailSendCompletedAtUtc, Is.Not.Null);
        });
    }

    private async Task SeedSubmissionsAsync(int userId, string ipAddress, int count, bool useDistinctUsers)
    {
        await using var context = new AppDbContext(_options);
        for (var i = 0; i < count; i++)
        {
            context.ContactRequestSubmissions.Add(new ContactRequestSubmission
            {
                UserId = useDistinctUsers ? userId + i : userId,
                UserEmail = $"user{i}@example.com",
                Subject = "Bug Report",
                MessageLength = 50,
                IpAddress = ipAddress,
                SubmittedAtUtc = _timeProvider.UtcNow.UtcDateTime.AddMinutes(-15 - i)
            });
        }

        await context.SaveChangesAsync();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public TestTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppDbContext(_options));
    }
}