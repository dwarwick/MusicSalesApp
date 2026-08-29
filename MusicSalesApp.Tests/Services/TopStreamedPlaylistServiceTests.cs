using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

// This project does not enable nullable reference types project-wide.
#nullable enable

/// <summary>
/// Covers the five global "most streamed" playlists.
///
/// <para>
/// The clock is driven by hand throughout. These playlists rank on a window measured back from the
/// moment the job runs, so asserting anything about a window boundary against the wall clock would
/// be either flaky or untestable.
/// </para>
/// </summary>
[TestFixture]
public class TopStreamedPlaylistServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 2, 0, 0, TimeSpan.Zero);

    private DbContextOptions<AppDbContext> _dbContextOptions = null!;
    private Mock<ILogger<TopStreamedPlaylistService>> _mockLogger = null!;

    [SetUp]
    public void Setup()
    {
        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _mockLogger = new Mock<ILogger<TopStreamedPlaylistService>>();
    }

    private IDbContextFactory<AppDbContext> CreateDbContextFactory()
    {
        var mockFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_dbContextOptions));
        return mockFactory.Object;
    }

    private TopStreamedPlaylistService CreateService(DateTimeOffset? utcNow = null) =>
        new(CreateDbContextFactory(), new TestTimeProvider(utcNow ?? Now), _mockLogger.Object);

    private static SongMetadata Song(int id, int lifetimeStreams = 0, bool isActive = true, bool isEnabled = true, int? creatorId = null) =>
        new()
        {
            Id = id,
            SongTitle = $"Song {id}",
            Mp3BlobPath = $"{id}/{id}-music.mp3",
            NumberOfStreams = lifetimeStreams,
            IsActive = isActive,
            IsEnabled = isEnabled,
            CreatorId = creatorId
        };

    /// <summary>Writes <paramref name="count"/> stream events for a song, all at <paramref name="at"/>.</summary>
    private static IEnumerable<SongStream> Streams(int songId, int count, DateTime at) =>
        Enumerable.Range(0, count).Select(_ => new SongStream
        {
            SongMetadataId = songId,
            CreatedDate = at
        });

    private async Task SeedAsync(IEnumerable<SongMetadata>? songs = null, IEnumerable<SongStream>? streams = null, IEnumerable<Creator>? creators = null)
    {
        await using var context = new AppDbContext(_dbContextOptions);
        if (creators != null) context.Creators.AddRange(creators);
        if (songs != null) context.SongMetadata.AddRange(songs);
        if (streams != null) context.SongStreams.AddRange(streams);
        await context.SaveChangesAsync();
    }

    private async Task<List<TopStreamedPlaylistEntry>> ReadAsync(string window)
    {
        await using var context = new AppDbContext(_dbContextOptions);
        return await context.TopStreamedPlaylistEntries
            .Where(entry => entry.Window == window)
            .OrderBy(entry => entry.DisplayOrder)
            .ToListAsync();
    }

    // ---- Ranking -----------------------------------------------------------------

    [Test]
    public async Task RollingWindow_RanksMostStreamedFirst()
    {
        var recent = Now.UtcDateTime.AddHours(-2);
        await SeedAsync(
            songs: [Song(1), Song(2), Song(3)],
            streams: Streams(1, 3, recent).Concat(Streams(2, 9, recent)).Concat(Streams(3, 6, recent)));

        await CreateService().GenerateAllAsync();

        var entries = await ReadAsync(TopStreamedWindows.Day);
        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(e => e.SongMetadataId), Is.EqualTo(new[] { 2, 3, 1 }),
                "Rank 1 must be the most streamed song in the window.");
            Assert.That(entries.Select(e => e.StreamCount), Is.EqualTo(new[] { 9, 6, 3 }),
                "The stored count is what the row was ranked on, and must descend.");
            Assert.That(entries.Select(e => e.DisplayOrder), Is.EqualTo(new[] { 1, 2, 3 }));
        });
    }

    [Test]
    public async Task RollingWindow_ExcludesStreamsOlderThanTheCutoff()
    {
        // Song 1 was huge yesterday, song 2 is modest but current. "Today" must prefer song 2.
        await SeedAsync(
            songs: [Song(1), Song(2)],
            streams: Streams(1, 50, Now.UtcDateTime.AddHours(-30))
                .Concat(Streams(2, 4, Now.UtcDateTime.AddHours(-1))));

        await CreateService().GenerateAllAsync();

        var day = await ReadAsync(TopStreamedWindows.Day);
        var week = await ReadAsync(TopStreamedWindows.Week);

        Assert.Multiple(() =>
        {
            Assert.That(day.Select(e => e.SongMetadataId), Is.EqualTo(new[] { 2 }),
                "A stream 30 hours ago is outside the 24-hour window.");
            Assert.That(week.Select(e => e.SongMetadataId), Is.EqualTo(new[] { 1, 2 }),
                "The same stream is inside the 7-day window.");
        });
    }

    [Test]
    public async Task RollingWindow_IncludesAStreamExactlyOnTheCutoff()
    {
        // The filter is >= cutoff, so the boundary stream counts. Pinned because flipping it to > is
        // an easy and silent change.
        await SeedAsync(
            songs: [Song(1)],
            streams: Streams(1, 1, Now.UtcDateTime.AddDays(-1)));

        await CreateService().GenerateAllAsync();

        Assert.That((await ReadAsync(TopStreamedWindows.Day)).Select(e => e.SongMetadataId), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public async Task RollingWindow_BreaksTiesBySongIdSoRebuildsAreStable()
    {
        await SeedAsync(
            songs: [Song(3), Song(1), Song(2)],
            streams: Streams(3, 5, Now.UtcDateTime).Concat(Streams(1, 5, Now.UtcDateTime)).Concat(Streams(2, 5, Now.UtcDateTime)));

        await CreateService().GenerateAllAsync();

        Assert.That((await ReadAsync(TopStreamedWindows.Day)).Select(e => e.SongMetadataId),
            Is.EqualTo(new[] { 1, 2, 3 }),
            "Level songs must order by id, or the playlist reshuffles nightly with no underlying change.");
    }

    [Test]
    public async Task RollingWindow_CapsAtTen()
    {
        var songs = Enumerable.Range(1, 15).Select(id => Song(id)).ToList();
        // Descending stream counts so the expected top ten is unambiguous.
        var streams = songs.SelectMany(song => Streams(song.Id, 100 - song.Id, Now.UtcDateTime));
        await SeedAsync(songs: songs, streams: streams);

        await CreateService().GenerateAllAsync();

        var entries = await ReadAsync(TopStreamedWindows.Day);
        Assert.Multiple(() =>
        {
            Assert.That(entries, Has.Count.EqualTo(TopStreamedPlaylists.MaxSongs));
            Assert.That(entries.Select(e => e.SongMetadataId), Is.EqualTo(Enumerable.Range(1, 10)));
        });
    }

    [Test]
    public async Task RollingWindow_ShowsFewerThanTenWhenTheWindowIsQuiet()
    {
        await SeedAsync(
            songs: [Song(1), Song(2)],
            streams: Streams(1, 2, Now.UtcDateTime).Concat(Streams(2, 1, Now.UtcDateTime)));

        await CreateService().GenerateAllAsync();

        Assert.That(await ReadAsync(TopStreamedWindows.Day), Has.Count.EqualTo(2),
            "A short playlist is honest; it must not be padded out to ten.");
    }

    [Test]
    public async Task RollingWindow_IsEmptyWhenNothingWasStreamed()
    {
        await SeedAsync(songs: [Song(1, lifetimeStreams: 500)]);

        await CreateService().GenerateAllAsync();

        Assert.That(await ReadAsync(TopStreamedWindows.Day), Is.Empty,
            "No streams in the window means no playlist, so the caller can hide the tile.");
    }

    // ---- Eligibility -------------------------------------------------------------

    [Test]
    public async Task Excludes_DisabledInactiveAndDeactivatedCreatorSongs()
    {
        await SeedAsync(
            creators: [new Creator { Id = 7, IsActive = false }, new Creator { Id = 8, IsActive = true }],
            songs:
            [
                Song(1, isEnabled: false),
                Song(2, isActive: false),
                Song(3, creatorId: 7),
                Song(4, creatorId: 8),
                Song(5)
            ],
            streams: Enumerable.Range(1, 5).SelectMany(id => Streams(id, 20 - id, Now.UtcDateTime)));

        await CreateService().GenerateAllAsync();

        Assert.That((await ReadAsync(TopStreamedWindows.Day)).Select(e => e.SongMetadataId),
            Is.EqualTo(new[] { 4, 5 }),
            "Disabled, inactive, and deactivated-creator songs must all drop out.");
    }

    [Test]
    public async Task Excludes_SongsWithNoPlayableAudio()
    {
        var noAudio = Song(1);
        noAudio.Mp3BlobPath = null;
        await SeedAsync(
            songs: [noAudio, Song(2)],
            streams: Streams(1, 50, Now.UtcDateTime).Concat(Streams(2, 1, Now.UtcDateTime)));

        await CreateService().GenerateAllAsync();

        Assert.That((await ReadAsync(TopStreamedWindows.Day)).Select(e => e.SongMetadataId), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public async Task IneligibleSongsDoNotShortenThePlaylist()
    {
        // The top ten by raw count are all ineligible. Over-fetching is what lets the playlist still
        // reach ten; ranking exactly ten rows and then filtering would have returned nothing.
        var blocked = Enumerable.Range(1, 10).Select(id => Song(id, isEnabled: false)).ToList();
        var allowed = Enumerable.Range(11, 10).Select(id => Song(id)).ToList();
        var streams = blocked.SelectMany(song => Streams(song.Id, 1000, Now.UtcDateTime))
            .Concat(allowed.SelectMany(song => Streams(song.Id, 100 - song.Id, Now.UtcDateTime)));
        await SeedAsync(songs: blocked.Concat(allowed), streams: streams);

        await CreateService().GenerateAllAsync();

        Assert.That(await ReadAsync(TopStreamedWindows.Day), Has.Count.EqualTo(TopStreamedPlaylists.MaxSongs));
    }

    // ---- All time ----------------------------------------------------------------

    [Test]
    public async Task AllTime_RanksOnTheLifetimeCounterNotTheEventLog()
    {
        // Song 1 has the bigger lifetime total but no logged events - exactly the shape of a song
        // that charted before the SongStreams table existed. It must still win all time.
        await SeedAsync(
            songs: [Song(1, lifetimeStreams: 900), Song(2, lifetimeStreams: 10)],
            streams: Streams(2, 10, Now.UtcDateTime));

        await CreateService().GenerateAllAsync();

        var allTime = await ReadAsync(TopStreamedWindows.AllTime);
        Assert.Multiple(() =>
        {
            Assert.That(allTime.Select(e => e.SongMetadataId), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(allTime[0].StreamCount, Is.EqualTo(900));
        });
    }

    [Test]
    public async Task AllTime_ExcludesSongsNobodyHasStreamed()
    {
        await SeedAsync(songs: [Song(1, lifetimeStreams: 5), Song(2, lifetimeStreams: 0)]);

        await CreateService().GenerateAllAsync();

        Assert.That((await ReadAsync(TopStreamedWindows.AllTime)).Select(e => e.SongMetadataId),
            Is.EqualTo(new[] { 1 }),
            "A never-played song does not belong on a most-streamed playlist.");
    }

    // ---- Regeneration ------------------------------------------------------------

    [Test]
    public async Task Regenerating_ReplacesRatherThanAppends()
    {
        await SeedAsync(
            songs: [Song(1), Song(2)],
            streams: Streams(1, 5, Now.UtcDateTime));

        await CreateService().GenerateAllAsync();
        await CreateService().GenerateAllAsync();

        Assert.That(await ReadAsync(TopStreamedWindows.Day), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Regenerating_EmptiesAWindowThatHasGoneQuiet()
    {
        await SeedAsync(
            songs: [Song(1)],
            streams: Streams(1, 5, Now.UtcDateTime));

        await CreateService().GenerateAllAsync();
        Assert.That(await ReadAsync(TopStreamedWindows.Day), Is.Not.Empty);

        // Two days later the same streams are outside the 24-hour window.
        await CreateService(Now.AddDays(2)).GenerateAllAsync();

        Assert.That(await ReadAsync(TopStreamedWindows.Day), Is.Empty,
            "A stale playlist must be cleared, not left showing yesterday's ranking.");
    }

    [Test]
    public async Task GenerateAllAsync_WritesEveryWindow()
    {
        await SeedAsync(
            songs: [Song(1, lifetimeStreams: 5)],
            streams: Streams(1, 5, Now.UtcDateTime));

        await CreateService().GenerateAllAsync();

        foreach (var descriptor in TopStreamedPlaylists.All)
        {
            Assert.That(await ReadAsync(descriptor.Window), Is.Not.Empty, $"{descriptor.Window} should have entries.");
        }
    }

    // ---- Reads -------------------------------------------------------------------

    [Test]
    public async Task GetAsync_ReturnsRankOrderWithSongMetadataLoaded()
    {
        await SeedAsync(
            songs: [Song(1), Song(2)],
            streams: Streams(1, 2, Now.UtcDateTime).Concat(Streams(2, 8, Now.UtcDateTime)));

        var service = CreateService();
        await service.GenerateAllAsync();

        var result = await service.GetAsync(TopStreamedWindows.Day);

        Assert.Multiple(() =>
        {
            Assert.That(result.Select(e => e.SongMetadataId), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(result[0].SongMetadata, Is.Not.Null, "The mapper needs the navigation property.");
        });
    }

    /// <summary>
    /// Applies <paramref name="change"/> to song 1 after the playlists have been built.
    /// </summary>
    private async Task<TopStreamedPlaylistService> GenerateThenChangeSongOneAsync(Action<SongMetadata> change)
    {
        await SeedAsync(
            songs: [Song(1), Song(2)],
            streams: Streams(1, 5, Now.UtcDateTime).Concat(Streams(2, 3, Now.UtcDateTime)));

        var service = CreateService();
        await service.GenerateAllAsync();

        await using (var context = new AppDbContext(_dbContextOptions))
        {
            change(await context.SongMetadata.FirstAsync(song => song.Id == 1));
            await context.SaveChangesAsync();
        }

        return service;
    }

    [Test]
    public async Task GetAsync_HidesASongTheAdminDisabledSinceGeneration()
    {
        // IsEnabled = false is the admin takedown - "content violates terms or policies". The playlist
        // is up to 24 hours stale, so a song pulled this morning has to disappear from it now rather
        // than at the next nightly rebuild.
        var service = await GenerateThenChangeSongOneAsync(song => song.IsEnabled = false);

        Assert.That((await service.GetAsync(TopStreamedWindows.Day)).Select(e => e.SongMetadataId),
            Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public async Task GetAsync_HidesASongTheCreatorDeletedSinceGeneration()
    {
        // IsActive = false is the creator's own deletion, or their account closing. Same staleness
        // argument as the admin case, and a separate flag - filtering only one would leak the other.
        var service = await GenerateThenChangeSongOneAsync(song => song.IsActive = false);

        Assert.That((await service.GetAsync(TopStreamedWindows.Day)).Select(e => e.SongMetadataId),
            Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public async Task GetAsync_HidesASongWhoseCreatorWasDeactivatedSinceGeneration()
    {
        await SeedAsync(
            creators: [new Creator { Id = 7, IsActive = true }],
            songs: [Song(1, creatorId: 7), Song(2)],
            streams: Streams(1, 5, Now.UtcDateTime).Concat(Streams(2, 3, Now.UtcDateTime)));

        var service = CreateService();
        await service.GenerateAllAsync();

        await using (var context = new AppDbContext(_dbContextOptions))
        {
            (await context.Creators.FirstAsync(creator => creator.Id == 7)).IsActive = false;
            await context.SaveChangesAsync();
        }

        Assert.That((await service.GetAsync(TopStreamedWindows.Day)).Select(e => e.SongMetadataId),
            Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public async Task GetCountsAsync_DoesNotCountASongDisabledSinceGeneration()
    {
        // The count drives the tile's "N song(s)" label, so it has to shrink with the playlist rather
        // than keep advertising a song the page will not show.
        var service = await GenerateThenChangeSongOneAsync(song => song.IsEnabled = false);

        var counts = await service.GetCountsAsync();

        Assert.That(counts[TopStreamedWindows.Day], Is.EqualTo(1));
    }

    [Test]
    public async Task GetCountsAsync_OmitsAWindowWhoseEverySongWasDisabled()
    {
        // And when the last one goes, the tile must disappear entirely rather than render as "0 songs".
        await SeedAsync(songs: [Song(1)], streams: Streams(1, 5, Now.UtcDateTime));

        var service = CreateService();
        await service.GenerateAllAsync();

        await using (var context = new AppDbContext(_dbContextOptions))
        {
            (await context.SongMetadata.FirstAsync(song => song.Id == 1)).IsEnabled = false;
            await context.SaveChangesAsync();
        }

        Assert.That(await service.GetCountsAsync(), Does.Not.ContainKey(TopStreamedWindows.Day));
    }

    [Test]
    public async Task GetAsync_ReturnsEmptyForAnUnknownWindow()
    {
        Assert.That(await CreateService().GetAsync("LastTuesday"), Is.Empty);
    }

    [Test]
    public async Task GetCountsAsync_OmitsEmptyWindowsSoCallersCanHideTheTile()
    {
        // Streamed only within the last hour, so Day/Week/Month/Year have entries and All Time does
        // not - the song's lifetime counter was never incremented.
        await SeedAsync(
            songs: [Song(1)],
            streams: Streams(1, 4, Now.UtcDateTime));

        var service = CreateService();
        await service.GenerateAllAsync();

        var counts = await service.GetCountsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(counts.ContainsKey(TopStreamedWindows.AllTime), Is.False);
            Assert.That(counts[TopStreamedWindows.Day], Is.EqualTo(1));
        });
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
