using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The one-off pass that packages an existing catalogue, and the repair pass that puts it back after
/// a restore.
///
/// <para>
/// Unlike the image backfill this service does no work itself - it selects songs and queues
/// messages, and the Function does the rest - so what is worth testing is the selection, the
/// resumability that selection buys, and the lock.
/// </para>
/// </summary>
[TestFixture]
public class HlsPackagingBackfillServiceTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private RecordingQueueClient _queue = null!;
    private Mock<IBackgroundJobClient> _jobs = null!;
    private HlsPackagingBackfillService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"hls-backfill-{Guid.NewGuid():N}")
            .Options;

        _queue = new RecordingQueueClient();

        _jobs = new Mock<IBackgroundJobClient>();
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");

        _service = new HlsPackagingBackfillService(
            new TestFactory(_options),
            _queue,
            StubContainerFactory(),
            _jobs.Object,
            Mock.Of<ILogger<HlsPackagingBackfillService>>());
    }

    /// <summary>
    /// The container factory is only used for its name and for existence checks, neither of which a
    /// unit test can reach against real storage. Only the Missing scope is exercised here, which
    /// never asks storage anything.
    /// </summary>
    private static IBlobContainerFactory StubContainerFactory()
    {
        var factory = new Mock<IBlobContainerFactory>();
        factory.Setup(f => f.GetStreamingContainer())
            .Returns(new Azure.Storage.Blobs.BlobContainerClient(
                "UseDevelopmentStorage=true",
                "musicstreaming-test"));
        return factory.Object;
    }

    private async Task AddSongsAsync(params SongMetadata[] songs)
    {
        await using var context = new AppDbContext(_options);
        context.SongMetadata.AddRange(songs);
        await context.SaveChangesAsync();
    }

    private static SongMetadata Song(
        int id,
        Guid? hlsStreamId = null,
        string original = null,
        bool isActive = true,
        bool isEnabled = true) => new()
        {
            Id = id,
            SongTitle = $"Song {id}",
            Mp3BlobPath = $"folder{id}/song.mp3",
            OriginalAudioBlobPath = original,
            IsActive = isActive,
            IsEnabled = isEnabled,
            HlsStreamId = hlsStreamId
        };

    private async Task<HlsPackagingBackfillRun> RunAsync(HlsPackagingBackfillScope scope, bool dryRun = false)
    {
        var run = await _service.StartAsync(scope, dryRun, 1, "admin@example.com");
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        return await context.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == run.Id);
    }

    [Test]
    public async Task Missing_SelectsOnlySongsWithNoPackage()
    {
        await AddSongsAsync(
            Song(1),
            Song(2, hlsStreamId: Guid.NewGuid()),
            Song(3));

        var run = await RunAsync(HlsPackagingBackfillScope.Missing);

        Assert.That(run.TotalItemCount, Is.EqualTo(2));
        Assert.That(_queue.Requests.Select(r => r.SongMetadataId), Is.EquivalentTo(new[] { 1, 3 }));
    }

    /// <summary>
    /// Selecting on the package being absent is what makes a run resumable with no per-item state:
    /// re-running after an interruption picks up only what is still outstanding.
    /// </summary>
    [Test]
    public async Task Missing_ReRunAfterAnInterruption_PicksUpOnlyWhatIsStillOutstanding()
    {
        await AddSongsAsync(Song(1), Song(2), Song(3));

        var first = await RunAsync(HlsPackagingBackfillScope.Missing);

        // Two came back before the run was interrupted; the third never did. Its lock is released
        // here because the first run is over - a run still awaiting callbacks holds the lock and
        // would (correctly) refuse a second one.
        await using (var context = new AppDbContext(_options))
        {
            foreach (var id in new[] { 1, 2 })
            {
                var song = await context.SongMetadata.SingleAsync(s => s.Id == id);
                song.HlsStreamId = Guid.NewGuid();
            }

            var interrupted = await context.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == first.Id);
            interrupted.Status = HlsPackagingBackfillStatus.Cancelled;
            interrupted.ActiveLockKey = null;

            await context.SaveChangesAsync();
        }

        _queue.Requests.Clear();
        var second = await RunAsync(HlsPackagingBackfillScope.Missing);

        Assert.That(second.TotalItemCount, Is.EqualTo(1));
        Assert.That(_queue.Requests.Single().SongMetadataId, Is.EqualTo(3));
    }

    [Test]
    public async Task UnpublishedSongsAreNeverSelected()
    {
        await AddSongsAsync(
            Song(1, isActive: false),
            Song(2, isEnabled: false),
            Song(3));

        var run = await RunAsync(HlsPackagingBackfillScope.Missing);

        Assert.That(run.TotalItemCount, Is.EqualTo(1));
        Assert.That(_queue.Requests.Single().SongMetadataId, Is.EqualTo(3));
    }

    /// <summary>
    /// Packaging re-encodes to AAC, so going via the playback MP3 when a distinct original exists
    /// would cost a second generation of loss for nothing.
    /// </summary>
    [Test]
    public async Task TheRetainedOriginalIsPreferredWhenItIsADifferentBlob()
    {
        await AddSongsAsync(Song(1, original: "folder1/song-original.wav"));

        await RunAsync(HlsPackagingBackfillScope.Missing);

        Assert.That(_queue.Requests.Single().SourceBlobPath, Is.EqualTo("folder1/song-original.wav"));
    }

    [Test]
    public async Task ThePlaybackMp3IsUsedWhenTheOriginalIsTheSameBlob()
    {
        // What an MP3 upload looks like: SongMediaPaths.OriginalAudio returns the playback path, so
        // the two columns hold the same string by design and there is nothing to prefer.
        await AddSongsAsync(Song(1, original: "folder1/song.mp3"));

        await RunAsync(HlsPackagingBackfillScope.Missing);

        Assert.That(_queue.Requests.Single().SourceBlobPath, Is.EqualTo("folder1/song.mp3"));
    }

    /// <summary>
    /// Every run mints a fresh package folder rather than reusing the recorded one. Repackaging into
    /// the live folder would overwrite a working package in place, and a run that then failed halfway
    /// would leave the song pointing at a folder that is half old and half new.
    /// </summary>
    [Test]
    public async Task EachDispatchGetsAFreshStreamId()
    {
        await AddSongsAsync(Song(1), Song(2));

        await RunAsync(HlsPackagingBackfillScope.Missing);

        var ids = _queue.Requests.Select(r => r.HlsStreamId).ToList();

        Assert.That(ids, Has.Count.EqualTo(2));
        Assert.That(ids.Distinct().Count(), Is.EqualTo(2));
        Assert.That(ids, Has.None.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task ADryRunQueuesNothing()
    {
        await AddSongsAsync(Song(1), Song(2));

        var run = await RunAsync(HlsPackagingBackfillScope.Missing, dryRun: true);

        Assert.Multiple(() =>
        {
            Assert.That(run.TotalItemCount, Is.EqualTo(2));
            Assert.That(_queue.Requests, Is.Empty);
            Assert.That(run.Status, Is.EqualTo(HlsPackagingBackfillStatus.Completed));

            // The lock is released, so a real run can follow immediately.
            Assert.That(run.ActiveLockKey, Is.Null);
        });
    }

    [Test]
    public async Task EveryDispatchedMessageCarriesTheRunId()
    {
        await AddSongsAsync(Song(1), Song(2));

        var run = await RunAsync(HlsPackagingBackfillScope.Missing);

        // Without this the callbacks could not be attributed, and the run would wait forever for
        // results it had no way to recognise.
        Assert.That(_queue.Requests.Select(r => r.BackfillRunId), Has.All.EqualTo(run.Id));
    }

    [Test]
    public async Task AfterDispatch_TheRunAwaitsCallbacksRatherThanReportingItselfDone()
    {
        await AddSongsAsync(Song(1));

        var run = await RunAsync(HlsPackagingBackfillScope.Missing);

        // The Hangfire job has returned by now, but no song has actually been packaged - the work is
        // in Azure. Reporting Completed here would claim a catalogue was packaged when nothing was.
        Assert.That(run.Status, Is.EqualTo(HlsPackagingBackfillStatus.AwaitingCallbacks));
        Assert.That(run.ActiveLockKey, Is.EqualTo(1));
    }

    [Test]
    public async Task ASecondRunIsRejectedWhileOneIsActive()
    {
        await AddSongsAsync(Song(1));
        await _service.StartAsync(HlsPackagingBackfillScope.Missing, false, 1, "admin@example.com");

        // Two overlapping runs could mint different package folders for the same song, and whichever
        // callback landed second would sweep the folder the first had just recorded.
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartAsync(HlsPackagingBackfillScope.Missing, false, 1, "admin@example.com"));
    }

    [Test]
    public async Task CancellationStopsDispatchAndReleasesTheLock()
    {
        await AddSongsAsync(Song(1), Song(2), Song(3));

        var run = await _service.StartAsync(HlsPackagingBackfillScope.Missing, false, 1, "admin@example.com");
        await _service.RequestCancellationAsync(run.Id);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == run.Id);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(HlsPackagingBackfillStatus.Cancelled));
            Assert.That(stored.ActiveLockKey, Is.Null);
            Assert.That(_queue.Requests, Is.Empty);
        });
    }

    [Test]
    public void StartingWithNoQueueConfigured_IsRefusedRatherThanSilentlyDoingNothing()
    {
        var service = new HlsPackagingBackfillService(
            new TestFactory(_options),
            new RecordingQueueClient { Configured = false },
            StubContainerFactory(),
            _jobs.Object,
            Mock.Of<ILogger<HlsPackagingBackfillService>>());

        Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(HlsPackagingBackfillScope.Missing, false, 1, "admin@example.com"));
    }

    /// <summary>
    /// Cancelling a run that is only waiting for callbacks must actually end it.
    ///
    /// <para>
    /// By that point the Hangfire job has returned, so a cooperative flag has no reader: the run
    /// would sit in AwaitingCallbacks holding ActiveLockKey until every straggler called back. A
    /// dead-lettered message never calls back, so "until" can mean forever - and StartAsync refuses
    /// to start anything while the lock is held. Cancel appearing to work while quietly bricking
    /// every future run is the worst of both.
    /// </para>
    /// </summary>
    [Test]
    public async Task CancellingARunAwaitingCallbacks_ReleasesTheLockSoAnotherCanStart()
    {
        await using (var context = new AppDbContext(_options))
        {
            context.HlsPackagingBackfillRuns.Add(new HlsPackagingBackfillRun
            {
                Id = 700,
                Status = HlsPackagingBackfillStatus.AwaitingCallbacks,
                ActiveLockKey = 1,
                TotalItemCount = 10,
                DispatchedCount = 10,
                SucceededCount = 9
            });
            await context.SaveChangesAsync();
        }

        await _service.RequestCancellationAsync(700);

        await using (var verify = new AppDbContext(_options))
        {
            var run = await verify.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == 700);

            Assert.Multiple(() =>
            {
                Assert.That(run.Status, Is.EqualTo(HlsPackagingBackfillStatus.Cancelled));
                Assert.That(run.ActiveLockKey, Is.Null);
                Assert.That(run.CompletedAt, Is.Not.Null);
            });
        }

        // The point of releasing the lock: the next run is startable without a database edit.
        await AddSongsAsync(Song(1));
        Assert.DoesNotThrowAsync(() => _service.StartAsync(HlsPackagingBackfillScope.Missing, false, null, null));
    }

    /// <summary>
    /// A run still dispatching keeps the cooperative flag, because the job is there to read it.
    /// </summary>
    [Test]
    public async Task CancellingARunStillDispatching_OnlyFlagsIt()
    {
        await using (var context = new AppDbContext(_options))
        {
            context.HlsPackagingBackfillRuns.Add(new HlsPackagingBackfillRun
            {
                Id = 701,
                Status = HlsPackagingBackfillStatus.Dispatching,
                ActiveLockKey = 1
            });
            await context.SaveChangesAsync();
        }

        await _service.RequestCancellationAsync(701);

        await using var verify = new AppDbContext(_options);
        var run = await verify.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == 701);

        Assert.Multiple(() =>
        {
            Assert.That(run.CancellationRequestedAt, Is.Not.Null);
            Assert.That(run.Status, Is.EqualTo(HlsPackagingBackfillStatus.Dispatching));

            // Still held: the dispatch loop is what notices the flag and finishes the run properly,
            // and releasing it here would let a second run race the first one's callbacks.
            Assert.That(run.ActiveLockKey, Is.EqualTo(1));
        });
    }

    private sealed class RecordingQueueClient : IMediaProcessingQueueClient
    {
        public bool Configured { get; init; } = true;

        public List<AudioPackageRequest> Requests { get; } = new();

        public bool IsConfigured => Configured;

        public bool IsCoverArtMatchConfigured => Configured;

        public bool IsPackagingConfigured => Configured;

        public Task EnqueuePackageAsync(AudioPackageRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public Task EnqueueTranscodeAsync(AudioTranscodeRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueProbesAsync(IEnumerable<AudioProbeRequest> requests, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueCoverArtMatchAsync(CoverArtMatchRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// A run that predates the timing column must not report a confident zero.
    ///
    /// <para>
    /// Every run completed before <c>TotalProcessingSeconds</c> existed has it at 0. Averaging that
    /// would render "0.0s per song" in the grid and feed a projection built on nothing.
    /// </para>
    /// </summary>
    [Test]
    public void ARunWithNoRecordedTiming_ReportsNoAverageRatherThanZero()
    {
        var run = new HlsPackagingBackfillRun
        {
            SucceededCount = 10,
            TotalProcessingSeconds = 0,
            StartedAt = new DateTime(2026, 8, 27, 2, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 8, 27, 2, 5, 0, DateTimeKind.Utc)
        };

        Assert.Multiple(() =>
        {
            Assert.That(run.AverageProcessingSeconds, Is.Null);
            Assert.That(run.ObservedConcurrency, Is.Null);

            // Elapsed is still real and worth showing - only the derived figures are unknown.
            Assert.That(run.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(5)));
        });
    }

    /// <summary>
    /// Concurrency is total Function time over wall clock, which is the one figure that says how a
    /// small run extrapolates and that neither the queue nor the portal reports.
    /// </summary>
    [Test]
    public void ObservedConcurrency_IsTotalWorkOverWallClock()
    {
        var run = new HlsPackagingBackfillRun
        {
            SucceededCount = 10,

            // Ten songs at 60s each, finished in 5 minutes: four were running at any moment.
            TotalProcessingSeconds = 600,
            StartedAt = new DateTime(2026, 8, 27, 2, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 8, 27, 2, 2, 30, DateTimeKind.Utc)
        };

        Assert.Multiple(() =>
        {
            Assert.That(run.AverageProcessingSeconds, Is.EqualTo(60));
            Assert.That(run.ObservedConcurrency, Is.EqualTo(4).Within(0.001));
        });
    }

    [Test]
    public void ARunThatWentOneAtATime_ReportsConcurrencyOfOne()
    {
        var run = new HlsPackagingBackfillRun
        {
            SucceededCount = 10,
            TotalProcessingSeconds = 300,
            StartedAt = new DateTime(2026, 8, 27, 2, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 8, 27, 2, 5, 0, DateTimeKind.Utc)
        };

        // What scale-out ramp looks like on a short run, and the reason wall clock must not be
        // scaled up directly to estimate a large one.
        Assert.That(run.ObservedConcurrency, Is.EqualTo(1).Within(0.001));
    }

    [Test]
    public void AnUnfinishedRun_HasNoElapsedOrConcurrency()
    {
        var run = new HlsPackagingBackfillRun
        {
            SucceededCount = 3,
            TotalProcessingSeconds = 180,
            StartedAt = new DateTime(2026, 8, 27, 2, 0, 0, DateTimeKind.Utc)
        };

        Assert.Multiple(() =>
        {
            Assert.That(run.Elapsed, Is.Null);
            Assert.That(run.ObservedConcurrency, Is.Null);

            // The per-song figure is meaningful as soon as anything has succeeded, though.
            Assert.That(run.AverageProcessingSeconds, Is.EqualTo(60));
        });
    }
}
