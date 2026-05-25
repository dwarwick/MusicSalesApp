using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ContactRequestAdminServiceTests
{
    private DbContextOptions<AppDbContext> _options;
    private ContactRequestAdminService _service;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ContactRequestAdminTests_{Guid.NewGuid()}")
            .Options;
        _service = new ContactRequestAdminService(new TestDbContextFactory(_options));
    }

    [Test]
    public async Task GetSubmissionsAsync_ReturnsNewestSubmissionsForGrid()
    {
        await using (var context = new AppDbContext(_options))
        {
            context.ContactRequestSubmissions.AddRange(
                CreateSubmission(1, "old@example.com", "Bug Report", "Older message", new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc)),
                CreateSubmission(2, "new@example.com", "App Suggestion", "Newer message", new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc)));
            await context.SaveChangesAsync();
        }

        var submissions = await _service.GetSubmissionsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(submissions, Has.Count.EqualTo(2));
            Assert.That(submissions[0].UserEmail, Is.EqualTo("new@example.com"));
            Assert.That(submissions[0].Subject, Is.EqualTo("App Suggestion"));
            Assert.That(submissions[0].MessageText, Is.EqualTo("Newer message"));
            Assert.That(submissions[1].UserEmail, Is.EqualTo("old@example.com"));
        });
    }

    [Test]
    public async Task GetSubmissionsAsync_ProvidesFilterableDisplayProperties()
    {
        await using (var context = new AppDbContext(_options))
        {
            var submission = CreateSubmission(
                7,
                "listener@example.com",
                "General Question / Comment",
                "First line\nSecond line",
                new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc));
            submission.UserEmailSent = true;
            submission.AdminEmailSent = false;
            context.ContactRequestSubmissions.Add(submission);
            await context.SaveChangesAsync();
        }

        var result = (await _service.GetSubmissionsAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.MessagePreview, Is.EqualTo("First line Second line"));
            Assert.That(result.EmailStatus, Is.EqualTo("User sent"));
        });
    }

    private static ContactRequestSubmission CreateSubmission(int id, string userEmail, string subject, string messageText, DateTime submittedAtUtc)
    {
        return new ContactRequestSubmission
        {
            Id = id,
            UserId = id,
            UserEmail = userEmail,
            Subject = subject,
            MessageText = messageText,
            MessageLength = messageText.Length,
            IpAddress = "192.0.2.1",
            SubmittedAtUtc = submittedAtUtc,
            UserEmailSent = true,
            AdminEmailSent = true,
            EmailSendCompletedAtUtc = submittedAtUtc.AddSeconds(5)
        };
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