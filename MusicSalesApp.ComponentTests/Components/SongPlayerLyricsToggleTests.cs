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

    private IRenderedComponent<SongPlayer> RenderWith(
        SongLyrics? lyrics,
        MusicSalesApp.Common.Contracts.LyricsTimingsDocument? timings = null)
    {
        MockLyricsService.Setup(x => x.GetForSongAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lyrics);

        // The service is what enforces "published only" for the document itself, so the stub answers
        // for whatever this case asked for rather than deriving it from the row.
        MockLyricsService
            .Setup(x => x.GetPublishedTimingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(timings);

        return TestContext.Render<SongPlayer>(pb => pb.Add(p => p.SongTitle, "TestSong"));
    }

    private static MusicSalesApp.Common.Contracts.LyricsTimingsDocument TimingsDocument() => new()
    {
        SongId = 1,
        DurationMs = 240_000,
        Lines =
        [
            new MusicSalesApp.Common.Contracts.LyricsTimedLine
            {
                Text = "hello world",
                StartMs = 1_000,
                EndMs = 3_000,
                Words =
                [
                    new MusicSalesApp.Common.Contracts.LyricsTimedWord
                    { Text = "hello", StartMs = 1_000, EndMs = 2_000 },
                    new MusicSalesApp.Common.Contracts.LyricsTimedWord
                    { Text = "world", StartMs = 2_000, EndMs = 3_000 }
                ]
            }
        ]
    };

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

    /// <summary>
    /// The player renders the words, rather than merely naming where they live.
    ///
    /// <para>
    /// This replaces a test that asserted the scroller was handed a timings URL, which it was - and
    /// the panel still showed nothing but "Lyrics are loading…" forever. The words are drawn from a
    /// document C# holds; a URL only ever reached the browser-side highlighter, which can colour
    /// existing spans but cannot create them. The old test passed for as long as the feature was
    /// completely broken, which is the reason this one asserts on rendered output instead.
    /// </para>
    /// </summary>
    [Test]
    public void PublishedLyricsAreActuallyRenderedAsWords()
    {
        var cut = RenderWith(Lyrics(SongLyricsStatus.Published), TimingsDocument());

        cut.WaitForState(() => cut.FindAll("[data-w]").Count > 0, TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll("[data-w]"), Has.Count.EqualTo(2), "One span per timed word.");
            Assert.That(cut.Markup, Does.Contain("hello"));
            Assert.That(cut.Markup, Does.Not.Contain("Lyrics are loading"));
        });
    }

    [Test]
    public void APublishedSongWhoseDocumentCannotBeReadSaysSoRatherThanRenderingNothing()
    {
        // The row says published but the blob is unreadable - the empty message is then honest, and
        // it is the state the whole player used to sit in permanently.
        var cut = RenderWith(Lyrics(SongLyricsStatus.Published), timings: null);

        Assert.That(cut.Markup, Does.Contain("Lyrics are loading"));
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
