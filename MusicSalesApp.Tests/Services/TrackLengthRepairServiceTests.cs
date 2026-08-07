using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The nightly track-length repair, split in two by the move to Azure Functions: this job now only
/// picks the candidates and queues a probe for each, and <see cref="AudioProbeResultHandler"/>
/// writes the duration when the Function reports back.
///
/// <para>
/// The candidate query is unchanged and still carries the interesting rules - which songs qualify,
/// and the legacy <c>BlobPath</c> fallback for rows that predate <c>Mp3BlobPath</c> - so it is
/// tested through the dispatch, and the write side is tested through the handler.
/// </para>
/// </summary>
[TestFixture]
public class TrackLengthRepairServiceTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestDbContextFactory _factory = null!;
    private RecordingQueueClient _queue = null!;
    private Mock<IAzureStorageService> _storage = null!;
    private TrackLengthRepairService _service = null!;
    private AudioProbeResultHandler _handler = null!;
    private AppDbContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TrackLengthRepairTests_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(_options);
        _factory = new TestDbContextFactory(_options);
        _queue = new RecordingQueueClient();
        _storage = new Mock<IAzureStorageService>();

        _service = new TrackLengthRepairService(
            _factory,
            _queue,
            Mock.Of<ILogger<TrackLengthRepairService>>());

        _handler = new AudioProbeResultHandler(
            _factory,
            _storage.Object,
            _queue,
            Mock.Of<IMediaIntegrityAuditService>(),
            Mock.Of<ILogger<AudioProbeResultHandler>>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task Dispatch_WithoutAConfiguredQueue_SkipsQuietly()
    {
        // A nightly Hangfire job on a best-effort repair. On an environment with no media-processing
        // storage this would otherwise throw every single night, filling the failed-jobs list with
        // an error nobody can act on.
        AddSong(id: 1, mp3BlobPath: "songs/test-song.mp3", trackLength: null);
        await _context.SaveChangesAsync();
        _queue.IsConfigured = false;

        var dispatched = await _service.RepairMissingTrackLengthsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(dispatched, Is.Zero);
            Assert.That(_queue.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task Dispatch_QueuesOneProbePerActivePlayableSongWithNoLength()
    {
        AddSong(id: 1, mp3BlobPath: "songs/test-song.mp3", trackLength: null);
        await _context.SaveChangesAsync();

        var dispatched = await _service.RepairMissingTrackLengthsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(dispatched, Is.EqualTo(1));
            Assert.That(_queue.Probes, Has.Count.EqualTo(1));
            Assert.That(_queue.Probes[0].Kind, Is.EqualTo(AudioProbeKind.TrackLengthRepair));
            Assert.That(_queue.Probes[0].SongMetadataId, Is.EqualTo(1));
            Assert.That(_queue.Probes[0].BlobPath, Is.EqualTo("songs/test-song.mp3"));
        });
    }

    [Test]
    public async Task Dispatch_FallsBackToTheLegacyBlobPathForRowsWithoutAnMp3Path()
    {
        // Songs uploaded before Mp3BlobPath existed only have BlobPath, and it is the only way to
        // find their audio.
        AddSong(id: 1, mp3BlobPath: null, trackLength: null, blobPath: "legacy/song.mp3");
        await _context.SaveChangesAsync();

        await _service.RepairMissingTrackLengthsAsync();

        Assert.That(_queue.Probes.Single().BlobPath, Is.EqualTo("legacy/song.mp3"));
    }

    [Test]
    public async Task Dispatch_IgnoresALegacyBlobPathThatIsNotAnMp3()
    {
        // A cover-art row would otherwise be queued for audio decoding.
        AddSong(id: 1, mp3BlobPath: null, trackLength: null, blobPath: "legacy/cover.png");
        await _context.SaveChangesAsync();

        var dispatched = await _service.RepairMissingTrackLengthsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(dispatched, Is.Zero);
            Assert.That(_queue.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task Dispatch_SkipsInactiveAlbumCoverAndAlreadySizedSongs()
    {
        AddSong(id: 1, mp3BlobPath: "songs/a.mp3", trackLength: 200);
        AddSong(id: 2, mp3BlobPath: "songs/b.mp3", trackLength: null, isActive: false);
        AddSong(id: 3, mp3BlobPath: "songs/c.mp3", trackLength: null, isAlbumCover: true);
        AddSong(id: 4, mp3BlobPath: null, trackLength: null, blobPath: null);
        await _context.SaveChangesAsync();

        var dispatched = await _service.RepairMissingTrackLengthsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(dispatched, Is.Zero);
            Assert.That(_queue.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task Dispatch_IncludesDisabledButActiveSongs()
    {
        // A quarantined song is exactly the one whose metadata most needs repairing.
        AddSong(id: 1, mp3BlobPath: "songs/a.mp3", trackLength: null, isEnabled: false);
        await _context.SaveChangesAsync();

        Assert.That(await _service.RepairMissingTrackLengthsAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Dispatch_WithNoCandidates_QueuesNothing()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await _service.RepairMissingTrackLengthsAsync(), Is.Zero);
            Assert.That(_queue.Probes, Is.Empty);
        });
    }

    [Test]
    public async Task Result_WritesTheDurationAndBackfillsTheMp3Path()
    {
        AddSong(id: 1, mp3BlobPath: null, trackLength: null, blobPath: "legacy/song.mp3");
        await _context.SaveChangesAsync();

        await _handler.HandleAsync(PlayableProbe(songId: 1, blobPath: "legacy/song.mp3", duration: 212.5));

        await using var verify = new AppDbContext(_options);
        var song = await verify.SongMetadata.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(song.TrackLength, Is.EqualTo(212.5));
            Assert.That(song.Mp3BlobPath, Is.EqualTo("legacy/song.mp3"));
        });
    }

    [Test]
    public async Task Result_ThatIsNotPlayable_LeavesTheSongAlone()
    {
        AddSong(id: 1, mp3BlobPath: "songs/a.mp3", trackLength: null);
        await _context.SaveChangesAsync();

        await _handler.HandleAsync(new AudioProbeResult
        {
            ProbeId = Guid.NewGuid(),
            Kind = AudioProbeKind.TrackLengthRepair,
            SongMetadataId = 1,
            BlobPath = "songs/a.mp3",
            BlobExists = true,
            Outcome = AudioProcessingOutcome.Unplayable,
            FailureCode = "DecoderRejected"
        });

        await using var verify = new AppDbContext(_options);
        Assert.That((await verify.SongMetadata.SingleAsync()).TrackLength, Is.Null);
    }

    [Test]
    public async Task Result_ThatArrivesAfterSomethingElsePopulatedTheLength_DoesNotOverwriteIt()
    {
        // A re-upload or an audit repair can land between dispatch and the probe coming back, and
        // that value is the more recent truth.
        AddSong(id: 1, mp3BlobPath: "songs/a.mp3", trackLength: 300);
        await _context.SaveChangesAsync();

        await _handler.HandleAsync(PlayableProbe(songId: 1, blobPath: "songs/a.mp3", duration: 10));

        await using var verify = new AppDbContext(_options);
        Assert.That((await verify.SongMetadata.SingleAsync()).TrackLength, Is.EqualTo(300));
    }

    [Test]
    public async Task Result_ForASongThatWasDeleted_IsIgnored()
    {
        Assert.DoesNotThrowAsync(() =>
            _handler.HandleAsync(PlayableProbe(songId: 999, blobPath: "songs/gone.mp3", duration: 10)));

        await using var verify = new AppDbContext(_options);
        Assert.That(verify.SongMetadata.Count(), Is.Zero);
    }

    private static AudioProbeResult PlayableProbe(int songId, string blobPath, double duration)
        => new()
        {
            ProbeId = Guid.NewGuid(),
            Kind = AudioProbeKind.TrackLengthRepair,
            SongMetadataId = songId,
            BlobPath = blobPath,
            BlobExists = true,
            DurationSeconds = duration,
            Outcome = AudioProcessingOutcome.Playable
        };

    private void AddSong(
        int id,
        string mp3BlobPath,
        double? trackLength,
        string blobPath = null,
        bool isActive = true,
        bool isEnabled = true,
        bool isAlbumCover = false)
    {
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = id,
            BlobPath = blobPath ?? mp3BlobPath ?? string.Empty,
            Mp3BlobPath = mp3BlobPath,
            TrackLength = trackLength,
            IsActive = isActive,
            IsEnabled = isEnabled,
            IsAlbumCover = isAlbumCover,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }

    private sealed class RecordingQueueClient : IMediaProcessingQueueClient
    {
        public List<AudioProbeRequest> Probes { get; } = [];

        public bool IsConfigured { get; set; } = true;
        public bool IsCoverArtMatchConfigured => true;

        public Task EnqueueTranscodeAsync(AudioTranscodeRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueCoverArtMatchAsync(CoverArtMatchRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueProbesAsync(IEnumerable<AudioProbeRequest> requests, CancellationToken cancellationToken = default)
        {
            Probes.AddRange(requests);
            return Task.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
