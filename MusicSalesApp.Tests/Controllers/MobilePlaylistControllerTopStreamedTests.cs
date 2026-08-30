using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Controllers;

// This project does not enable nullable reference types project-wide.
#nullable enable

/// <summary>
/// The mobile endpoints for the five global "most streamed" playlists.
/// </summary>
/// <remarks>
/// The behaviour worth pinning here is that these two endpoints answer a <b>signed-out</b> caller,
/// while every other action on the controller does not. That is a deliberate exception to the
/// class-level <c>[Authorize]</c>, and it is invisible to a test that only ever calls them signed in.
/// </remarks>
[TestFixture]
public class MobilePlaylistControllerTopStreamedTests
{
    private Mock<IPlaylistService> _mockPlaylistService = null!;
    private Mock<IRecommendationService> _mockRecommendationService = null!;
    private Mock<ITopStreamedPlaylistService> _mockTopStreamed = null!;
    private Mock<ISubscriptionService> _mockSubscriptionService = null!;
    private Mock<ISongMetadataService> _mockSongMetadataService = null!;
    private Mock<IAppSettingsService> _mockAppSettingsService = null!;
    private Mock<IMobileSongMapper> _mockSongMapper = null!;
    private DbContextOptions<AppDbContext> _options = null!;

    [SetUp]
    public void SetUp()
    {
        _mockPlaylistService = new Mock<IPlaylistService>();
        _mockRecommendationService = new Mock<IRecommendationService>();
        _mockTopStreamed = new Mock<ITopStreamedPlaylistService>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockSongMetadataService = new Mock<ISongMetadataService>();
        _mockAppSettingsService = new Mock<IAppSettingsService>();
        _mockSongMapper = new Mock<IMobileSongMapper>();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"MobilePlaylistTopStreamed_{Guid.NewGuid()}")
            .Options;

        _mockAppSettingsService.Setup(s => s.GetStreamQualifyingSettingsAsync())
            .ReturnsAsync(new StreamQualifyingSettings(30, false));

        _mockTopStreamed.Setup(s => s.GetCountsAsync())
            .ReturnsAsync(new Dictionary<string, int>());
        _mockTopStreamed.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TopStreamedPlaylistEntry>());

        _mockSongMapper
            .Setup(m => m.MapToPlaylistSong(
                It.IsAny<SongMetadata>(), It.IsAny<TimeSpan>(), It.IsAny<int?>(),
                It.IsAny<StreamQualifyingSettings>(), It.IsAny<SongLyrics?>(), It.IsAny<MobileStreamContext?>()))
            .Returns((SongMetadata song, TimeSpan _, int? __, StreamQualifyingSettings ___, SongLyrics? ____, MobileStreamContext? _____)
                => new MobilePlaylistSongDto { SongMetadataId = song.Id, SongTitle = song.SongTitle });
    }

    /// <param name="userId">Null for a signed-out caller.</param>
    private MobilePlaylistController CreateController(int? userId)
    {
        var controller = new MobilePlaylistController(
            _mockPlaylistService.Object,
            _mockRecommendationService.Object,
            _mockTopStreamed.Object,
            _mockSubscriptionService.Object,
            _mockSongMetadataService.Object,
            _mockAppSettingsService.Object,
            _mockSongMapper.Object,
            new TestDbContextFactory(_options),
            Mock.Of<ILogger<MobilePlaylistController>>());

        var httpContext = new DefaultHttpContext
        {
            User = userId is null
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())], "Bearer"))
        };

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static TopStreamedPlaylistEntry Entry(int songId, int displayOrder, int streamCount) => new()
    {
        Window = TopStreamedWindows.Day,
        SongMetadataId = songId,
        DisplayOrder = displayOrder,
        StreamCount = streamCount,
        SongMetadata = new SongMetadata
        {
            Id = songId,
            SongTitle = $"Song {songId}",
            Mp3BlobPath = $"{songId}/{songId}-music.mp3"
        }
    };

    private static T Body<T>(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        return (T)((OkObjectResult)result).Value!;
    }

    // ---- The anonymous surface ----------------------------------------------------

    [Test]
    public void TheTwoTopStreamedActionsAllowAnonymousCallers()
    {
        // The class carries [Authorize]; these two override it. Asserted by reflection because the
        // pipeline that enforces it does not run in a unit test - and because silently losing the
        // attribute would make the playlists vanish for signed-out users with nothing else failing.
        foreach (var actionName in new[] { nameof(MobilePlaylistController.GetTopStreamedPlaylists),
                                           nameof(MobilePlaylistController.GetTopStreamedSongs) })
        {
            var action = typeof(MobilePlaylistController).GetMethod(actionName)!;

            Assert.Multiple(() =>
            {
                Assert.That(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), true), Is.Not.Empty,
                    $"{actionName} must serve signed-out visitors.");

                var authorize = action.GetCustomAttributes(typeof(AuthorizeAttribute), true)
                    .Cast<AuthorizeAttribute>()
                    .SingleOrDefault();

                // Without the explicit schemes the MAUI bearer token is ignored, HttpContext.User is
                // empty, and a signed-in subscriber is served preview-length audio.
                Assert.That(authorize, Is.Not.Null, $"{actionName} must list the auth schemes.");
                Assert.That(authorize!.AuthenticationSchemes, Does.Contain("Bearer").And.Contain("Identity.Application"));
            });
        }
    }

    [Test]
    public async Task GetTopStreamedPlaylists_AnswersASignedOutCaller()
    {
        _mockTopStreamed.Setup(s => s.GetCountsAsync()).ReturnsAsync(new Dictionary<string, int>
        {
            [TopStreamedWindows.Day] = 10,
            [TopStreamedWindows.AllTime] = 8
        });

        var result = await CreateController(userId: null).GetTopStreamedPlaylists();

        var tiles = Body<List<MobilePlaylistDto>>(result);
        Assert.That(tiles.Select(t => t.Key), Is.EqualTo(new[] { TopStreamedWindows.Day, TopStreamedWindows.AllTime }));
    }

    [Test]
    public async Task GetTopStreamedSongs_AnswersASignedOutCallerWithPreviewOnlyAccess()
    {
        _mockTopStreamed.Setup(s => s.GetAsync(TopStreamedWindows.Day))
            .ReturnsAsync([Entry(1, 1, 50)]);

        var result = await CreateController(userId: null).GetTopStreamedSongs(TopStreamedWindows.Day);

        Assert.That(Body<MobilePlaylistSongsDto>(result).Songs, Has.Count.EqualTo(1));

        // A caller with no id gets no full access, so the manifest they are handed is preview-length.
        _mockSongMapper.Verify(m => m.MapToPlaylistSong(
            It.IsAny<SongMetadata>(), It.IsAny<TimeSpan>(), It.IsAny<int?>(), It.IsAny<StreamQualifyingSettings>(),
            It.IsAny<SongLyrics?>(),
            It.Is<MobileStreamContext?>(context => context!.UserId == null && !context.HasFullAccess)),
            Times.Once);
        _mockSubscriptionService.Verify(s => s.HasActiveSubscriptionAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task GetTopStreamedSongs_HonoursASignedInSubscribersEntitlement()
    {
        _mockTopStreamed.Setup(s => s.GetAsync(TopStreamedWindows.Day)).ReturnsAsync([Entry(1, 1, 50)]);
        _mockSubscriptionService.Setup(s => s.HasActiveSubscriptionAsync(7)).ReturnsAsync(true);

        await CreateController(userId: 7).GetTopStreamedSongs(TopStreamedWindows.Day);

        _mockSongMapper.Verify(m => m.MapToPlaylistSong(
            It.IsAny<SongMetadata>(), It.IsAny<TimeSpan>(), It.IsAny<int?>(), It.IsAny<StreamQualifyingSettings>(),
            It.IsAny<SongLyrics?>(),
            It.Is<MobileStreamContext?>(context => context!.UserId == 7 && context.HasFullAccess)),
            Times.Once);
    }

    // ---- Shape of the response ----------------------------------------------------

    [Test]
    public async Task GetTopStreamedPlaylists_OmitsEmptyWindowsAndKeepsDisplayOrder()
    {
        _mockTopStreamed.Setup(s => s.GetCountsAsync()).ReturnsAsync(new Dictionary<string, int>
        {
            // Seeded out of order; the response must impose the display order.
            [TopStreamedWindows.Year] = 4,
            [TopStreamedWindows.Day] = 10
        });

        var tiles = Body<List<MobilePlaylistDto>>(await CreateController(null).GetTopStreamedPlaylists());

        Assert.Multiple(() =>
        {
            Assert.That(tiles.Select(t => t.Key), Is.EqualTo(new[] { TopStreamedWindows.Day, TopStreamedWindows.Year }));
            Assert.That(tiles, Has.All.Matches<MobilePlaylistDto>(t => t.Kind == MobilePlaylistKinds.TopStreamed));
            Assert.That(tiles, Has.All.Matches<MobilePlaylistDto>(t => t.Id == 0),
                "These have no row, so the client must open them by Key.");
        });
    }

    [Test]
    public async Task GetTopStreamedSongs_CarriesBothStreamCountsForARollingWindow()
    {
        _mockTopStreamed.Setup(s => s.GetAsync(TopStreamedWindows.Day))
            .ReturnsAsync([Entry(1, 1, 99), Entry(2, 2, 40)]);

        var body = Body<MobilePlaylistSongsDto>(await CreateController(null).GetTopStreamedSongs(TopStreamedWindows.Day));

        Assert.Multiple(() =>
        {
            Assert.That(body.PeriodLabel, Is.EqualTo("Today"));
            Assert.That(body.Songs.Select(s => s.PeriodStreamCount), Is.EqualTo(new int?[] { 99, 40 }),
                "Descending: this is what the list was ranked on.");
        });
    }

    [Test]
    public async Task GetTopStreamedSongs_LeavesThePeriodCountOffTheAllTimeList()
    {
        // There the ranking number and the lifetime counter are the same figure, so a second column
        // would only repeat the first.
        _mockTopStreamed.Setup(s => s.GetAsync(TopStreamedWindows.AllTime))
            .ReturnsAsync([Entry(1, 1, 5000)]);

        var body = Body<MobilePlaylistSongsDto>(await CreateController(null).GetTopStreamedSongs(TopStreamedWindows.AllTime));

        Assert.Multiple(() =>
        {
            Assert.That(body.PeriodLabel, Is.Null);
            Assert.That(body.Songs[0].PeriodStreamCount, Is.Null);
        });
    }

    [Test]
    public async Task GetTopStreamedSongs_ReportsWhenTheRankingWasTaken()
    {
        // The client shows this because rank order is up to a day old while the counts beside it are
        // live, so the two can disagree slightly.
        var generatedAt = new DateTime(2026, 8, 29, 2, 0, 0, DateTimeKind.Utc);
        var entry = Entry(1, 1, 12);
        entry.GeneratedAt = generatedAt;
        _mockTopStreamed.Setup(s => s.GetAsync(TopStreamedWindows.Day)).ReturnsAsync([entry]);

        var body = Body<MobilePlaylistSongsDto>(await CreateController(null).GetTopStreamedSongs(TopStreamedWindows.Day));

        Assert.That(body.GeneratedAtUtc, Is.EqualTo(generatedAt));
    }

    [Test]
    public async Task GetTopStreamedPlaylists_ReportsWhenTheRankingWasTaken()
    {
        var generatedAt = new DateTime(2026, 8, 29, 2, 0, 0, DateTimeKind.Utc);
        _mockTopStreamed.Setup(s => s.GetCountsAsync())
            .ReturnsAsync(new Dictionary<string, int> { [TopStreamedWindows.Day] = 10 });
        _mockTopStreamed.Setup(s => s.GetLastGeneratedAtAsync()).ReturnsAsync(generatedAt);

        var tiles = Body<List<MobilePlaylistDto>>(await CreateController(null).GetTopStreamedPlaylists());

        Assert.That(tiles.Single().GeneratedAtUtc, Is.EqualTo(generatedAt));
    }

    [Test]
    public async Task GetTopStreamedSongs_PassesThroughWhateverPeriodCountTheServiceReports()
    {
        // The service recounts the window at read time, so the controller must not substitute
        // anything of its own here.
        _mockTopStreamed.Setup(s => s.GetAsync(TopStreamedWindows.Day)).ReturnsAsync([Entry(1, 1, 42)]);

        var body = Body<MobilePlaylistSongsDto>(await CreateController(null).GetTopStreamedSongs(TopStreamedWindows.Day));

        Assert.That(body.Songs.Single().PeriodStreamCount, Is.EqualTo(42));
    }

    [Test]
    public async Task GetTopStreamedSongs_RejectsAnUnknownWindow()
    {
        var result = await CreateController(null).GetTopStreamedSongs("LastTuesday");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetTopStreamedSongs_ResolvesTheWindowCaseInsensitively()
    {
        // These keys travel in URLs, where a hand-typed or lower-cased link is likely.
        _mockTopStreamed.Setup(s => s.GetAsync(TopStreamedWindows.Day)).ReturnsAsync([Entry(1, 1, 5)]);

        var result = await CreateController(null).GetTopStreamedSongs("day");

        Assert.That(Body<MobilePlaylistSongsDto>(result).PlaylistName, Is.EqualTo("Top 10 Today"));
    }

    // ---- The Kind mislabel this change also fixed ---------------------------------

    [Test]
    public async Task GetMyPlaylists_OnlyLabelsTheActualLikedSongsPlaylistAsLikedSongs()
    {
        // Previously any IsSystemGenerated playlist was labelled LikedSongs, so a second system
        // playlist would have been mislabelled.
        _mockPlaylistService.Setup(s => s.GetOrCreateLikedSongsPlaylistAsync(7))
            .ReturnsAsync(new Playlist { Id = 1, UserId = 7, PlaylistName = PlaylistNames.LikedSongs, IsSystemGenerated = true });
        _mockPlaylistService.Setup(s => s.GetUserPlaylistsAsync(7)).ReturnsAsync(
        [
            new Playlist { Id = 1, UserId = 7, PlaylistName = PlaylistNames.LikedSongs, IsSystemGenerated = true },
            new Playlist { Id = 2, UserId = 7, PlaylistName = "Something Else", IsSystemGenerated = true },
            new Playlist { Id = 3, UserId = 7, PlaylistName = "Rock", IsSystemGenerated = false }
        ]);
        _mockPlaylistService.Setup(s => s.GetPlaylistSongsAsync(It.IsAny<int>())).ReturnsAsync([]);

        var dtos = Body<List<MobilePlaylistDto>>(await CreateController(userId: 7).GetMyPlaylists());

        Assert.Multiple(() =>
        {
            Assert.That(dtos.Single(d => d.Id == 1).Kind, Is.EqualTo(MobilePlaylistKinds.LikedSongs));
            Assert.That(dtos.Single(d => d.Id == 2).Kind, Is.Not.EqualTo(MobilePlaylistKinds.LikedSongs));
            Assert.That(dtos.Single(d => d.Id == 3).Kind, Is.EqualTo(MobilePlaylistKinds.Custom));
        });
    }

    // Re-declared per file, following the convention the other controller/service fixtures use.
    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }
}
