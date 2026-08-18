#nullable enable
using MusicSalesApp.Components.Players;
using MusicSalesApp.Models;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The playlist page's lyrics gating, asserted on the model rather than through rendered markup.
///
/// <para>
/// <b>Not a stylistic choice.</b> <c>PlaylistPlayerInteractive</c> loads its data in
/// <c>OnAfterRenderAsync</c>, which bUnit does not reliably trigger - two tests in
/// <c>PlaylistPlayerTests</c> and one in <c>PlayerTransportRegressionTests</c> are skipped for
/// exactly that reason, and the page never reaches its loaded state here. Rendering assertions were
/// tried and found nothing.
/// </para>
///
/// <para>
/// So this asserts the part that decides whether a listener can reach unapproved timings, which is
/// pure and needs no renderer. The markup around it - the toggle button, the overlay, the class swap
/// - is the same markup as the song player's, which <em>is</em> covered by
/// <c>SongPlayerLyricsToggleTests</c>. What remains genuinely unverified is that the two are wired
/// together on this page, and that needs a browser.
/// </para>
/// </summary>
[TestFixture]
public class PlaylistPlayerLyricsToggleTests
{
    private sealed class TrackLyricsProbe : PlaylistPlayerInteractiveModel
    {
        public void SetTrackLyrics(SongLyrics? lyrics) => _currentTrackLyrics = lyrics;

        public bool Offered => HasPublishedLyrics();

        public string? TimingsUrl => GetLyricsTimingsUrl();
    }

    private static SongLyrics Lyrics(
        SongLyricsStatus status,
        string? timings = "abc/abc-lyrics.json",
        int version = 4) => new()
    {
        SongMetadataId = 1,
        Status = status,
        TimingsBlobPath = timings,
        Version = version
    };

    [Test]
    public void APublishedTrackOffersItsLyrics()
    {
        var probe = new TrackLyricsProbe();
        probe.SetTrackLyrics(Lyrics(SongLyricsStatus.Published));

        Assert.Multiple(() =>
        {
            Assert.That(probe.Offered, Is.True);
            Assert.That(probe.TimingsUrl, Is.EqualTo("/api/music/abc/abc-lyrics.json?v=4"));
        });
    }

    [TestCase(SongLyricsStatus.NeedsReview)]
    [TestCase(SongLyricsStatus.Pending)]
    [TestCase(SongLyricsStatus.Failed)]
    public void NoOtherStatusIsOffered(SongLyricsStatus status)
    {
        // NeedsReview is now where every successful alignment lands, and its timings sit at exactly
        // the path a published song's would. Only the row distinguishes them.
        var probe = new TrackLyricsProbe();
        probe.SetTrackLyrics(Lyrics(status));

        Assert.Multiple(() =>
        {
            Assert.That(probe.Offered, Is.False);
            Assert.That(probe.TimingsUrl, Is.Null);
        });
    }

    [Test]
    public void ATrackWithNoLyricsAtAllIsNotOffered()
    {
        var probe = new TrackLyricsProbe();
        probe.SetTrackLyrics(null);

        Assert.That(probe.Offered, Is.False);
    }

    [Test]
    public void PublishedWithNoTimingsFileIsNotOffered()
    {
        var probe = new TrackLyricsProbe();
        probe.SetTrackLyrics(Lyrics(SongLyricsStatus.Published, timings: null));

        Assert.That(probe.Offered, Is.False);
    }

    [Test]
    public void MovingBetweenTracksChangesWhatIsOffered()
    {
        // The toggle belongs to the track, not the playlist. A playlist mixing songs that have
        // published lyrics with songs that do not must offer it only on the ones that do - and the
        // URL has to follow, or the second song would show the first song's words.
        var probe = new TrackLyricsProbe();

        probe.SetTrackLyrics(Lyrics(SongLyricsStatus.Published, "one/one-lyrics.json", version: 2));
        var first = probe.TimingsUrl;

        probe.SetTrackLyrics(null);
        var second = probe.Offered;

        probe.SetTrackLyrics(Lyrics(SongLyricsStatus.Published, "two/two-lyrics.json", version: 9));
        var third = probe.TimingsUrl;

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("/api/music/one/one-lyrics.json?v=2"));
            Assert.That(second, Is.False);
            Assert.That(third, Is.EqualTo("/api/music/two/two-lyrics.json?v=9"));
        });
    }
}
