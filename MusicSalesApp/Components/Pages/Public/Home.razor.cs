using MusicSalesApp.Components.Base;

#nullable enable

namespace MusicSalesApp.Components.Pages.Public;

/// <summary>
/// Code-behind for the home page.
///
/// <para>
/// Deliberately empty of data loading. Home renders as static SSR - it declares no
/// <c>@rendermode</c> - so <c>OnAfterRenderAsync</c> is never called on it, and AGENTS.md
/// mandates that hook over <c>OnInitializedAsync</c> for anything touching the DbContext.
/// Everything on this page that needs data is therefore an island: <c>HomeSubscriptionOffer</c>,
/// the embedded <c>MusicLibrary</c>, and <c>HomeUserPlaylists</c>.
/// </para>
///
/// <para>
/// The playlist loading that used to live here moved to <see cref="HomeUserPlaylistsModel"/>
/// unchanged; it had never run in production from this class.
/// </para>
/// </summary>
public partial class HomeModel : BlazorBase
{
}
