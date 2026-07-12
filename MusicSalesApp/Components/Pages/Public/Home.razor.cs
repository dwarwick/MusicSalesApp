using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

#nullable enable

namespace MusicSalesApp.Components.Pages.Public;

public partial class HomeModel : BlazorBase
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
                var appUser = await UserManager.GetUserAsync(authState.User);
                if (appUser != null)
                {
                    _currentUserId = appUser.Id;
                }

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
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadRecommendedPlaylistAsync()
    {
        if (_currentUserId == 0) return;

        try
        {
            _loadingRecommendations = true;
            _recommendedPlaylist = await RecommendationService.GetRecommendedPlaylistAsync(_currentUserId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load recommended playlist for user {UserId}", _currentUserId);
        }
        finally
        {
            _loadingRecommendations = false;
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
