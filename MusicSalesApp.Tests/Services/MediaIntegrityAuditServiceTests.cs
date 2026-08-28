using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The audit no longer decodes anything itself: <c>RunAsync</c> dispatches one probe message per
/// candidate to the Azure Function, and the run completes on the last result to come back through
/// <c>RecordProbedItemAsync</c>. These tests drive both halves - dispatch, then the probe results
/// the Function would have posted.
/// </summary>
[TestFixture]
public class MediaIntegrityAuditServiceTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IAzureStorageService> _storage = null!;
    private Mock<IEmailService> _email = null!;
    private Mock<IBackgroundJobClient> _jobs = null!;
    private Mock<IHlsPackageIntegrityChecker> _hlsPackages = null!;
    private RecordingQueueClient _queue = null!;
    private MediaIntegrityAuditService _service = null!;
    private AudioProbeResultHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"media-audit-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _storage = new Mock<IAzureStorageService>();
        _storage.Setup(service => service.ExistsAsync("Boof/Boof.wav")).ReturnsAsync(true);
        _email = new Mock<IEmailService>();
        _email.Setup(service => service.GetAppBaseUrl()).Returns("https://streamtunes.test");
        _email.Setup(service => service.GetEmailLogoHtml()).Returns("<div>logo</div>");
        _email.Setup(service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _jobs = new Mock<IBackgroundJobClient>();
        _jobs.Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        _queue = new RecordingQueueClient();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            [AppSettingKeys.EmailAdminEmail] = "audit-admin@example.com"
        }).Build();

        // A checker that finds nothing, so these tests keep asserting the probe pipeline. The package
        // sweep is a separate question with its own fixture, and stubbing it also keeps the audit from
        // reaching for a storage account it has no business touching in a unit test.
        _hlsPackages = new Mock<IHlsPackageIntegrityChecker>();
        _hlsPackages
            .Setup(checker => checker.CheckAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new HlsPackageIntegrityReport());

        _service = new MediaIntegrityAuditService(
            _factory,
            _queue,
            _email.Object,
            configuration,
            _jobs.Object,
            _hlsPackages.Object,
            Mock.Of<ILogger<MediaIntegrityAuditService>>());

        _handler = new AudioProbeResultHandler(
            _factory,
            _storage.Object,
            _queue,
            _service,
            Mock.Of<ILogger<AudioProbeResultHandler>>());
    }

    [Test]
    public async Task RunAsync_DispatchesOneProbePerCandidateAndLeavesTheRunOpen()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);

        await _service.RunAsync(runId);

        await using var context = new AppDbContext(_options);
        var run = await context.MediaIntegrityAuditRuns.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(_queue.Probes, Has.Count.EqualTo(1));
            Assert.That(_queue.Probes[0].BlobPath, Is.EqualTo("Boof/Boof.mp3"));
            Assert.That(_queue.Probes[0].Attempt, Is.EqualTo(1));
            Assert.That(run.CandidateCount, Is.EqualTo(1));

            // Still Running: nothing has been decoded yet, so completing here would report an
            // audit that never actually looked at anything.
            Assert.That(run.Status, Is.EqualTo(MediaAuditRunStatus.Running));
        });
    }

    [Test]
    public async Task ReportOnly_RecordsRepairableEvidenceWithoutMutatingSong()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: null, duration: null);
        await _service.RunAsync(runId);

        await _handler.HandleAsync(PlayableProbe(runId, duration: 20));

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
        await _service.RunAsync(runId);

        await _handler.HandleAsync(PlayableProbe(runId, duration: 31));

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
    public async Task FirstUnplayableVerdict_IsReprobedRatherThanRecorded()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        await _service.RunAsync(runId);
        _queue.Probes.Clear();

        await _handler.HandleAsync(UnplayableProbe(runId, attempt: 1));

        await using var context = new AppDbContext(_options);
        Assert.Multiple(() =>
        {
            // Nothing recorded yet: one bad read must not condemn a song.
            Assert.That(context.MediaIntegrityAuditItems.Count(), Is.Zero);
            Assert.That(_queue.Probes, Has.Count.EqualTo(1));
            Assert.That(_queue.Probes[0].Attempt, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task FirstUnplayableVerdict_WhenTheReProbeCannotBeQueued_IsRecordedAsInconclusive()
    {
        // The re-probe is the only thing that would ever have counted this song. Dropping it on an
        // enqueue failure left ProcessedCount permanently short of CandidateCount, so the run could
        // never complete - and Inconclusive rather than ConfirmedUnplayable because the second read
        // that was supposed to justify condemning the song never happened.
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        await _service.RunAsync(runId);
        _queue.Probes.Clear();
        _queue.FailNextProbeEnqueue = true;

        await _handler.HandleAsync(UnplayableProbe(runId, attempt: 1));

        await using var context = new AppDbContext(_options);
        var item = await context.MediaIntegrityAuditItems.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(item.Outcome, Is.EqualTo(MediaAuditOutcome.Inconclusive));
            Assert.That(item.FailureCode, Is.EqualTo("ConfirmationNotAttempted"));
            Assert.That(item.Attempts, Is.EqualTo(1));
            Assert.That(_queue.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task RunAsync_WhenTheQueueIsNotConfigured_FailsTheRunRatherThanLeavingItRunning()
    {
        // Nothing can be dispatched, so no result will ever arrive to close the run. Failing it here
        // releases the single-run lock; going quiet would block every future audit until the
        // reconciler noticed hours later.
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        _queue.IsConfigured = false;

        Assert.ThrowsAsync<InvalidOperationException>(() => _service.RunAsync(runId));

        await using var context = new AppDbContext(_options);
        var run = await context.MediaIntegrityAuditRuns.SingleAsync(item => item.Id == runId);
        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo(MediaAuditRunStatus.Failed));
            Assert.That(run.ActiveLockKey, Is.Null, "A failed run must not keep holding the lock.");
            Assert.That(_queue.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task Quarantine_WhoseSongHasLostItsPlaybackBlob_IsNeitherDispatchedNorCounted()
    {
        // A probe carrying a null BlobPath throws inside the Function and poisons the message rather
        // than calling back, so it would never be counted either - and CandidateCount has to agree
        // with what was actually dispatched or the run can never reach its completion condition.
        var runId = await AddRunAndSongAsync(MediaAuditMode.QuarantineConfirmedFailures, title: "Boof", duration: 10);
        await using (var setup = new AppDbContext(_options))
        {
            var song = await setup.SongMetadata.SingleAsync();
            song.Mp3BlobPath = null;
            await setup.SaveChangesAsync();
        }

        await _service.RunAsync(runId);

        await using var context = new AppDbContext(_options);
        var run = await context.MediaIntegrityAuditRuns.SingleAsync(item => item.Id == runId);
        Assert.Multiple(() =>
        {
            Assert.That(_queue.Probes, Is.Empty);
            Assert.That(run.CandidateCount, Is.Zero);
            Assert.That(
                run.Status,
                Is.EqualTo(MediaAuditRunStatus.Completed),
                "With nothing to probe there is no callback coming, so the run closes itself.");
        });
    }

    [Test]
    public async Task SecondAttemptSucceeding_DowngradesTheVerdictToInconclusive()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        await _service.RunAsync(runId);

        await _handler.HandleAsync(UnplayableProbe(runId, attempt: 1));
        await _handler.HandleAsync(PlayableProbe(runId, duration: 10, attempt: 2));

        await using var context = new AppDbContext(_options);
        var item = await context.MediaIntegrityAuditItems.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(item.Outcome, Is.EqualTo(MediaAuditOutcome.Inconclusive));
            Assert.That(item.FailureCode, Is.EqualTo("FailureNotReproduced"));
            Assert.That(item.Attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Quarantine_RevalidatesFailureAndPreservesEveryBlob()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.QuarantineConfirmedFailures, title: "Boof", duration: 10);

        await _service.RunAsync(runId);

        // Fails twice, so the verdict is confirmed rather than written off as a transient read.
        await _handler.HandleAsync(UnplayableProbe(runId, attempt: 1));
        await _handler.HandleAsync(UnplayableProbe(runId, attempt: 2));

        await using var verify = new AppDbContext(_options);
        var saved = await verify.SongMetadata.SingleAsync();
        var item = await verify.MediaIntegrityAuditItems
            .Where(entry => entry.AuditRunId == runId)
            .OrderBy(entry => entry.Id)
            .LastAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.IsEnabled, Is.False);
            Assert.That(saved.IsActive, Is.True);
            Assert.That(item.Quarantined, Is.True);
            Assert.That(item.Attempts, Is.EqualTo(2));
            Assert.That(verify.SongStatusHistories.Count(), Is.EqualTo(1));
        });

        // Quarantine hides a song; it never destroys the creator's audio.
        _storage.Verify(service => service.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task DecoderInfrastructureFailure_IsInconclusiveAndNeverQuarantined()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.QuarantineConfirmedFailures, title: "Boof", duration: 10);
        await _service.RunAsync(runId);

        await _handler.HandleAsync(new AudioProbeResult
        {
            ProbeId = Guid.NewGuid(),
            Kind = AudioProbeKind.MediaIntegrityAudit,
            AuditRunId = runId,
            SongMetadataId = await SingleSongIdAsync(),
            BlobPath = "Boof/Boof.mp3",
            Attempt = 1,
            BlobExists = true,
            BlobLength = 10,
            ContentType = "audio/mpeg",
            DetectedFormat = ".mp3",
            Outcome = AudioProcessingOutcome.Inconclusive,
            FailureCode = "FfmpegUnavailable",
            Diagnostic = "FFmpeg was not available."
        });

        await using var context = new AppDbContext(_options);
        var item = await context.MediaIntegrityAuditItems
            .SingleAsync(entry => entry.AuditRunId == runId);
        var song = await context.SongMetadata.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(item.Outcome, Is.EqualTo(MediaAuditOutcome.Inconclusive));
            Assert.That(item.Quarantined, Is.False);
            Assert.That(item.FailureCode, Is.EqualTo("FfmpegUnavailable"));

            // A worker that could not run FFmpeg says nothing about the song.
            Assert.That(song.IsEnabled, Is.True);
        });
    }

    [Test]
    public async Task MissingPlaybackBlob_IsConfirmedUnplayable()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        await _service.RunAsync(runId);

        await _handler.HandleAsync(new AudioProbeResult
        {
            ProbeId = Guid.NewGuid(),
            Kind = AudioProbeKind.MediaIntegrityAudit,
            AuditRunId = runId,
            SongMetadataId = await SingleSongIdAsync(),
            BlobPath = "Boof/Boof.mp3",
            Attempt = 2,
            BlobExists = false,
            Outcome = AudioProcessingOutcome.Unplayable
        });

        await using var context = new AppDbContext(_options);
        var item = await context.MediaIntegrityAuditItems.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(item.Outcome, Is.EqualTo(MediaAuditOutcome.ConfirmedUnplayable));
            Assert.That(item.FailureCode, Is.EqualTo("PlaybackBlobMissing"));
        });
    }

    [Test]
    public async Task DuplicateProbeResult_IsIgnored()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        await _service.RunAsync(runId);

        // A queue retry after the HTTP response was lost replays the same result. Recording it
        // twice would double-count the run and could quarantine the same song twice.
        await _handler.HandleAsync(PlayableProbe(runId, duration: 10));
        await _handler.HandleAsync(PlayableProbe(runId, duration: 10));

        await using var context = new AppDbContext(_options);
        Assert.That(context.MediaIntegrityAuditItems.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task ResultForADeletedSong_StillCountsTowardsTheRun()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        await _service.RunAsync(runId);

        var songId = await SingleSongIdAsync();
        await using (var context = new AppDbContext(_options))
        {
            context.SongMetadata.RemoveRange(context.SongMetadata);
            await context.SaveChangesAsync();
        }

        await _handler.HandleAsync(new AudioProbeResult
        {
            ProbeId = Guid.NewGuid(),
            Kind = AudioProbeKind.MediaIntegrityAudit,
            AuditRunId = runId,
            SongMetadataId = songId,
            BlobPath = "Boof/Boof.mp3",
            Attempt = 1,
            BlobExists = true,
            DurationSeconds = 10,
            Outcome = AudioProcessingOutcome.Playable
        });

        await using var verify = new AppDbContext(_options);
        var run = await verify.MediaIntegrityAuditRuns.SingleAsync();
        Assert.Multiple(() =>
        {
            // Otherwise the run never reaches its candidate count and sits Running forever,
            // blocking every future run through the single-run lock.
            Assert.That(run.Status, Is.EqualTo(MediaAuditRunStatus.Completed));
            Assert.That(verify.MediaIntegrityAuditItems.Single().FailureCode, Is.EqualTo("SongUnavailable"));
        });
    }

    [Test]
    public async Task RunWithNoCandidates_CompletesImmediately()
    {
        int runId;
        await using (var context = new AppDbContext(_options))
        {
            var run = new MediaIntegrityAuditRun
            {
                Mode = MediaAuditMode.ReportOnly,
                Status = MediaAuditRunStatus.Queued
            };
            context.MediaIntegrityAuditRuns.Add(run);
            await context.SaveChangesAsync();
            runId = run.Id;
        }

        await _service.RunAsync(runId);

        await using var verify = new AppDbContext(_options);
        var completed = await verify.MediaIntegrityAuditRuns.SingleAsync();
        Assert.Multiple(() =>
        {
            // No probes means no callback will ever arrive to close this out.
            Assert.That(completed.Status, Is.EqualTo(MediaAuditRunStatus.Completed));
            Assert.That(_queue.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task ReconcileStalledRuns_ClosesARunWhoseResultsStoppedArriving()
    {
        var runId = await AddRunAndSongAsync(MediaAuditMode.ReportOnly, title: "Boof", duration: 10);
        await _service.RunAsync(runId);

        // Pretend the dispatch happened long ago and no probe ever came back - a poisoned message,
        // or an instance that died mid-decode.
        await using (var context = new AppDbContext(_options))
        {
            var run = await context.MediaIntegrityAuditRuns.SingleAsync();
            run.StartedAt = DateTime.UtcNow.AddHours(-5);
            await context.SaveChangesAsync();
        }

        await _service.ReconcileStalledRunsAsync();

        await using var verify = new AppDbContext(_options);
        Assert.That(
            (await verify.MediaIntegrityAuditRuns.SingleAsync()).Status,
            Is.EqualTo(MediaAuditRunStatus.Completed));
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

        _email.Setup(service => service.SendEmailAsync(
                "audit-admin@example.com", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _email.SetupSequence(service => service.SendEmailAsync(
                "initiator@example.com", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        await _service.RunAsync(runId);

        // The last probe result completes the run and sends the notifications. The stalled-run
        // sweep then retries only the recipient that failed.
        await _handler.HandleAsync(PlayableProbe(runId, duration: 10));
        await MarkRunStaleAsync(runId);
        await _service.ReconcileStalledRunsAsync();

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

    private async Task MarkRunStaleAsync(int runId)
    {
        await using var context = new AppDbContext(_options);
        var run = await context.MediaIntegrityAuditRuns.SingleAsync(item => item.Id == runId);
        run.Status = MediaAuditRunStatus.Running;
        run.StartedAt = DateTime.UtcNow.AddHours(-5);
        foreach (var item in context.MediaIntegrityAuditItems.Where(entry => entry.AuditRunId == runId))
        {
            item.CheckedAt = DateTime.UtcNow.AddHours(-5);
        }

        await context.SaveChangesAsync();
    }

    private async Task<int> AddRunAndSongAsync(MediaAuditMode mode, string title, double? duration)
    {
        await using var context = new AppDbContext(_options);
        context.SongMetadata.Add(NewSong(title, duration));
        await context.SaveChangesAsync();

        if (mode == MediaAuditMode.QuarantineConfirmedFailures)
        {
            // Quarantine runs work from a completed source run's confirmed failures.
            var songId = await context.SongMetadata.Select(song => song.Id).SingleAsync();
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
                SongMetadataId = songId,
                EffectiveTitle = title,
                PlaybackBlobPath = "Boof/Boof.mp3",
                Outcome = MediaAuditOutcome.ConfirmedUnplayable
            });

            var quarantine = new MediaIntegrityAuditRun
            {
                Mode = mode,
                SourceRunId = source.Id,
                Status = MediaAuditRunStatus.Queued
            };
            context.MediaIntegrityAuditRuns.Add(quarantine);
            await context.SaveChangesAsync();
            return quarantine.Id;
        }

        var run = new MediaIntegrityAuditRun { Mode = mode, Status = MediaAuditRunStatus.Queued };
        context.MediaIntegrityAuditRuns.Add(run);
        await context.SaveChangesAsync();
        return run.Id;
    }

    private async Task<int> SingleSongIdAsync()
    {
        await using var context = new AppDbContext(_options);
        return await context.SongMetadata.Select(song => song.Id).FirstAsync();
    }

    private AudioProbeResult PlayableProbe(int runId, double duration, int attempt = 1)
        => new()
        {
            ProbeId = Guid.NewGuid(),
            Kind = AudioProbeKind.MediaIntegrityAudit,
            AuditRunId = runId,
            SongMetadataId = SingleSongIdAsync().GetAwaiter().GetResult(),
            BlobPath = "Boof/Boof.mp3",
            Attempt = attempt,
            BlobExists = true,
            BlobLength = 10,
            ContentType = "audio/mpeg",
            ETag = "etag",
            DetectedFormat = ".mp3",
            DurationSeconds = duration,
            Outcome = AudioProcessingOutcome.Playable
        };

    private AudioProbeResult UnplayableProbe(int runId, int attempt)
        => new()
        {
            ProbeId = Guid.NewGuid(),
            Kind = AudioProbeKind.MediaIntegrityAudit,
            AuditRunId = runId,
            SongMetadataId = SingleSongIdAsync().GetAwaiter().GetResult(),
            BlobPath = "Boof/Boof.mp3",
            Attempt = attempt,
            BlobExists = true,
            BlobLength = 10,
            ContentType = "audio/mpeg",
            DetectedFormat = ".mp3",
            Outcome = AudioProcessingOutcome.Unplayable,
            FailureCode = "DecoderRejected",
            Diagnostic = "Invalid audio data."
        };

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

    /// <summary>Captures what would have gone on the queue, and replays nothing.</summary>
    private sealed class RecordingQueueClient : IMediaProcessingQueueClient
    {
        public List<AudioProbeRequest> Probes { get; } = [];
        public List<AudioTranscodeRequest> Transcodes { get; } = [];

        public bool IsConfigured { get; set; } = true;
        public bool IsCoverArtMatchConfigured => true;

        /// <summary>Makes the next probe enqueue throw, as a storage outage would.</summary>
        public bool FailNextProbeEnqueue { get; set; }

        public Task EnqueueTranscodeAsync(AudioTranscodeRequest request, CancellationToken cancellationToken = default)
        {
            Transcodes.Add(request);
            return Task.CompletedTask;
        }

        public Task EnqueueCoverArtMatchAsync(CoverArtMatchRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public bool IsPackagingConfigured => true;

        public List<AudioPackageRequest> PackageRequests { get; } = new();

        public Task EnqueuePackageAsync(AudioPackageRequest request, CancellationToken cancellationToken = default)
        {
            PackageRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task EnqueueProbesAsync(IEnumerable<AudioProbeRequest> requests, CancellationToken cancellationToken = default)
        {
            if (FailNextProbeEnqueue)
            {
                FailNextProbeEnqueue = false;
                throw new InvalidOperationException("The queue is unreachable.");
            }

            Probes.AddRange(requests);
            return Task.CompletedTask;
        }
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
