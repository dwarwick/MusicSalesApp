using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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

    private SqliteConnection _connection = null!;
    private List<string> _sql = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private RecordingSweeper _sweeper = null!;
    private HlsContentKeyProtector _protector = null!;
    private HlsPackagingResultHandler _handler = null!;

    /// <summary>
    /// Sqlite rather than the InMemory provider every other handler test uses, for two reasons that
    /// both matter here.
    ///
    /// <para>
    /// The handler increments the run counters with <c>ExecuteUpdateAsync</c> so that concurrent
    /// callbacks cannot lose one, and InMemory does not implement it at all. More to the point,
    /// InMemory could not demonstrate the property even if it did: it has no real transactions, so
    /// the lost update this guards against is not expressible there. Same reasoning as
    /// <c>SongLikeServiceSetStateConcurrencyTests</c>.
    /// </para>
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _sql = new List<string>();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .LogTo(line => _sql.Add(line), LogLevel.Information)
            .Options;

        using (var schema = new AppDbContext(_options))
        {
            schema.Database.EnsureCreated();
        }

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

    /// <summary>
    /// A failure callback must not delete the package the song is currently served from.
    ///
    /// <para>
    /// Queue delivery is at-least-once. A message whose first attempt succeeded and was recorded can
    /// be redelivered, and if that second attempt fails, the failure names the very stream id the row
    /// now points at. Sweeping it unguarded takes a healthy song off the air - the manifest endpoint
    /// starts answering 503 - and nothing in the database looks wrong afterwards, which is what makes
    /// it expensive to diagnose.
    /// </para>
    /// </summary>
    [Test]
    public async Task ARedeliveredFailure_DoesNotSweepThePackageTheSongIsServedFrom()
    {
        var liveStreamId = Guid.NewGuid();
        await GivenSongAsync(liveStreamId, existingKey: "v1.already-wrapped");

        await _handler.HandleAsync(new AudioPackageResult
        {
            SongMetadataId = SongId,
            HlsStreamId = liveStreamId,
            Outcome = AudioProcessingOutcome.Unplayable,
            FailureCode = "PackagingFailed",
            Diagnostic = "the retry could not decode the source"
        });

        Assert.Multiple(async () =>
        {
            Assert.That(_sweeper.Swept, Does.Not.Contain(liveStreamId));

            var song = await ReadSongAsync();
            Assert.That(song.HlsStreamId, Is.EqualTo(liveStreamId), "the song keeps its working package");
        });
    }

    /// <summary>
    /// A failed attempt at a NEW package still sweeps that attempt's own folder.
    ///
    /// <para>
    /// The guard above must not turn into "never sweep": a failed repackage leaves a partial folder
    /// behind, and nothing else would ever remove it.
    /// </para>
    /// </summary>
    [Test]
    public async Task AFailedRepackage_StillSweepsItsOwnAbandonedFolder()
    {
        var liveStreamId = Guid.NewGuid();
        var attemptedStreamId = Guid.NewGuid();
        await GivenSongAsync(liveStreamId, existingKey: "v1.already-wrapped");

        await _handler.HandleAsync(new AudioPackageResult
        {
            SongMetadataId = SongId,
            HlsStreamId = attemptedStreamId,
            Outcome = AudioProcessingOutcome.Unplayable,
            FailureCode = "PackagingFailed",
            Diagnostic = "bad source"
        });

        Assert.Multiple(() =>
        {
            Assert.That(_sweeper.Swept, Does.Contain(attemptedStreamId));
            Assert.That(_sweeper.Swept, Does.Not.Contain(liveStreamId));
        });
    }

    /// <summary>
    /// Many callbacks against one run must add up, and the run must finish and release its lock.
    ///
    /// <para>
    /// This pins the OUTCOME the atomic increment protects, not the race itself: Sqlite serialises
    /// everything through the one connection these tests share, so the interleaving that loses an
    /// update cannot be staged here - this test passes against the read-modify-write it replaced.
    /// <see cref="TheRunCounterIsIncrementedByTheDatabaseRatherThanReadModifyWritten"/> is the one
    /// that actually discriminates.
    /// </para>
    ///
    /// <para>
    /// Up to <c>MaxInFlightMessages</c> songs are packaged at once and every one calls back
    /// independently. Read-modify-writing the counters loses an increment whenever two land together,
    /// and because a run completes only when
    /// <c>DispatchedCount - SucceededCount - FailedCount</c> reaches zero, one lost increment means it
    /// never completes: the run holds <c>ActiveLockKey</c> indefinitely, and <c>StartAsync</c> refuses
    /// every future run while any run holds it. A miscount here does not degrade the feature, it
    /// disables the feature permanently.
    /// </para>
    /// </summary>
    [Test]
    public async Task ConcurrentCallbacks_EachCountOnce_SoTheRunCanComplete()
    {
        const int callbacks = 8;

        var songIds = new List<int>();
        await using (var context = new AppDbContext(_options))
        {
            context.HlsPackagingBackfillRuns.Add(new HlsPackagingBackfillRun
            {
                Id = 900,
                Status = HlsPackagingBackfillStatus.AwaitingCallbacks,
                ActiveLockKey = 1,
                TotalItemCount = callbacks,
                DispatchedCount = callbacks
            });

            for (var i = 0; i < callbacks; i++)
            {
                var song = new SongMetadata
                {
                    SongTitle = $"Song {i}",
                    Mp3BlobPath = $"folder/song-{i}.mp3",
                    IsActive = true,
                    IsEnabled = true
                };
                context.SongMetadata.Add(song);
                await context.SaveChangesAsync();
                songIds.Add(song.Id);
            }

            await context.SaveChangesAsync();
        }

        // Fired together rather than in sequence: the lost update only appears when two callbacks
        // read the same row before either has written it back.
        await Task.WhenAll(songIds.Select(id => _handler.HandleAsync(new AudioPackageResult
        {
            SongMetadataId = id,
            HlsStreamId = Guid.NewGuid(),
            BackfillRunId = 900,
            KeyHex = Convert.ToHexString(HlsContentKeyProtector.CreateContentKey()).ToLowerInvariant(),
            IvHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            SegmentCount = 10,
            TargetDurationSeconds = 6,
            ProcessingSeconds = 12,
            Outcome = AudioProcessingOutcome.Playable
        })));

        await using var verify = new AppDbContext(_options);
        var run = await verify.HlsPackagingBackfillRuns.SingleAsync(r => r.Id == 900);

        Assert.Multiple(() =>
        {
            Assert.That(run.SucceededCount, Is.EqualTo(callbacks), "an increment was lost");
            Assert.That(run.OutstandingCount, Is.Zero);
            Assert.That(run.Status, Is.EqualTo(HlsPackagingBackfillStatus.Completed));
            Assert.That(run.ActiveLockKey, Is.Null, "a stranded lock blocks every future run");
        });
    }

    /// <summary>
    /// The counter must be incremented relative to its own column, in the database.
    ///
    /// <para>
    /// This is the assertion the outcome test above cannot make. Up to <c>MaxInFlightMessages</c>
    /// songs are packaged at once and each calls back independently, so two callbacks routinely load
    /// the same run row, each add one to the value they read, and one increment is lost. Because a
    /// run completes only when <c>DispatchedCount - SucceededCount - FailedCount</c> reaches zero,
    /// one lost increment means it never completes: it holds <c>ActiveLockKey</c> indefinitely and
    /// <c>StartAsync</c> refuses every future run while any run holds it. A miscount here does not
    /// degrade the feature, it disables it permanently and needs a database edit to recover.
    /// </para>
    ///
    /// <para>
    /// So the SQL is inspected directly. An atomic increment names the column on both sides
    /// (<c>SET "SucceededCount" = "SucceededCount" + 1</c>); a read-modify-write assigns a
    /// parameter computed from a value read moments earlier - the same value the other callback read.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheRunCounterIsIncrementedByTheDatabaseRatherThanReadModifyWritten()
    {
        await GivenSongAsync();

        await using (var context = new AppDbContext(_options))
        {
            context.HlsPackagingBackfillRuns.Add(new HlsPackagingBackfillRun
            {
                Id = 901,
                Status = HlsPackagingBackfillStatus.AwaitingCallbacks,
                ActiveLockKey = 1,
                TotalItemCount = 2,
                DispatchedCount = 2
            });
            await context.SaveChangesAsync();
        }

        _sql.Clear();
        await _handler.HandleAsync(Success(Guid.NewGuid(), runId: 901));

        var update = _sql.FirstOrDefault(sql =>
            sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("SucceededCount", StringComparison.Ordinal));

        Assert.That(update, Is.Not.Null, "the run counter was never updated at all");

        var mentions = Regex.Matches(update!, "SucceededCount").Count;

        Assert.That(
            mentions,
            Is.GreaterThan(1),
            "the counter was assigned a value computed in memory rather than incremented in the "
            + "database, so two concurrent callbacks would lose one of the increments and the run "
            + "would never complete. SQL was: " + update);
    }

    [TearDown]
    public void TearDown() => _connection?.Dispose();

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
