using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Where a finished package becomes a playable song. The cases worth pinning are the ones where
/// getting it wrong takes a working song off the air rather than merely failing to add a new one.
/// </summary>
[TestFixture]
public class HlsPackagingResultHandlerTests
{
    private const int SongId = 501;

    private DbContextOptions<AppDbContext> _options = null!;
    private RecordingSweeper _sweeper = null!;
    private HlsContentKeyProtector _protector = null!;
    private HlsPackagingResultHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"hls-package-{Guid.NewGuid():N}")
            .Options;

        _sweeper = new RecordingSweeper();

        _protector = new HlsContentKeyProtector(
            Options.Create(new HlsOptions
            {
                ContentKeyWrappingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }),
            Mock.Of<ILogger<HlsContentKeyProtector>>());

        _handler = new HlsPackagingResultHandler(
            new TestFactory(_options),
            _protector,
            _sweeper,
            Mock.Of<ILogger<HlsPackagingResultHandler>>());
    }

    private async Task<SongMetadata> GivenSongAsync(Guid? existingStreamId = null, string existingKey = null)
    {
        await using var context = new AppDbContext(_options);
        var song = new SongMetadata
        {
            Id = SongId,
            SongTitle = "Night Drive",
            Mp3BlobPath = "folder/song.mp3",
            IsActive = true,
            IsEnabled = true,
            HlsStreamId = existingStreamId,
            HlsKeyProtected = existingKey
        };
        context.SongMetadata.Add(song);
        await context.SaveChangesAsync();
        return song;
    }

    private static AudioPackageResult Success(Guid streamId, int? runId = null) => new()
    {
        SongMetadataId = SongId,
        HlsStreamId = streamId,
        BackfillRunId = runId,
        KeyHex = Convert.ToHexString(HlsContentKeyProtector.CreateContentKey()).ToLowerInvariant(),
        IvHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        SegmentCount = 40,
        TargetDurationSeconds = 6,
        DurationSeconds = 237.5,
        Outcome = AudioProcessingOutcome.Playable
    };

    private async Task<SongMetadata> ReadSongAsync()
    {
        await using var context = new AppDbContext(_options);
        return await context.SongMetadata.SingleAsync(s => s.Id == SongId);
    }

    [Test]
    public async Task ASuccessfulPackage_IsRecordedAndItsKeyIsStoredWrapped()
    {
        await GivenSongAsync();
        var streamId = Guid.NewGuid();
        var result = Success(streamId);

        await _handler.HandleAsync(result);

        var song = await ReadSongAsync();

        Assert.Multiple(() =>
        {
            Assert.That(song.HlsStreamId, Is.EqualTo(streamId));
            Assert.That(song.HlsSegmentCount, Is.EqualTo(40));
            Assert.That(song.HlsIv, Is.EqualTo(result.IvHex));
            Assert.That(song.HlsPackagedAt, Is.Not.Null);

            // Never the raw key. Storing it in the clear would put the whole catalogue one database
            // read away from being decryptable.
            Assert.That(song.HlsKeyProtected, Is.Not.EqualTo(result.KeyHex));
            Assert.That(song.HlsKeyProtected, Does.StartWith("v1."));
        });

        var recovered = _protector.Unprotect(SongId, song.HlsKeyProtected);
        Assert.That(Convert.ToHexString(recovered).ToLowerInvariant(), Is.EqualTo(result.KeyHex));
    }

    /// <summary>
    /// A repackage supersedes the old folder, and the old one is swept - it is unreachable the moment
    /// the row points elsewhere, and the streaming container has no lifecycle rule to collect it.
    /// </summary>
    [Test]
    public async Task ARepackage_SweepsTheSupersededFolder()
    {
        var oldStreamId = Guid.NewGuid();
        await GivenSongAsync(oldStreamId, "v1.whatever");

        var newStreamId = Guid.NewGuid();
        await _handler.HandleAsync(Success(newStreamId));

        Assert.That((await ReadSongAsync()).HlsStreamId, Is.EqualTo(newStreamId));
        Assert.That(_sweeper.Swept, Is.EqualTo(new[] { oldStreamId }));
    }

    /// <summary>
    /// A redelivered callback for the package the song already has must change nothing.
    ///
    /// <para>
    /// Without this the retry would sweep the very folder the row points at, taking a working song
    /// off the air - and the queue redelivers on any non-2xx, so this is an ordinary event rather
    /// than an exotic one.
    /// </para>
    /// </summary>
    [Test]
    public async Task ARedeliveredCallbackForTheCurrentPackage_ChangesNothingAndSweepsNothing()
    {
        var streamId = Guid.NewGuid();
        await GivenSongAsync();

        await _handler.HandleAsync(Success(streamId));
        var afterFirst = await ReadSongAsync();

        await _handler.HandleAsync(Success(streamId));
        var afterSecond = await ReadSongAsync();

        Assert.Multiple(() =>
        {
            Assert.That(afterSecond.HlsStreamId, Is.EqualTo(streamId));
            Assert.That(afterSecond.HlsKeyProtected, Is.EqualTo(afterFirst.HlsKeyProtected));
            Assert.That(_sweeper.Swept, Is.Empty);
        });
    }

    /// <summary>
    /// A failed repackage must leave the song exactly as it was. Clearing the columns would turn a
    /// failed optional improvement into an outage for a song that was playing perfectly well.
    /// </summary>
    [Test]
    public async Task AFailedPackage_LeavesAnExistingPackageIntact()
    {
        var existingStreamId = Guid.NewGuid();
        var existingKey = _protector.Protect(SongId, HlsContentKeyProtector.CreateContentKey());
        await GivenSongAsync(existingStreamId, existingKey);

        var attemptedStreamId = Guid.NewGuid();
        await _handler.HandleAsync(new AudioPackageResult
        {
            SongMetadataId = SongId,
            HlsStreamId = attemptedStreamId,
            Outcome = AudioProcessingOutcome.Unplayable,
            FailureCode = "PackagingFailed",
            Diagnostic = "ffmpeg said no"
        });

        var song = await ReadSongAsync();

        Assert.Multiple(() =>
        {
            Assert.That(song.HlsStreamId, Is.EqualTo(existingStreamId));
            Assert.That(song.HlsKeyProtected, Is.EqualTo(existingKey));

            // The half-written folder from the failed attempt is cleaned up, not the live one.
            Assert.That(_sweeper.Swept, Is.EqualTo(new[] { attemptedStreamId }));
        });
    }

    [Test]
    public async Task ASuccessWithNoKeyMaterial_IsRefusedRatherThanRecorded()
    {
        await GivenSongAsync();
        var streamId = Guid.NewGuid();

        await _handler.HandleAsync(new AudioPackageResult
        {
            SongMetadataId = SongId,
            HlsStreamId = streamId,
            Outcome = AudioProcessingOutcome.Playable,
            SegmentCount = 40
        });

        var song = await ReadSongAsync();

        // Recording a package whose key nothing knows would make the song permanently unplayable
        // while looking, in the database, exactly like a healthy one.
        Assert.That(song.HlsStreamId, Is.Null);
        Assert.That(_sweeper.Swept, Is.EqualTo(new[] { streamId }));
    }

    [Test]
    public async Task APackageForADeletedSong_IsSweptRatherThanLeftInPublicStorage()
    {
        var streamId = Guid.NewGuid();

        // No song row at all - deleted while its packaging was in flight.
        await _handler.HandleAsync(Success(streamId));

        Assert.That(_sweeper.Swept, Is.EqualTo(new[] { streamId }));
    }

    [Test]
    public async Task ARunIsCompletedByItsLastCallback()
    {
        await GivenSongAsync();

        int runId;
        await using (var context = new AppDbContext(_options))
        {
            var run = new HlsPackagingBackfillRun
            {
                Scope = HlsPackagingBackfillScope.Missing,
                Status = HlsPackagingBackfillStatus.AwaitingCallbacks,
                TotalItemCount = 1,
                DispatchedCount = 1,
                ActiveLockKey = 1
            };
            context.HlsPackagingBackfillRuns.Add(run);
            await context.SaveChangesAsync();
            runId = run.Id;
        }

        await _handler.HandleAsync(Success(Guid.NewGuid(), runId));

        await using var verify = new AppDbContext(_options);
        var finished = await verify.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == runId);

        Assert.Multiple(() =>
        {
            Assert.That(finished.SucceededCount, Is.EqualTo(1));
            Assert.That(finished.Status, Is.EqualTo(HlsPackagingBackfillStatus.Completed));

            // Releasing the lock is what lets the next run start. A run that finished without
            // releasing it would block every future run with nothing visibly wrong.
            Assert.That(finished.ActiveLockKey, Is.Null);
        });
    }

    /// <summary>
    /// A callback arriving while the Hangfire job is still dispatching must not complete the run.
    /// Outstanding reaching zero there only means the callbacks are keeping up with the queueing.
    /// </summary>
    [Test]
    public async Task ACallbackDuringDispatch_DoesNotCompleteTheRun()
    {
        await GivenSongAsync();

        int runId;
        await using (var context = new AppDbContext(_options))
        {
            var run = new HlsPackagingBackfillRun
            {
                Scope = HlsPackagingBackfillScope.Missing,
                Status = HlsPackagingBackfillStatus.Dispatching,
                TotalItemCount = 500,
                DispatchedCount = 1,
                ActiveLockKey = 1
            };
            context.HlsPackagingBackfillRuns.Add(run);
            await context.SaveChangesAsync();
            runId = run.Id;
        }

        await _handler.HandleAsync(Success(Guid.NewGuid(), runId));

        await using var verify = new AppDbContext(_options);
        var run2 = await verify.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == runId);

        Assert.That(run2.Status, Is.EqualTo(HlsPackagingBackfillStatus.Dispatching));
        Assert.That(run2.ActiveLockKey, Is.EqualTo(1));
    }

    [Test]
    public async Task FailureDetailIsCappedButTheCountIsNot()
    {
        await GivenSongAsync();

        int runId;
        await using (var context = new AppDbContext(_options))
        {
            var run = new HlsPackagingBackfillRun
            {
                Status = HlsPackagingBackfillStatus.Dispatching,
                TotalItemCount = 10_000,
                DispatchedCount = 10_000,
                ActiveLockKey = 1,
                FailedCount = HlsPackagingBackfillRun.MaxRecordedFailures
            };

            for (var i = 0; i < HlsPackagingBackfillRun.MaxRecordedFailures; i++)
            {
                run.Failures.Add(new HlsPackagingBackfillFailure { SongMetadataId = i + 1 });
            }

            context.HlsPackagingBackfillRuns.Add(run);
            await context.SaveChangesAsync();
            runId = run.Id;
        }

        await _handler.HandleAsync(new AudioPackageResult
        {
            SongMetadataId = SongId,
            HlsStreamId = Guid.NewGuid(),
            BackfillRunId = runId,
            Outcome = AudioProcessingOutcome.Unplayable,
            FailureCode = "PackagingFailed"
        });

        await using var verify = new AppDbContext(_options);
        var run3 = await verify.HlsPackagingBackfillRuns
            .Include(r => r.Failures)
            .SingleAsync(r => r.Id == runId);

        Assert.Multiple(() =>
        {
            // A systemic failure would otherwise write one row per song in the catalogue, turning a
            // diagnostic aid into a second incident.
            Assert.That(run3.Failures, Has.Count.EqualTo(HlsPackagingBackfillRun.MaxRecordedFailures));

            // The counter stays exact regardless, so the run still reports the truth.
            Assert.That(run3.FailedCount, Is.EqualTo(HlsPackagingBackfillRun.MaxRecordedFailures + 1));
        });
    }

    private sealed class RecordingSweeper : IHlsPackageSweeper
    {
        public List<Guid> Swept { get; } = new();

        public Task SweepAsync(Guid hlsStreamId, CancellationToken cancellationToken = default)
        {
            Swept.Add(hlsStreamId);
            return Task.CompletedTask;
        }
    }

    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);
    }
}
