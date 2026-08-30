#nullable enable
using MusicSalesApp.Components.Players;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The decisions the "Marquee" redesign added to the playlist page: which of the five routes gets
/// the artist treatment, what the hero chip says, what a track row's small-screen subtitle reads,
/// and how the lyrics/art segmented control behaves.
///
/// <para>
/// <b>Asserted on the model, not on rendered markup, for the reason
/// <see cref="PlaylistPlayerLyricsToggleTests"/> already documents:</b> this component loads in
/// <c>OnAfterRenderAsync</c>, which bUnit does not reliably drive, so the page never reaches its
/// loaded state under test. Two tests in <c>PlaylistPlayerTests</c> and one in
/// <c>PlayerTransportRegressionTests</c> are skipped for exactly that. Moving the load to
/// <c>OnInitializedAsync</c> would make them pass and is deliberately NOT done - <c>AGENTS.md</c>
/// documents <c>OnAfterRenderAsync(firstRender)</c> as the Blazor Server DbContext-threading rule.
/// </para>
///
/// <para>
/// What that leaves genuinely unverified is the wiring between these decisions and the DOM, which
/// needs a browser. The markup they drive is the song player's, which <em>is</em> covered.
/// </para>
/// </summary>
[TestFixture]
public class PlaylistPlayerModeTests
{
    private sealed class ModeProbe : PlaylistPlayerInteractiveModel
    {
        public void SetMode(bool artist = false, bool creator = false, bool genre = false, bool recommended = false, bool topStreamed = false)
        {
            _isArtistMode = artist;
            _isCreatorMode = creator;
            _isGenreMode = genre;
            _isRecommendedMode = recommended;
            _isTopStreamedMode = topStreamed;
        }

        /// <summary>Turns on the period column the four rolling top-streamed pages carry.</summary>
        public void SetPeriodStreamLabel(string? label) => _periodStreamLabel = label;

        public bool ShowsPeriodColumn => ShowPeriodStreamCount();

        public string PeriodColumnLabel => GetPeriodStreamLabel();

        public static string StreamsSummary(int periodCount, string periodLabel, int lifetimeCount, bool showPeriod)
            => FormatStreamsSummary(periodCount, periodLabel, lifetimeCount, showPeriod);

        public void SetTrackLyrics(SongLyrics? lyrics) => _currentTrackLyrics = lyrics;

        public bool ArtistTreatment => IsArtistTreatment();

        public string Label => GetModeLabel();

        public string Subtitle => GetTrackSubtitle(0);

        public bool Showing => ShowingLyrics();

        public void PressLyrics() => ShowLyricsPanel();

        public void PressArt() => ShowArtPanel();
    }

    private static SongLyrics Published() => new()
    {
        SongMetadataId = 1,
        Status = SongLyricsStatus.Published,
        TimingsBlobPath = "abc/abc-lyrics.json",
        Version = 1
    };

    // ---- Which routes are "the artist page" -------------------------------------------

    [TestCase(true, false, Description = "/artist/{name}")]
    [TestCase(false, true, Description = "/creator/{id}")]
    public void ArtistAndCreatorRoutesGetTheArtistTreatment(bool artist, bool creator)
    {
        // These two are the only artist page StreamTunes has, so the persona takes the hero and the
        // track list drops its Artist column - every row is the same person.
        var probe = new ModeProbe();
        probe.SetMode(artist: artist, creator: creator);

        Assert.That(probe.ArtistTreatment, Is.True);
    }

    [Test]
    public void GenreRecommendedAndPlaylistRoutesDoNot()
    {
        Assert.Multiple(() =>
        {
            var genre = new ModeProbe();
            genre.SetMode(genre: true);
            Assert.That(genre.ArtistTreatment, Is.False, "/genre lists many artists.");

            var recommended = new ModeProbe();
            recommended.SetMode(recommended: true);
            Assert.That(recommended.ArtistTreatment, Is.False);

            var topStreamed = new ModeProbe();
            topStreamed.SetMode(topStreamed: true);
            Assert.That(topStreamed.ArtistTreatment, Is.False, "/top-streamed lists many artists.");

            var playlist = new ModeProbe();
            playlist.SetMode();
            Assert.That(playlist.ArtistTreatment, Is.False, "No flag set is the /playlist fallback.");
        });
    }

    [Test]
    public void EachRouteNamesItselfInTheHeroChip()
    {
        var genre = new ModeProbe(); genre.SetMode(genre: true);
        var artist = new ModeProbe(); artist.SetMode(artist: true);
        var creator = new ModeProbe(); creator.SetMode(creator: true);
        var recommended = new ModeProbe(); recommended.SetMode(recommended: true);
        var topStreamed = new ModeProbe(); topStreamed.SetMode(topStreamed: true);
        var playlist = new ModeProbe(); playlist.SetMode();

        Assert.Multiple(() =>
        {
            Assert.That(genre.Label, Is.EqualTo("Genre"));
            Assert.That(artist.Label, Is.EqualTo("Artist"));
            Assert.That(creator.Label, Is.EqualTo("Artist"), "A creator is presented as an artist.");
            Assert.That(recommended.Label, Is.EqualTo("For you"));
            Assert.That(topStreamed.Label, Is.EqualTo("Most streamed"));
            Assert.That(playlist.Label, Is.EqualTo("Playlist"));
        });
    }

    // ---- The small-screen row subtitle ------------------------------------------------

    // ---- The second stream-count column ------------------------------------------------

    [Test]
    public void ARollingTopStreamedPageCarriesThePeriodColumn()
    {
        // The list is ranked on streams inside the period, but the live counter beside it is the
        // LIFETIME total. Without this column a correctly ordered "Top 10 Today" reads as mis-sorted.
        var probe = new ModeProbe();
        probe.SetMode(topStreamed: true);
        probe.SetPeriodStreamLabel("Today");

        Assert.Multiple(() =>
        {
            Assert.That(probe.ShowsPeriodColumn, Is.True);
            Assert.That(probe.PeriodColumnLabel, Is.EqualTo("Today"));
        });
    }

    [Test]
    public void TheAllTimePageDoesNotCarryThePeriodColumn()
    {
        // There the ranking number and the lifetime counter are the same figure, so a second column
        // would only repeat the first. The all-time descriptor carries a null label to say so.
        var probe = new ModeProbe();
        probe.SetMode(topStreamed: true);
        probe.SetPeriodStreamLabel(null);

        Assert.That(probe.ShowsPeriodColumn, Is.False);
    }

    [Test]
    public void NoOtherRouteCarriesThePeriodColumn()
    {
        Assert.Multiple(() =>
        {
            foreach (var (name, probe) in new (string, ModeProbe)[]
            {
                ("genre", Probe(p => p.SetMode(genre: true))),
                ("artist", Probe(p => p.SetMode(artist: true))),
                ("creator", Probe(p => p.SetMode(creator: true))),
                ("recommended", Probe(p => p.SetMode(recommended: true))),
                ("playlist", Probe(p => p.SetMode()))
            })
            {
                Assert.That(probe.ShowsPeriodColumn, Is.False, $"/{name} has no period of its own.");
            }
        });
    }

    private static ModeProbe Probe(Action<ModeProbe> configure)
    {
        var probe = new ModeProbe();
        configure(probe);
        return probe;
    }

    // ---- The small-screen stream summary -------------------------------------------------

    [Test]
    public void TheSmallScreenSummaryNamesBothCounts()
    {
        // Below 992px the Streams columns are hidden, so without this a phone sees none of the
        // numbers the playlist is ranked by.
        Assert.That(
            ModeProbe.StreamsSummary(42, "Today", 5001, showPeriod: true),
            Is.EqualTo("42 today · 5,001 all time"));
    }

    [Test]
    public void TheSmallScreenSummaryFallsBackToOneCountWithoutAPeriod()
    {
        // The all-time playlist ranks on the lifetime counter, so there is no second number to name.
        Assert.That(
            ModeProbe.StreamsSummary(0, string.Empty, 5001, showPeriod: false),
            Is.EqualTo("5,001 streams"));
    }

    [Test]
    public void TheSmallScreenSummaryGroupsThousands()
    {
        // A raw 1234567 in a 12.5px line under a phone-width title is unreadable.
        Assert.That(
            ModeProbe.StreamsSummary(1234, "This Week", 1234567, showPeriod: true),
            Is.EqualTo("1,234 this week · 1,234,567 all time"));
    }

    [Test]
    public void TheRowSubtitleDropsTheArtistOnAnArtistPage()
    {
        // Below 992px the Artist, Genre and Streams columns are hidden and this line carries their
        // content instead - that is how the table sheds columns rather than shrinking to 8px type.
        // On an artist page repeating the artist on every row would be noise.
        var catalogue = new ModeProbe();
        catalogue.SetMode(genre: true);

        var artist = new ModeProbe();
        artist.SetMode(artist: true);

        Assert.Multiple(() =>
        {
            Assert.That(catalogue.Subtitle, Does.Contain(ArtistDisplayNames.UnknownArtist));
            Assert.That(catalogue.Subtitle, Does.Contain("·"), "Artist and genre, separated.");
            Assert.That(artist.Subtitle, Does.Not.Contain(ArtistDisplayNames.UnknownArtist));
            Assert.That(artist.Subtitle, Does.Not.Contain("·"), "Genre alone.");
        });
    }

    // ---- The segmented lyrics/art control ---------------------------------------------

    [Test]
    public void TheStageShowsLyricsWheneverTheTrackHasThem()
    {
        // Opening on art would show the same image twice, since the hero already carries it.
        var probe = new ModeProbe();
        probe.SetTrackLyrics(Published());
        probe.PressLyrics();

        Assert.That(probe.Showing, Is.True);
    }

    [Test]
    public void PressingArtSwapsToTheArtwork()
    {
        var probe = new ModeProbe();
        probe.SetTrackLyrics(Published());
        probe.PressLyrics();

        probe.PressArt();

        Assert.That(probe.Showing, Is.False);
    }

    [Test]
    public void PressingTheSegmentThatIsAlreadyActiveChangesNothing()
    {
        // The two buttons SET a state rather than flipping one. Wiring both to a toggle - the
        // obvious way to build this out of the old single-button markup - makes clicking the lit
        // segment switch away from it, which reads as the control being broken.
        var probe = new ModeProbe();
        probe.SetTrackLyrics(Published());
        probe.PressLyrics();

        probe.PressLyrics();

        Assert.That(probe.Showing, Is.True);
    }

    [Test]
    public void ATrackWithNoPublishedLyricsNeverShowsTheLyricsPanel()
    {
        // The playlist moves between songs that have lyrics and songs that do not, so the flag can
        // outlive the track that set it. ShowingLyrics() has to re-check, or the panel would sit on
        // its empty message after advancing to a track with none.
        var probe = new ModeProbe();
        probe.PressLyrics();

        probe.SetTrackLyrics(null);

        Assert.That(probe.Showing, Is.False);
    }
}
