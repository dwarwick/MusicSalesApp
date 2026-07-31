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
    /// Library grid and home carousel. The art is fluid: <c>.card-album-art</c> is
    /// <c>width:100%; aspect-ratio:1</c> inside a card whose width comes from the grid track -
    /// <c>minmax(210px,1fr)</c> by default, <c>minmax(150px,1fr)</c> at 768, and a single full-width
    /// column at 576. The vw values approximate the track width net of the gap and card padding.
    /// </summary>
    public const string Card =
        "(max-width:576px) 92vw, (max-width:768px) 46vw, (max-width:992px) 31vw, 210px";

    /// <summary>
    /// The song player's hero artwork (<c>.playlist-art</c>).
    ///
    /// <para>
    /// Note the 225px band. It is not a typo: <c>md_app.css</c> sets <c>.playlist-art</c> to 225px,
    /// which is <em>larger</em> than the 140px it gets on a desktop monitor, so the ladder across
    /// breakpoints reads 85 - 95 - 225 - 130 - 140. Spelling it out here is what stops tablets from
    /// under-fetching and rendering a visibly soft hero.
    /// </para>
    /// </summary>
    public const string PlayerHero =
        "(max-width:576px) 85px, (max-width:768px) 95px, (max-width:992px) 225px, (max-width:1200px) 130px, 140px";

    /// <summary>
    /// The playlist player's hero artwork. Identical to <see cref="PlayerHero"/> except at the
    /// smallest breakpoint, where <c>.playlist-player-container .playlist-art</c> wins on
    /// specificity and pins it to 70px.
    /// </summary>
    public const string PlaylistPlayerHero =
        "(max-width:576px) 70px, (max-width:768px) 95px, (max-width:992px) 225px, (max-width:1200px) 130px, 140px";

    /// <summary>Playlist track rows: 40x40 at every breakpoint.</summary>
    public const string TrackThumbnail = "40px";

    /// <summary>A fixed pixel width, for the management grids and edit dialogs.</summary>
    public static string Fixed(int cssPixels) => $"{cssPixels}px";
}
