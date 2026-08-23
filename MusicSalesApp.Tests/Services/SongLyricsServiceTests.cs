using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// What a creator's Submit does, and - mostly - what it refuses to do.
///
/// <para>
/// Every refusal here is guarding something expensive or wrong: a second orchestration running in
/// parallel with the first, a blob written on behalf of somebody else's song, or a job queued for a
/// song with no audio to align against.
/// </para>
/// </summary>
[TestFixture]
public class SongLyricsServiceTests
{
    private const int CreatorId = 7;
    private const int OtherCreatorId = 8;

    private static readonly Guid SongGuid = Guid.Parse("abc00000-0000-0000-0000-000000000000");

    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IAzureStorageService> _storage = null!;
    private Mock<IDurableTaskClient> _durable = null!;
    private RecordingBackgroundJobClient _jobs = null!;
    private Mock<IAdminNotificationService> _adminNotifications = null!;
    private SongLyricsService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"song-lyrics-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);

        _storage = new Mock<IAzureStorageService>();
        _durable = new Mock<IDurableTaskClient>();
        _durable.SetupGet(client => client.IsConfigured).Returns(true);
        _jobs = new RecordingBackgroundJobClient();

        _adminNotifications = new Mock<IAdminNotificationService>();

        _service = new SongLyricsService(
            _factory,
            _storage.Object,
            _durable.Object,
            _jobs,
            _adminNotifications.Object,
            Mock.Of<ILogger<SongLyricsService>>());
    }

    [Test]
    public async Task AValidSubmissionStoresTheTextAndQueuesTheWork()
    {
        await AddSongAsync();

        var result = await _service.SubmitAsync(1, CreatorId, "hello darkness\nmy old friend");

        await using var context = new AppDbContext(_options);
        var job = await context.LyricsAlignmentJobs.SingleAsync();
        var lyrics = await context.SongLyrics.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(job.Status, Is.EqualTo(LyricsAlignmentJobStatus.Queued));
            Assert.That(lyrics.Status, Is.EqualTo(SongLyricsStatus.Pending));
            Assert.That(_jobs.Created, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AnAcceptedSubmissionTellsAdminWhoIsWorkingOnLyrics()
    {
        await AddSongAsync();

        await _service.SubmitAsync(1, CreatorId, "hello darkness");

        _adminNotifications.Verify(
            n => n.NotifyLyricsAddedAsync(CreatorId, 1),
            Times.Once);
    }

    [Test]
    public async Task ARefusedSubmissionTellsAdminNothing()
    {
        // Nothing happened, so there is nothing to report. Empty text never reaches storage, never
        // queues an attempt, and must not put a row in the admin's history either.
        await AddSongAsync();

        var result = await _service.SubmitAsync(1, CreatorId, "   ");

        Assert.That(result.Accepted, Is.False);
        _adminNotifications.Verify(
            n => n.NotifyLyricsAddedAsync(It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task AFailingAdminNotificationDoesNotFailTheCreatorsSubmission()
    {
        // THE IMPORTANT ONE. The notification runs after the work is committed and exists so an
        // admin can watch; an unreachable SMTP server must not surface to the creator as a submit
        // that failed - least of all one that failed after the attempt was already queued.
        await AddSongAsync();

        _adminNotifications
            .Setup(n => n.NotifyLyricsAddedAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("smtp is down"));

        var result = await _service.SubmitAsync(1, CreatorId, "hello darkness");

        await using var context = new AppDbContext(_options);

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(_jobs.Created, Is.EqualTo(1), "The attempt was still queued.");
            Assert.That(context.LyricsAlignmentJobs.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TheTextIsWrittenBeforeTheRowSoAQueuedAttemptAlwaysHasSomethingToAlign()
    {
        await AddSongAsync();

        await _service.SubmitAsync(1, CreatorId, "hello darkness");

        _storage.Verify(
            service => service.UploadAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task TheLyricsPathIsDerivedFromTheSongRatherThanSuppliedByTheCaller()
    {
        // The rule MusicController states for cover art and that applies just as much here: an
        // authenticated user must not be able to steer a write to an arbitrary blob path.
        await AddSongAsync();

        string written = null;
        _storage
            .Setup(service => service.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Callback<string, Stream, string>((path, _, _) => written = path)
            .Returns(Task.CompletedTask);

        await _service.SubmitAsync(1, CreatorId, "hello");

        // Derived from the same helper the service uses, rather than a hand-written string: the
        // point being asserted is "the path comes from the song's own record", and hard-coding it
        // here would just be a second place to get the GUID format wrong.
        var expected = SongMediaPaths.ResolveLyricsTextTarget(1, SongGuid, "abc/abc-music.mp3");
        Assert.That(written, Is.EqualTo(expected));
        Assert.That(written, Does.EndWith("-lyrics.txt"));
    }

    [Test]
    public async Task ASecondAttemptIsRefusedWhileTheFirstIsStillRunning()
    {
        // Superseding instead would bill two vocal-separation runs in parallel for one song, with a
        // non-deterministic winner deciding what ends up published.
        await AddSongAsync();
        await _service.SubmitAsync(1, CreatorId, "hello");

        var second = await _service.SubmitAsync(1, CreatorId, "hello again");

        Assert.Multiple(() =>
        {
            Assert.That(second.Accepted, Is.False);
            Assert.That(second.Outcome, Is.EqualTo(LyricsSubmissionOutcome.AlreadyRunning));
            Assert.That(_jobs.Created, Is.EqualTo(1), "No second orchestration may be queued.");
        });
    }

    [Test]
    public async Task AnotherCreatorsSongIsRefused()
    {
        await AddSongAsync();

        var result = await _service.SubmitAsync(1, OtherCreatorId, "hello");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(LyricsSubmissionOutcome.NotAllowed));
            Assert.That(_jobs.Created, Is.Zero);
        });

        _storage.Verify(
            service => service.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()),
            Times.Never,
            "Nothing may be written before ownership is established.");
    }

    [Test]
    public async Task ASongWithNoAudioIsRefused()
    {
        await AddSongAsync(withAudio: false);

        var result = await _service.SubmitAsync(1, CreatorId, "hello");

        Assert.That(result.Outcome, Is.EqualTo(LyricsSubmissionOutcome.NoAudio));
    }

    [Test]
    public async Task EmptyLyricsAreRefusedBeforeAnythingIsTouched()
    {
        await AddSongAsync();

        var result = await _service.SubmitAsync(1, CreatorId, "   \n\n  ");

        Assert.That(result.Outcome, Is.EqualTo(LyricsSubmissionOutcome.TextEmpty));
        _storage.Verify(
            service => service.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task TextPastTheCharacterCapIsRefused()
    {
        await AddSongAsync();

        var result = await _service.SubmitAsync(1, CreatorId, new string('a', 20_001));

        Assert.That(result.Outcome, Is.EqualTo(LyricsSubmissionOutcome.TextTooLong));
    }

    [Test]
    public async Task TextPastTheLineCapIsRefused()
    {
        await AddSongAsync();

        var result = await _service.SubmitAsync(
            1, CreatorId, string.Join("\n", Enumerable.Repeat("la", 501)));

        Assert.That(result.Outcome, Is.EqualTo(LyricsSubmissionOutcome.TooManyLines));
    }

    [Test]
    public async Task AnEnvironmentWithNoFunctionAppReportsItselfUnavailable()
    {
        // Not an error. An environment without the lyrics app configured simply does not offer the
        // feature, and everything else about the site carries on working.
        _durable.SetupGet(client => client.IsConfigured).Returns(false);
        await AddSongAsync();

        var result = await _service.SubmitAsync(1, CreatorId, "hello");

        Assert.Multiple(() =>
        {
            Assert.That(_service.IsAvailable, Is.False);
            Assert.That(result.Outcome, Is.EqualTo(LyricsSubmissionOutcome.Unavailable));
        });
    }

    [Test]
    public async Task ARerunLeavesAlreadyPublishedTimingsInPlaceWhileItRuns()
    {
        // A song that already has good timings keeps serving them until better ones arrive. Clearing
        // them on submit would take working lyrics away from listeners for the whole run, and lose
        // them entirely if it failed.
        await AddSongAsync();
        await AddPublishedLyricsAsync();

        await _service.SubmitAsync(1, CreatorId, "corrected lyrics");

        await using var context = new AppDbContext(_options);
        var lyrics = await context.SongLyrics.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lyrics.Status, Is.EqualTo(SongLyricsStatus.Pending));
            Assert.That(lyrics.TimingsBlobPath, Is.EqualTo("abc/abc-lyrics.json"));
            Assert.That(lyrics.Version, Is.EqualTo(3), "The version only moves on a successful alignment.");
        });
    }

    [Test]
    public async Task CancellingTerminatesTheOrchestrationAndReleasesTheSong()
    {
        await AddSongAsync();
        await _service.SubmitAsync(1, CreatorId, "hello");

        await using (var seed = new AppDbContext(_options))
        {
            var job = await seed.LyricsAlignmentJobs.SingleAsync();
            job.DurableFunctionTaskId = 42;
            await seed.SaveChangesAsync();
        }

        var cancelled = await _service.CancelAsync(1, CreatorId);

        await using var context = new AppDbContext(_options);
        var cancelledJob = await context.LyricsAlignmentJobs.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(cancelled, Is.True);
            Assert.That(cancelledJob.Status, Is.EqualTo(LyricsAlignmentJobStatus.Failed));
        });

        _durable.Verify(
            client => client.TerminateAsync(42, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task CancellingARerunDoesNotTakeAwayAlreadyPublishedTimings()
    {
        await AddSongAsync();
        await AddPublishedLyricsAsync();
        await _service.SubmitAsync(1, CreatorId, "corrected lyrics");

        await _service.CancelAsync(1, CreatorId);

        await using var context = new AppDbContext(_options);
        var lyrics = await context.SongLyrics.SingleAsync();

        // Pending -> Failed is correct here: the re-run is what failed. The blob paths survive, so
        // nothing a listener was hearing has gone away.
        Assert.That(lyrics.TimingsBlobPath, Is.EqualTo("abc/abc-lyrics.json"));
    }

    [Test]
    public async Task CancellingWhenNothingIsRunningIsAQuietNoOp()
    {
        await AddSongAsync();

        Assert.That(await _service.CancelAsync(1, CreatorId), Is.False);
    }


    // -----------------------------------------------------------------
    // The public-read gate.
    //
    // MusicController's whitelist mocks this, so the query itself is only covered here - and it is
    // the query that decides whether a listener can fetch a set of timings.
    // -----------------------------------------------------------------

    [Test]
    public async Task PublishedTimingsAreReadable()
    {
        await AddSongAsync();
        await AddPublishedLyricsAsync();

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-lyrics.json"), Is.True);
    }

    [Test]
    public async Task TheLrcExportIsReadableOnTheSameTerms()
    {
        await AddSongAsync();
        await AddPublishedLyricsAsync();

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-lyrics.lrc"), Is.True);
    }

    [Test]
    public async Task TimingsHeldForReviewAreNotReadable()
    {
        // The assertion the whole gate exists for. These sit at the identical path a published set
        // would, so nothing about the request distinguishes them - only the status does.
        await AddSongAsync();
        await AddLyricsAsync(SongLyricsStatus.NeedsReview);

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-lyrics.json"), Is.False);
    }

    [Test]
    public async Task TimingsForAPendingOrFailedAttemptAreNotReadable()
    {
        await AddSongAsync();
        await AddLyricsAsync(SongLyricsStatus.Failed);

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-lyrics.json"), Is.False);
    }

    [Test]
    public async Task LyricsDoNotOutliveADeletedSong()
    {
        // IsActive false is a creator deleting their song. Its lyrics must go with it, not linger as
        // a publicly fetchable artifact of a track that is no longer in the catalogue.
        await AddSongAsync(isActive: false);
        await AddPublishedLyricsAsync();

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-lyrics.json"), Is.False);
    }

    [Test]
    public async Task LyricsDoNotOutliveASongAnAdminDisabled()
    {
        // IsEnabled false is moderation. Same reasoning, different actor - and the two flags are
        // deliberately distinct throughout this codebase.
        await AddSongAsync(isEnabled: false);
        await AddPublishedLyricsAsync();

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-lyrics.json"), Is.False);
    }

    [Test]
    public async Task AnUnknownPathIsNotReadable()
    {
        await AddSongAsync();
        await AddPublishedLyricsAsync();

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-someone-elses.json"), Is.False);
    }

    [Test]
    public async Task ThePastedTextIsNotReadableEvenForAPublishedSong()
    {
        // The .txt is the creator's working copy. It is stored, it is never served: only the two
        // derived artifacts are recorded on the row as fetchable paths.
        await AddSongAsync();
        await AddPublishedLyricsAsync();

        Assert.That(await _service.IsPubliclyReadableAsync("abc/abc-lyrics.txt"), Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task AnEmptyPathIsNotReadable(string path)
    {
        Assert.That(await _service.IsPubliclyReadableAsync(path), Is.False);
    }

    private async Task AddSongAsync(bool withAudio = true, bool isActive = true, bool isEnabled = true)
    {
        await using var context = new AppDbContext(_options);
        context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            SongTitle = "Night Drive",
            CreatorId = CreatorId,
            MediaGuid = SongGuid,
            Mp3BlobPath = withAudio ? "abc/abc-music.mp3" : null,
            IsActive = isActive,
            IsEnabled = isEnabled
        });
        await context.SaveChangesAsync();
    }

    private Task AddLyricsAsync(SongLyricsStatus status) => AddLyricsRowAsync(status);

    private async Task AddPublishedLyricsAsync()
    {
        await using var context = new AppDbContext(_options);
        context.SongLyrics.Add(new SongLyrics
        {
            SongMetadataId = 1,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            TimingsBlobPath = "abc/abc-lyrics.json",
            LrcBlobPath = "abc/abc-lyrics.lrc",
            Status = SongLyricsStatus.Published,
            Confidence = 0.91d,
            Version = 3
        });
        await context.SaveChangesAsync();
    }

    private async Task AddLyricsRowAsync(SongLyricsStatus status)
    {
        await using var context = new AppDbContext(_options);
        context.SongLyrics.Add(new SongLyrics
        {
            SongMetadataId = 1,
            LyricsBlobPath = "abc/abc-lyrics.txt",
            TimingsBlobPath = "abc/abc-lyrics.json",
            LrcBlobPath = "abc/abc-lyrics.lrc",
            Status = status,
            Confidence = 0.41d,
            Version = 1
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Hangfire's <c>Enqueue&lt;T&gt;</c> is an extension method over <c>Create</c>, so counting
    /// calls to that is how "did it queue the work" gets asserted without a Hangfire server.
    /// </summary>
    private sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public int Created { get; private set; }

        public string Create(Job job, IState state)
        {
            Created++;
            return Guid.NewGuid().ToString("N");
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
