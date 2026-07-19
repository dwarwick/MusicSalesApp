using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MediaIntegrityAuditServiceTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IAzureStorageService> _storage = null!;
    private Mock<IMusicService> _music = null!;
    private Mock<IEmailService> _email = null!;
    private Mock<IBackgroundJobClient> _jobs = null!;
    private MediaIntegrityAuditService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"media-audit-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _storage = new Mock<IAzureStorageService>();
        _music = new Mock<IMusicService>();
        _email = new Mock<IEmailService>();
        _email.Setup(service => service.GetAppBaseUrl()).Returns("https://streamtunes.test");
        _email.Setup(service => service.GetEmailLogoHtml()).Returns("<div>logo</div>");
        _email.Setup(service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _jobs = new Mock<IBackgroundJobClient>();
        _jobs.Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            [AppSettingKeys.EmailAdminEmail] = "audit-admin@example.com"
        }).Build();
        _service = new MediaIntegrityAuditService(
            _factory,
            _storage.Object,
            _music.Object,
            _email.Object,
            configuration,
            _jobs.Object,
            Mock.Of<ILogger<MediaIntegrityAuditService>>());
    }

    [Test]
    public async Task ReportOnly_RecordsRepairableEvidenceWithoutMutatingSong()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: null, duration: null);
        SetupHealthyBlob(duration: 20);

        await _service.RunAsync(runId);

        await using var context = new AppDbContext(_options);
        var song = await context.SongMetadata.SingleAsync();
        var run = await context.MediaIntegrityAuditRuns.Include(item => item.Items).SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(song.SongTitle, Is.Null);
            Assert.That(song.TrackLength, Is.Null);
            Assert.That(song.IsEnabled, Is.True);
            Assert.That(run.Status, Is.EqualTo(MediaAuditRunStatus.Completed));
            Assert.That(run.Items.Single().Outcome, Is.EqualTo(MediaAuditOutcome.MetadataRepairable));
            Assert.That(run.RepairedCount, Is.Zero);
        });
    }

    [Test]
    public void StartQuarantine_WithoutCompletedSourceRun_IsRejected()
        => Assert.ThrowsAsync<InvalidOperationException>(() => _service.StartAsync(
            MediaAuditMode.QuarantineConfirmedFailures,
            initiatedByUserId: 1,
            initiatedByEmail: "admin@example.com"));

    [Test]
    public async Task StartAudit_WhenAnotherRunIsQueued_IsRejected()
    {
        await using (var context = new AppDbContext(_options))
        {
            context.MediaIntegrityAuditRuns.Add(new MediaIntegrityAuditRun
            {
                Mode = MediaAuditMode.ReportOnly,
                Status = MediaAuditRunStatus.Queued
            });
            await context.SaveChangesAsync();
        }

        Assert.ThrowsAsync<InvalidOperationException>(() => _service.StartAsync(
            MediaAuditMode.ReportOnly,
            initiatedByUserId: 1,
            initiatedByEmail: "admin@example.com"));
    }

    [Test]
    public async Task RepairSafeMetadata_RepairsOnlyProvenHealthyPlayback()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.RepairSafeMetadata, title: null, duration: null);
        SetupHealthyBlob(duration: 31);

        await _service.RunAsync(runId);

        await using var context = new AppDbContext(_options);
        var song = await context.SongMetadata.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(song.SongTitle, Is.EqualTo("Boof"));
            Assert.That(song.TrackLength, Is.EqualTo(31));
            Assert.That(song.IsEnabled, Is.True);
            Assert.That(context.MediaIntegrityAuditItems.Single().MetadataRepaired, Is.True);
        });
    }

    [Test]
    public async Task Quarantine_RevalidatesFailureAndPreservesEveryBlob()
    {
        await using (var context = new AppDbContext(_options))
        {
            var song = NewSong("Boof", 10);
            context.SongMetadata.Add(song);
            await context.SaveChangesAsync();
            var source = new MediaIntegrityAuditRun
            {
                Mode = MediaAuditMode.ReportOnly,
                Status = MediaAuditRunStatus.Completed,
                CompletedAt = DateTime.UtcNow
            };
            context.MediaIntegrityAuditRuns.Add(source);
            await context.SaveChangesAsync();
            context.MediaIntegrityAuditItems.Add(new MediaIntegrityAuditItem
            {
                AuditRunId = source.Id,
                SongMetadataId = song.Id,
                EffectiveTitle = "Boof",
                PlaybackBlobPath = song.Mp3BlobPath,
                Outcome = MediaAuditOutcome.ConfirmedUnplayable
            });
            var quarantine = new MediaIntegrityAuditRun
            {
                Mode = MediaAuditMode.QuarantineConfirmedFailures,
                SourceRunId = source.Id,
                Status = MediaAuditRunStatus.Queued
            };
            context.MediaIntegrityAuditRuns.Add(quarantine);
            await context.SaveChangesAsync();
            var runId = quarantine.Id;

            SetupHealthyStorageProperties();
            _music.Setup(service => service.ValidateAudioDecodeAsync(
                    It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AudioDecodeResult.Unplayable("DecoderRejected", "Invalid audio data."));
            await _service.RunAsync(runId);
        }

        await using var verify = new AppDbContext(_options);
        var saved = await verify.SongMetadata.SingleAsync();
        var item = await verify.MediaIntegrityAuditItems.OrderBy(entry => entry.Id).LastAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.IsEnabled, Is.False);
            Assert.That(saved.IsActive, Is.True);
            Assert.That(item.Quarantined, Is.True);
            Assert.That(item.Attempts, Is.EqualTo(2));
            Assert.That(verify.SongStatusHistories.Count(), Is.EqualTo(1));
        });
        _storage.Verify(service => service.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task DecoderInfrastructureFailure_IsInconclusiveAndNeverQuarantined()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        SetupHealthyStorageProperties();
        _music.Setup(service => service.ValidateAudioDecodeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AudioDecodeResult.Inconclusive("FfmpegUnavailable", "FFmpeg was not available."));

        await _service.RunAsync(runId);

        await using var context = new AppDbContext(_options);
        var item = await context.MediaIntegrityAuditItems.SingleAsync();
        var song = await context.SongMetadata.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(item.Outcome, Is.EqualTo(MediaAuditOutcome.Inconclusive));
            Assert.That(item.Quarantined, Is.False);
            Assert.That(item.FailureCode, Is.EqualTo("FfmpegUnavailable"));
            Assert.That(song.IsEnabled, Is.True);
        });
    }

    [Test]
    public async Task StartAudit_WhenHangfireEnqueueFails_ReleasesActiveRunLock()
    {
        _jobs.Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Throws(new InvalidOperationException("Hangfire unavailable"));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => _service.StartAsync(
            MediaAuditMode.ReportOnly,
            initiatedByUserId: 1,
            initiatedByEmail: "admin@example.com"));

        Assert.That(exception!.Message, Does.Contain("could not be queued"));
        await using var context = new AppDbContext(_options);
        var run = await context.MediaIntegrityAuditRuns.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo(MediaAuditRunStatus.Failed));
            Assert.That(run.ActiveLockKey, Is.Null);
            Assert.That(run.CompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task CompletionNotificationRetry_DoesNotResendSuccessfulRecipient()
    {
        int runId;
        await using (var context = new AppDbContext(_options))
        {
            context.SongMetadata.Add(NewSong("Boof", 10));
            var run = new MediaIntegrityAuditRun
            {
                Mode = MediaAuditMode.ReportOnly,
                Status = MediaAuditRunStatus.Queued,
                InitiatedByEmail = "initiator@example.com"
            };
            context.MediaIntegrityAuditRuns.Add(run);
            await context.SaveChangesAsync();
            runId = run.Id;
        }
        SetupHealthyBlob(10);
        _email.Setup(service => service.SendEmailAsync(
                "audit-admin@example.com", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _email.SetupSequence(service => service.SendEmailAsync(
                "initiator@example.com", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        await _service.RunAsync(runId);
        await _service.RunAsync(runId);

        _email.Verify(service => service.SendEmailAsync(
            "audit-admin@example.com", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _email.Verify(service => service.SendEmailAsync(
            "initiator@example.com", It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        await using var verify = new AppDbContext(_options);
        var notifications = await verify.MediaIntegrityAuditNotifications.ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(notifications, Has.Count.EqualTo(2));
            Assert.That(notifications, Has.All.Matches<MediaIntegrityAuditNotification>(item => item.Sent));
            Assert.That(notifications.Single(item => item.Recipient == "audit-admin@example.com").Attempts, Is.EqualTo(1));
            Assert.That(notifications.Single(item => item.Recipient == "initiator@example.com").Attempts, Is.EqualTo(2));
        });
    }

    private async Task<int> AddRunAndSongAsync(MediaAuditMode mode, string title, double? duration)
    {
        await using var context = new AppDbContext(_options);
        context.SongMetadata.Add(NewSong(title, duration));
        var run = new MediaIntegrityAuditRun { Mode = mode, Status = MediaAuditRunStatus.Queued };
        context.MediaIntegrityAuditRuns.Add(run);
        await context.SaveChangesAsync();
        return run.Id;
    }

    private static SongMetadata NewSong(string title, double? duration) => new()
    {
        BlobPath = "Boof/Boof.mp3",
        Mp3BlobPath = "Boof/Boof.mp3",
        ImageBlobPath = "Boof/Boof.png",
        OriginalAudioBlobPath = "Boof/Boof.wav",
        SongTitle = title,
        TrackLength = duration,
        IsActive = true,
        IsEnabled = true,
        IsAlbumCover = false
    };

    private void SetupHealthyBlob(double duration)
    {
        SetupHealthyStorageProperties();
        _music.Setup(service => service.ValidateAudioDecodeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AudioDecodeResult.Playable(duration));
    }

    private void SetupHealthyStorageProperties()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        _storage.Setup(service => service.GetFileInfoAsync("Boof/Boof.mp3"))
            .ReturnsAsync(new StorageFileInfo
            {
                Name = "Boof/Boof.mp3",
                Length = bytes.Length,
                ContentType = "audio/mpeg",
                ETag = "etag"
            });
        _storage.Setup(service => service.OpenReadAsync("Boof/Boof.mp3"))
            .ReturnsAsync(() => new MemoryStream(bytes));
        _storage.Setup(service => service.ExistsAsync("Boof/Boof.wav")).ReturnsAsync(true);
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
