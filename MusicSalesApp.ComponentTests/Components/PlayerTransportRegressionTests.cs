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
/// The transport bar's markup, pinned exactly as it renders today.
///
/// <para>
/// <b>These tests exist to be run unchanged after a refactor, not to describe new behaviour.</b> The
/// play/pause/seek/volume bar is currently duplicated between the song page, the playlist page and
/// the card mini-player, and it is about to be extracted into one shared component. The two
/// code-behinds involved are around 2,670 lines between them, and markup is the only thing that can
/// be asserted on cheaply - so the assertions have to exist <em>before</em> the extraction. Written
/// afterwards they would only prove the new code matches itself.
/// </para>
///
/// <para>
/// If a change to <c>PlayerTransport</c> makes one of these fail, the question to ask is not "how do
/// I update the test" but "what did a listener just lose".
/// </para>
/// </summary>
[TestFixture]
public class PlayerTransportRegressionTests : BUnitTestBase
{
    /// <summary>The pause glyph's path data - what renders while a song is playing.</summary>
    private const string PauseGlyph = "M6 19h4V5H6v14z";

    /// <summary>The play glyph's path data - what renders while a song is stopped.</summary>
    private const string PlayGlyph = "M8 5v14l11-7z";

    private Mock<IJSRuntime> _jsRuntime = null!;
    private Mock<IJSObjectReference> _jsModule = null!;
    private StubHttpMessageHandler _handler = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        _jsRuntime = new Mock<IJSRuntime>();
        _jsModule = new Mock<IJSObjectReference>();
        _jsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(_jsModule.Object);

        TestContext.Services.AddSingleton(_jsRuntime.Object);

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
    }

    [TearDown]
    public override void BaseTearDown()
    {
        _httpClient?.Dispose();
        _handler?.Dispose();
        base.BaseTearDown();
    }

    /// <summary>
    /// Render the song player for a track.
    /// </summary>
    /// <param name="freelyPlayable">
    /// Drives <c>IsProgressBarRestricted</c> without touching auth: a song on the home page is
    /// playable in full by anybody, so this flips the restriction from data alone.
    /// </param>
    private IRenderedComponent<SongPlayer> RenderSongPlayer(bool freelyPlayable)
    {
        MockSongMetadataService
            .Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata>
            {
                new()
                {
                    Id = 1,
                    Mp3BlobPath = "TestSong.mp3",
                    SongTitle = "TestSong",
                    DisplayOnHomePage = freelyPlayable,
                    UpdatedAt = DateTime.Now
                }
            });

        return TestContext.Render<SongPlayer>(pb => pb.Add(p => p.SongTitle, "TestSong"));
    }

    [Test]
    public void ThereIsExactlyOnePlayerBar()
    {
        // One bar, not two. The extraction's most obvious failure mode is leaving the original markup
        // in place alongside the new component, which renders two sets of controls fighting over one
        // audio element.
        var cut = RenderSongPlayer(freelyPlayable: true);

        Assert.That(cut.FindAll(".player-bar"), Has.Count.EqualTo(1));
    }

    [TestCase("controls-wrapper")]
    [TestCase("player-controls-row")]
    [TestCase("player-controls")]
    [TestCase("volume-controls")]
    [TestCase("volume-bar-container")]
    [TestCase("progress-container")]
    [TestCase("progress-bar-container")]
    [TestCase("progress-bar")]
    public void EveryStructuralClassSurvives(string className)
    {
        // Every one of these is styled from app.css and the theme files. A class dropped during the
        // extraction produces an unstyled control rather than a missing one, which is the kind of
        // regression that reaches production because everything still "works".
        var cut = RenderSongPlayer(freelyPlayable: true);

        Assert.That(cut.FindAll("." + className), Is.Not.Empty, $".{className} is missing.");
    }

    [Test]
    public void BothTimeDisplaysRender()
    {
        // Elapsed on the left, duration on the right. Losing one leaves a bar that says how far in
        // you are with nothing to measure it against.
        var cut = RenderSongPlayer(freelyPlayable: true);

        Assert.That(cut.FindAll(".time-display"), Has.Count.EqualTo(2));
    }

    [Test]
    public void TheFiveTransportButtonsRender()
    {
        // Shuffle, previous, play, next, repeat. Previous and next are inert on this page today and
        // are still rendered, so the count is five rather than three.
        var cut = RenderSongPlayer(freelyPlayable: true);

        var buttons = cut.FindAll(".player-controls button");

        Assert.That(buttons, Has.Count.EqualTo(5));
    }

    [Test]
    public void AStoppedSongShowsThePlayGlyphAndNotThePauseGlyph()
    {
        var cut = RenderSongPlayer(freelyPlayable: true);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain(PlayGlyph));
            Assert.That(cut.Markup, Does.Not.Contain(PauseGlyph));
        });
    }

    [Test]
    public void TheProgressBarCarriesAnInlineWidth()
    {
        // The bar is driven by an inline width rather than a class, so the style attribute is the
        // whole mechanism. A refactor that moved the calculation into the shared component and got
        // the units wrong would render a bar that never moves.
        var cut = RenderSongPlayer(freelyPlayable: true);

        var bar = cut.Find(".progress-bar");

        Assert.That(bar.GetAttribute("style"), Does.Contain("width:"));
    }

    [Test]
    public void TheVolumeBarCarriesAnInlineWidthToo()
    {
        var cut = RenderSongPlayer(freelyPlayable: true);

        Assert.That(cut.Find(".volume-bar").GetAttribute("style"), Does.Contain("width:"));
    }

    // -----------------------------------------------------------------
    // The preview restriction, which is a monetisation rule rather than a style
    // -----------------------------------------------------------------

    [Test]
    public void ARestrictedSongMarksTheBarAndShowsTheLimit()
    {
        // A non-subscriber gets 60 seconds. Both halves matter: the class is what greys the
        // unreachable part of the bar, and the marker is what shows where the preview stops. Losing
        // either during the extraction would give away full playback in appearance if not in fact.
        var cut = RenderSongPlayer(freelyPlayable: false);

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".progress-bar-container.restricted"), Is.Not.Empty);
            Assert.That(cut.FindAll(".progress-limit-marker"), Is.Not.Empty);
            Assert.That(
                cut.Find(".progress-limit-marker").GetAttribute("style"),
                Does.Contain("left:"),
                "The marker is positioned inline.");
        });
    }

    [Test]
    public void AFreelyPlayableSongHasNeitherMarkNorMarker()
    {
        // The opposite direction, and the one that would be missed: a refactor that always applied
        // the restricted styling would show every listener a 60-second cap they do not have.
        var cut = RenderSongPlayer(freelyPlayable: true);

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".progress-bar-container.restricted"), Is.Empty);
            Assert.That(cut.FindAll(".progress-limit-marker"), Is.Empty);
        });
    }

    // -----------------------------------------------------------------
    // The playlist page's copy of the same bar
    // -----------------------------------------------------------------

    [Test]
    [Ignore("PlaylistPlayerInteractive loads its data in OnAfterRenderAsync, which bUnit does not "
            + "reliably trigger - the same reason two tests in PlaylistPlayerTests are skipped. The "
            + "transport bar is inside the loaded state, so it never renders here and every assertion "
            + "below finds nothing. Kept rather than deleted because it is a real gap with a real "
            + "consequence: the playlist page's half of the transport extraction has no automated "
            + "safety net and has to be checked by hand. It starts passing the day that component's "
            + "loading moves to OnInitializedAsync.")]
    public void ThePlaylistPageRendersTheSameTransportBar()
    {
        var playlist = new Playlist { Id = 1, UserId = 1, PlaylistName = "My Test Playlist" };
        var song = new SongMetadata
        {
            Id = 1,
            Mp3BlobPath = "TestSong.mp3",
            SongTitle = "TestSong",
            DisplayOnHomePage = true,
            IsActive = true,
            IsEnabled = true
        };

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1)).ReturnsAsync(playlist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1))
            .ReturnsAsync(new List<UserPlaylist>
            {
                new() { Id = 1, UserId = 1, SongMetadataId = 1, SongMetadata = song, Playlist = playlist }
            });

        var cut = TestContext.Render<PlaylistPlayer>(pb => pb.Add(p => p.PlaylistId, 1));
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".player-bar"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(".progress-bar-container"), Is.Not.Empty);
            Assert.That(cut.FindAll(".volume-bar-container"), Is.Not.Empty);
            Assert.That(cut.FindAll(".time-display"), Has.Count.EqualTo(2));
        });
    }
}
