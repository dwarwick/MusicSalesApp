using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using System.Net.Http;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// Previous/Next on the song page.
///
/// <para>
/// Both buttons rendered but carried no <c>@onclick</c> at all until the "Marquee" redesign - they
/// were decoration. They now step through the song's own genre listing, in the order
/// <c>/genre/{name}</c> shows it, because a single-song URL carries no record of where the listener
/// came from and genre is the one context the page already puts on screen.
/// </para>
///
/// <para>
/// The five-button count these add to is asserted separately by
/// <see cref="PlayerTransportRegressionTests"/>; what is pinned here is where they go.
/// </para>
/// </summary>
[TestFixture]
public class SongPlayerTransportNavigationTests : BUnitTestBase
{
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
            new Uri("http://localhost/api/music/url/Beta.mp3"),
            new { Url = "http://localhost/api/music/Beta.mp3" });

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

    private const string Genre = "Indie Electronic";

    private static SongMetadata Song(int id, string title, string genre = Genre) => new()
    {
        Id = id,
        SongTitle = title,
        Mp3BlobPath = $"{title}.mp3",
        Genre = genre,
        DisplayOnHomePage = true,
        UpdatedAt = DateTime.Now
    };

    /// <summary>
    /// Renders the page for "Beta". <paramref name="genreListing"/> is what the genre page would
    /// show, in its order - which is what Previous/Next walk.
    /// </summary>
    private IRenderedComponent<SongPlayer> RenderBeta(params SongMetadata[] genreListing)
    {
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(genreListing.ToList());
        MockSongMetadataService.Setup(x => x.GetByGenreAsync(Genre))
            .ReturnsAsync(genreListing.ToList());

        return TestContext.Render<SongPlayer>(pb => pb.Add(p => p.SongTitle, "Beta"));
    }

    private static IElement PreviousButton(IRenderedComponent<SongPlayer> cut) =>
        cut.FindAll(".player-controls button")[1];

    private static IElement NextButton(IRenderedComponent<SongPlayer> cut) =>
        cut.FindAll(".player-controls button")[3];

    [Test]
    public void TheOnlySongInAGenreLeavesBothButtonsDisabled()
    {
        // Not hidden - the transport keeps its five controls at every breakpoint, so a dead end is
        // shown as unavailable rather than by resizing the bar.
        var cut = RenderBeta(Song(1, "Beta"));

        Assert.Multiple(() =>
        {
            Assert.That(PreviousButton(cut).HasAttribute("disabled"), Is.True);
            Assert.That(NextButton(cut).HasAttribute("disabled"), Is.True);
        });
    }

    [Test]
    public void NeighboursInTheGenreEnableBothButtons()
    {
        var cut = RenderBeta(Song(1, "Alpha"), Song(2, "Beta"), Song(3, "Gamma"));

        Assert.Multiple(() =>
        {
            Assert.That(PreviousButton(cut).HasAttribute("disabled"), Is.False);
            Assert.That(NextButton(cut).HasAttribute("disabled"), Is.False);
        });
    }

    [Test]
    public void TheTooltipsNameWhereTheButtonsActuallyGo()
    {
        var cut = RenderBeta(Song(1, "Alpha"), Song(2, "Beta"), Song(3, "Gamma"));

        Assert.Multiple(() =>
        {
            Assert.That(PreviousButton(cut).GetAttribute("title"), Is.EqualTo("Previous: Alpha"));
            Assert.That(NextButton(cut).GetAttribute("title"), Is.EqualTo("Next: Gamma"));
        });
    }

    [Test]
    public void TheEndsOfTheListingWrapRatherThanDeadEnd()
    {
        // Beta is last here, so Next comes back round to the first song.
        var cut = RenderBeta(Song(1, "Alpha"), Song(2, "Beta"));

        Assert.Multiple(() =>
        {
            Assert.That(NextButton(cut).GetAttribute("title"), Is.EqualTo("Next: Alpha"));
            Assert.That(PreviousButton(cut).GetAttribute("title"), Is.EqualTo("Previous: Alpha"));
        });
    }

    [Test]
    public void PressingNextNavigatesToTheNextSong()
    {
        var cut = RenderBeta(Song(1, "Alpha"), Song(2, "Beta"), Song(3, "Gamma"));
        var nav = TestContext.Services.GetRequiredService<NavigationManager>();

        NextButton(cut).Click();

        Assert.That(nav.Uri, Does.EndWith("/song/Gamma"));
    }

    [Test]
    public void PressingPreviousNavigatesToThePreviousSong()
    {
        var cut = RenderBeta(Song(1, "Alpha"), Song(2, "Beta"), Song(3, "Gamma"));
        var nav = TestContext.Services.GetRequiredService<NavigationManager>();

        PreviousButton(cut).Click();

        Assert.That(nav.Uri, Does.EndWith("/song/Alpha"));
    }

    [Test]
    public void ASongWithNoGenreStillWalksItsUnknownGenrePeers()
    {
        // GetGenre() reports "Unknown Genre" for a blank genre, and GetByGenreAsync special-cases
        // that same string to mean "everything with no genre set" - so the two line up and the
        // buttons keep working rather than silently disabling for every untagged song.
        var untagged = new[] { Song(1, "Alpha", genre: null), Song(2, "Beta", genre: null) };

        MockSongMetadataService.Setup(x => x.GetAllAsync()).ReturnsAsync(untagged.ToList());
        MockSongMetadataService.Setup(x => x.GetByGenreAsync("Unknown Genre"))
            .ReturnsAsync(untagged.ToList());

        var cut = TestContext.Render<SongPlayer>(pb => pb.Add(p => p.SongTitle, "Beta"));

        Assert.That(NextButton(cut).GetAttribute("title"), Is.EqualTo("Next: Alpha"));
    }

    [Test]
    public void AFailureLoadingTheGenreDisablesTheButtonsRatherThanThePage()
    {
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<SongMetadata> { Song(2, "Beta") });
        MockSongMetadataService.Setup(x => x.GetByGenreAsync(Genre))
            .ThrowsAsync(new InvalidOperationException("database is having a moment"));

        var cut = TestContext.Render<SongPlayer>(pb => pb.Add(p => p.SongTitle, "Beta"));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".player-bar"), Has.Count.EqualTo(1), "The page still renders.");
            Assert.That(PreviousButton(cut).HasAttribute("disabled"), Is.True);
            Assert.That(NextButton(cut).HasAttribute("disabled"), Is.True);
        });
    }
}
