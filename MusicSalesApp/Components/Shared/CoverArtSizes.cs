#nullable enable

namespace MusicSalesApp.Components.Shared;

/// <summary>
/// The <c>sizes</c> attribute for each surface that renders cover art.
///
/// <para>
/// <c>sizes</c> tells the browser how wide the image will actually be <em>before</em> layout runs,
/// which is what lets it pick a rendition from <c>srcset</c> on the first pass. Get it wrong and it
/// either downloads more than it needs or renders something soft.
/// </para>
///
/// <para>
/// These values track the five stylesheets loaded in <c>App.razor</c>. They are ordered
/// <c>xl_app.css</c> (min-width:1200) then <c>lg</c> (max:1200), <c>md</c> (max:992),
/// <c>sm</c> (max:768), <c>xs</c> (max:576) - all at the same specificity, so for any viewport the
/// <b>last</b> matching sheet wins, i.e. the smallest breakpoint that still matches.
/// </para>
/// </summary>
public static class CoverArtSizes
{
    /// <summary>
    /// The library grid (<c>.music-cards-grid</c>). The art is fluid: <c>.card-album-art</c> is
    /// <c>width:100%; aspect-ratio:1</c> inside a card whose width comes from the grid track -
    /// <c>minmax(210px,1fr)</c> by default and <c>minmax(150px,1fr)</c> at 768. The vw values
    /// approximate the track width net of the gap and card padding.
    ///
    /// <para>
    /// The 576 stop is back to <c>92vw</c>. It read a fixed <c>84px</c> while the grid card was
    /// horizontal with its art in a small left column; that layout is gone and the art now runs
    /// the full width of a single-column card again (<c>xs_app.css</c>, "THE ARTWORK IS THE
    /// CARD"). Leaving it at 84px fetches a thumbnail rendition for a ~370px box, which renders
    /// visibly soft - it does not error.
    /// </para>
    /// </summary>
    public const string Card =
        "(max-width:576px) 92vw, (max-width:768px) 46vw, (max-width:992px) 31vw, 210px";

    /// <summary>
    /// The home / signup featured carousel (<c>.featured-music-carousel</c>), which keeps the tall
    /// square card at every width - so it needs its own ladder, not <see cref="Card"/>.
    ///
    /// <para>
    /// Tracks the "show ~N cards" widths in the breakpoint sheets: 220px fixed at xl, then
    /// <c>100vw/4.2</c>, <c>/3.2</c>, <c>/2.2</c> and <c>/1.2</c> as the viewport narrows.
    /// </para>
    /// </summary>
    public const string CardCarousel =
        "(max-width:576px) 83vw, (max-width:768px) 45vw, (max-width:992px) 31vw, (max-width:1200px) 24vw, 220px";

    /// <summary>
    /// The song player's hero artwork (<c>.song-hero-art</c>).
    ///
    /// <para>
    /// This is the small identity thumbnail in the hero band, not the main display surface - see
    /// <see cref="PlayerStage"/> for that. The ladder reads 118 - 140 - 176 - 156 - 168 and tracks
    /// <c>.song-hero-art</c> across the five breakpoint sheets. Keep the two in step: change one
    /// without the other and the browser fetches the wrong rendition, which shows up as a
    /// visibly soft hero rather than as an error.
    /// </para>
    /// </summary>
    public const string PlayerHero =
        "(max-width:576px) 118px, (max-width:768px) 140px, (max-width:992px) 176px, (max-width:1200px) 156px, 168px";

    /// <summary>
    /// The song player's stage panel artwork (<c>.song-stage-art</c>) - what the panel shows when
    /// the listener flips the toggle to Art, and what it shows outright for the many songs that
    /// have no published lyrics.
    /// </summary>
    public const string PlayerStage =
        "(max-width:576px) 300px, (max-width:768px) 320px, (max-width:992px) 300px, 250px";

    /// <summary>
    /// The blurred hero backdrop (<c>.song-hero-backdrop-img</c>).
    ///
    /// <para>
    /// Deliberately tiny. The image is scaled up and put behind a ~50px blur, so a large rendition
    /// buys nothing a 160px one does not - the blur destroys the detail either way - while costing
    /// a full-size download on every song page. Do not "fix" this to match the element's rendered
    /// width.
    /// </para>
    /// </summary>
    public const string PlayerBackdrop = "160px";

    /// <summary>
    /// The playlist player's hero artwork (<c>.playlist-player-container .player-art-shell</c>) -
    /// the current track's cover, or the persona portrait on the artist and creator routes.
    ///
    /// <para>
    /// The ladder reads 96 - 128 - 150 - 156 - 168 and descends cleanly, unlike the one it
    /// replaced, which bulged to 225px at the medium breakpoint because the old tablet layout
    /// stacked the art larger than the desktop one did. Keep this in step with
    /// <c>.playlist-player-container .player-art-shell</c> across the five breakpoint sheets.
    /// </para>
    /// </summary>
    public const string PlaylistPlayerHero =
        "(max-width:576px) 96px, (max-width:768px) 128px, (max-width:992px) 150px, (max-width:1200px) 156px, 168px";

    /// <summary>Playlist track rows: 40x40 at every breakpoint.</summary>
    public const string TrackThumbnail = "40px";

    /// <summary>A fixed pixel width, for the management grids and edit dialogs.</summary>
    public static string Fixed(int cssPixels) => $"{cssPixels}px";
}
