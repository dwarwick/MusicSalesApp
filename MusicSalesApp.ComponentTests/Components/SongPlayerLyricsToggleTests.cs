#nullable enable
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using System.Net.Http;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The art/lyrics toggle on the song player page.
///
/// <para>
/// <b>Every test here is really the same question: can a listener reach timings nobody approved?</b>
/// Since alignment stopped publishing, a freshly timed song sits in <c>NeedsReview</c> with its
/// timings at exactly the blob path a published song would use. The media route already refuses
/// those, so this component is the second gate rather than the only one - but a toggle that appears
/// and then shows nothing is its own bug, and the two failures look identical from the outside.
/// </para>
/// </summary>
[TestFixture]
public class SongPlayerLyricsToggleTests : BUnitTestBase
{
    private StubHttpMessageHandler _handler = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        var js = new Mock<IJSRuntime>();
        js.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(new Mock<IJSObjectReference>().Object);
        TestContext.Services.AddSingleton(js.Object);

        _handler = new StubHttpMessageHandler();
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _handler.SetupJsonResponse(
            new Uri("http://localhost/api/cart/status/TestSong.mp3"),
            new { Owns = false, InCart = false });
        _handler.SetupJsonResponse(
            new Uri("http://localhost/api/music/url/TestSong.mp3"),
            new { Url = "http://localhost/api/music/TestSong.mp3" });

        TestContext.Services.AddSingleton(_httpClient);
        SetupRendererInfo();

        MockSongMetadataService
            .Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata>
            {
                new()
                {
                    Id = 1,
                    Mp3BlobPath = "TestSong.mp3",
                    SongTitle = "TestSong",
                    DisplayOnHomePage = true,
                    UpdatedAt = DateTime.Now
                }
            });
    }

    [TearDown]
    public override void BaseTearDown()
    {
        _httpClient?.Dispose();
        _handler?.Dispose();
        base.BaseTearDown();
    }

    private IRenderedComponent<SongPlayer> RenderWith(SongLyrics? lyrics)
    {
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lyrics);

        return TestContext.Render<SongPlayer>(pb => pb.Add(p => p.SongTitle, "TestSong"));
    }

    private static SongLyrics Lyrics(SongLyricsStatus status, string? timings = "abc/abc-lyrics.json") => new()
    {
        SongMetadataId = 1,
        Status = status,
        TimingsBlobPath = timings,
        Version = 4
    };

    [Test]
    public void PublishedLyricsGetAToggle()
    {
        var cut = RenderWith(Lyrics(SongLyricsStatus.Published));

        Assert.That(cut.FindAll(".lyrics-toggle-button"), Is.Not.Empty);
    }

    [TestCase(SongLyricsStatus.NeedsReview)]
    [TestCase(SongLyricsStatus.Pending)]
    [TestCase(SongLyricsStatus.Failed)]
    public void EveryOtherStatusLooksExactlyLikeASongWithNoLyrics(SongLyricsStatus status)
    {
        // NeedsReview is the important one and the most likely to be got wrong, because the timings
        // genuinely exist and sit at the published path - only the row says they are not for
        // listeners. It is now the state EVERY successful alignment lands in.
        var cut = RenderWith(Lyrics(status));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".lyrics-toggle-button"), Is.Empty);
            Assert.That(cut.FindAll(".lyrics-scroller"), Is.Empty);
        });
    }

    [Test]
    public void ASongWithNoLyricsRowHasNoToggle()
    {
        var cut = RenderWith(lyrics: null);

        Assert.That(cut.FindAll(".lyrics-toggle-button"), Is.Empty);
    }

    [Test]
    public void PublishedButWithNoTimingsFileIsStillNotOffered()
    {
        // Defensive: a row could reach Published with its timings path cleared. Offering the toggle
        // would fetch nothing and leave the listener staring at an empty panel.
        var cut = RenderWith(Lyrics(SongLyricsStatus.Published, timings: null));

        Assert.That(cut.FindAll(".lyrics-toggle-button"), Is.Empty);
    }

    [Test]
    public void TheTimingsUrlCarriesTheVersionAsACacheBuster()
    {
        // The blob path never changes between versions and the response is served immutable for a
        // year. Without ?v= a creator's re-publish would be invisible to every browser that had
        // already seen the song, permanently.
        // Asserted on the parameter rather than the markup: the URL is handed to the JS module to
        // fetch with, so it never appears in the rendered HTML at all.
        var cut = RenderWith(Lyrics(SongLyricsStatus.Published));

        var scroller = cut.FindComponent<MusicSalesApp.Components.Shared.LyricsScroller>();

        Assert.That(scroller.Instance.TimingsUrl, Is.EqualTo("/api/music/abc/abc-lyrics.json?v=4"));
    }

    [Test]
    public void TheArtIsWhatShowsBeforeAnybodyPressesTheToggle()
    {
        // The lyrics panel is mounted from the start so its animation loop survives toggling, so
        // "which one is showing" is a class rather than which one exists. Defaulting to lyrics would
        // change what every listener sees on arrival.
        var cut = RenderWith(Lyrics(SongLyricsStatus.Published));

        var overlay = cut.Find(".lyrics-scroller-overlay");

        Assert.That(overlay.GetAttribute("class"), Does.Contain("is-hidden"));
    }

    [Test]
    public void PressingTheToggleSwapsWhichOneIsHidden()
    {
        var cut = RenderWith(Lyrics(SongLyricsStatus.Published));

        cut.Find(".lyrics-toggle-button").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Find(".lyrics-scroller-overlay").GetAttribute("class"), Does.Not.Contain("is-hidden"));
            Assert.That(cut.FindAll(".lyrics-scroller"), Is.Not.Empty, "Still mounted, now visible.");
        });
    }
}
