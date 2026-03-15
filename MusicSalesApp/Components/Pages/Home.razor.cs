using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages;

public partial class HomeModel : BlazorBase
{
    protected string _subscriptionPrice = "3.99";
    protected bool _hasActiveSubscription = false;
    protected bool _isAuthenticated = false;
    protected List<RecommendedPlaylist> _recommendedPlaylist = new();
    protected Playlist _likedSongsPlaylist = null;
    protected int _likedSongsCount = 0;
    protected bool _loadingRecommendations = false;
    protected int _currentUserId;
    protected bool _isActiveCreator = false;

    protected bool HasUserPlaylists => _isAuthenticated && 
        (_recommendedPlaylist.Any() || (_likedSongsPlaylist != null && _likedSongsCount > 0));

    protected bool HasLikedSongsToShow => _likedSongsPlaylist != null && _likedSongsCount > 0;

    protected override async Task OnInitializedAsync()
    {
        await LoadSubscriptionPriceAsync();

        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            _isAuthenticated = true;
            var appUser = await UserManager.GetUserAsync(authState.User);
            if (appUser != null)
            {
                _currentUserId = appUser.Id;
            }
            await LoadSubscriptionStatusAsync();
            await LoadRecommendedPlaylistAsync();
            await LoadLikedSongsPlaylistAsync();
            await LoadCreatorStatusAsync();
        }
    }

    private async Task LoadSubscriptionStatusAsync()
    {
        try
        {
            if (_currentUserId > 0)
            {
                _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(_currentUserId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retrieve subscription status.");
        }
    }

    private async Task LoadSubscriptionPriceAsync()
    {
        try
        {
            var price = await AppSettingsService.GetSubscriptionPriceAsync();
            _subscriptionPrice = price.ToString("F2");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load subscription price from database.");
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

    private async Task LoadCreatorStatusAsync()
    {
        if (_currentUserId == 0) return;

        try
        {
            _isActiveCreator = await CreatorService.IsActiveCreatorAsync(_currentUserId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to check creator status for user {UserId}", _currentUserId);
        }
    }
}
