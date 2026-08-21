using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

#nullable enable

namespace MusicSalesApp.Components.Pages.Public;

/// <summary>
/// Code-behind for the home page's "Your Playlists" island.
///
/// <para>
/// Lifted verbatim out of <see cref="HomeModel"/>. Nothing about the loading logic changed - the
/// bug it fixes was purely about WHERE it lived: <c>Home.razor</c> is static SSR, so its
/// <c>OnAfterRenderAsync</c> was never invoked and none of this ever ran in production.
/// </para>
/// </summary>
public partial class HomeUserPlaylistsModel : BlazorBase
{
    private bool _hasLoadedData;
    protected bool _isAuthenticated;
    protected List<RecommendedPlaylist> _recommendedPlaylist = new();
    protected Playlist _likedSongsPlaylist = null!;
    protected int _likedSongsCount;
    protected bool _loadingRecommendations;
    protected int _currentUserId;

    protected bool HasUserPlaylists => _isAuthenticated &&
        (_recommendedPlaylist.Any() || (_likedSongsPlaylist != null && _likedSongsCount > 0));

    protected bool HasLikedSongsToShow => _likedSongsPlaylist != null && _likedSongsCount > 0;

    /// <summary>
    /// AGENTS.md mandates this hook over <c>OnInitializedAsync</c> for anything touching the
    /// DbContext - see "Solution: Use OnAfterRenderAsync with firstRender". That rule is why this
    /// section had to become an island rather than simply moving the load earlier.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData)
        {
            return;
        }

        _hasLoadedData = true;
        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            if (authState.User?.Identity?.IsAuthenticated == true)
            {
                _isAuthenticated = true;
                // Claims, not UserManager.GetUserAsync. The id is already a claim on the cookie
                // principal, and GetUserAsync is a DB round-trip on the circuit's SINGLE scoped
                // AppDbContext. On a cold circuit (hard refresh) every island first-renders in
                // one batch, and two in-flight UserManager calls throw "a second operation was
                // started on this context" - which the catch below then swallows, so the section
                // silently renders nothing. Warm circuits (enhanced nav) stagger the calls and
                // usually get away with it, which is exactly the works-on-click, dies-on-refresh
                // flakiness this section showed. Same pattern as MusicLibrary.razor.cs.
                _currentUserId = GetUserId(authState.User) ?? 0;

                // Paint the loading state before the round-trips rather than after. Previously
                // every flag settled inside one hook with a single StateHasChanged at the end, so
                // the "Loading your personalized content..." branch was unreachable by construction.
                _loadingRecommendations = true;
                await InvokeAsync(StateHasChanged);

                await LoadRecommendedPlaylistAsync();
                await LoadLikedSongsPlaylistAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load personalized home page content.");
        }
        finally
        {
            _loadingRecommendations = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadRecommendedPlaylistAsync()
    {
        if (_currentUserId == 0) return;

        try
        {
            _recommendedPlaylist = await RecommendationService.GetRecommendedPlaylistAsync(_currentUserId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load recommended playlist for user {UserId}", _currentUserId);
        }
    }

    private async Task LoadLikedSongsPlaylistAsync()
    {
        if (_currentUserId == 0) return;

        try
        {
            _likedSongsPlaylist = await PlaylistService.GetOrCreateLikedSongsPlaylistAsync(_currentUserId);
            var playlistSongs = await PlaylistService.GetPlaylistSongsAsync(_likedSongsPlaylist.Id);
            _likedSongsCount = playlistSongs.Count;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load Liked Songs playlist for user {UserId}", _currentUserId);
        }
    }
}
