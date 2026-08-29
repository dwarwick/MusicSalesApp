using Bunit;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Public;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The home page's "Most Streamed" island.
/// </summary>
/// <remarks>
/// The point of this component is that it is NOT gated on sign-in - unlike its neighbour
/// <c>HomeUserPlaylists</c>, whose content is personal. The anonymous test below is the one that
/// matters most; a stray auth check would be invisible to every other test in the suite.
/// </remarks>
[TestFixture]
public class HomeTopStreamedPlaylistsTests : BUnitTestBase
{
    private void GivenSongCounts(params (string Window, int Count)[] counts)
    {
        MockTopStreamedPlaylistService
            .Setup(x => x.GetCountsAsync())
            .ReturnsAsync(counts.ToDictionary(entry => entry.Window, entry => entry.Count));
    }

    [Test]
    public async Task RendersForAnAnonymousVisitor()
    {
        // No SetupAuthorizedUser call anywhere in this test, deliberately: these five playlists are
        // identical for every visitor and must be visible before sign-in.
        SetupRendererInfo();
        GivenSongCounts((TopStreamedWindows.Day, 10), (TopStreamedWindows.AllTime, 10));

        var cut = TestContext.Render<HomeTopStreamedPlaylists>();
        await cut.InvokeAsync(() => { });

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Top 10 Today"));
            Assert.That(cut.Markup, Does.Contain("Top 10 of All Time"));
            Assert.That(cut.Markup, Does.Contain("/top-streamed/Day"));
        });
    }

    [Test]
    public async Task ShowsThePlaylistsInDayWeekMonthYearAllTimeOrder()
    {
        SetupRendererInfo();
        // Seeded in a deliberately scrambled order - the component must impose the display order
        // rather than echo whatever the dictionary hands back.
        GivenSongCounts(
            (TopStreamedWindows.Year, 4),
            (TopStreamedWindows.Day, 1),
            (TopStreamedWindows.AllTime, 5),
            (TopStreamedWindows.Week, 2),
            (TopStreamedWindows.Month, 3));

        var cut = TestContext.Render<HomeTopStreamedPlaylists>();
        await cut.InvokeAsync(() => { });

        var markup = cut.Markup;
        var positions = TopStreamedPlaylists.All
            .OrderBy(descriptor => descriptor.DisplayOrder)
            .Select(descriptor => markup.IndexOf(descriptor.Name, StringComparison.Ordinal))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.None.EqualTo(-1), "Every playlist should render.");
            Assert.That(positions, Is.Ordered.Ascending,
                "Expected Day, Week, Month, Year, All Time in that order.");
        });
    }

    [Test]
    public async Task OmitsAPlaylistWithNoSongs()
    {
        // A quiet 24 hours drops the tile rather than showing an empty card - the same way
        // Recommended and Liked Songs already hide at zero.
        SetupRendererInfo();
        GivenSongCounts((TopStreamedWindows.AllTime, 10));

        var cut = TestContext.Render<HomeTopStreamedPlaylists>();
        await cut.InvokeAsync(() => { });

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Top 10 Today"));
            Assert.That(cut.Markup, Does.Contain("Top 10 of All Time"));
        });
    }

    [Test]
    public async Task RendersNothingWhenNoPlaylistHasSongs()
    {
        SetupRendererInfo();
        GivenSongCounts();

        var cut = TestContext.Render<HomeTopStreamedPlaylists>();
        await cut.InvokeAsync(() => { });

        Assert.That(cut.Markup.Trim(), Is.Empty,
            "A brand-new site should show no section at all, not an empty heading.");
    }

    [Test]
    public async Task ShowsTheSongCountForEachPlaylist()
    {
        SetupRendererInfo();
        GivenSongCounts((TopStreamedWindows.Day, 7));

        var cut = TestContext.Render<HomeTopStreamedPlaylists>();
        await cut.InvokeAsync(() => { });

        Assert.That(cut.Markup, Does.Contain("7 song(s)"));
    }

    [Test]
    public async Task SurvivesTheServiceFailing()
    {
        // Best-effort: a failure here must not take the home page down with it.
        SetupRendererInfo();
        MockTopStreamedPlaylistService
            .Setup(x => x.GetCountsAsync())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var cut = TestContext.Render<HomeTopStreamedPlaylists>();
        await cut.InvokeAsync(() => { });

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
