using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages.Public;

public partial class MyPlaylistsModel : BlazorBase
{
    protected List<Playlist> _playlists;
    protected Dictionary<int, int> _playlistSongCounts = new();
    protected List<UserPlaylist> _playlistSongs;
    protected List<SongMetadata> _availableSongs;
    protected List<RecommendedPlaylist> _recommendedPlaylist = new();

    /// <summary>
    /// Song count per top-streamed window key. A window with no songs is absent and renders no card.
    /// </summary>
    protected Dictionary<string, int> _topStreamedCounts = new();
    protected Playlist _selectedPlaylist;
    protected Playlist _editingPlaylist;
    protected Playlist _playlistToDelete;
    protected string _playlistName;
    protected string _error;
    protected bool _loading = true;
    protected bool _showPlaylistDialog = false;
    protected bool _showDeleteDialog = false;
    protected bool _showAddSongDialog = false;
    protected bool _viewingSongs = false;
    protected int _currentUserId;
    protected bool _hasActiveSubscription = false;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    var userIdInt = GetUserId(user);
                    if (userIdInt.HasValue)
                    {
                        _currentUserId = userIdInt.Value;
                        
                        // Check if user has active subscription
                        _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(_currentUserId);
                        
                        await LoadPlaylists();
                        await LoadRecommendedPlaylist();
                    }
                }

                // Outside the authenticated block on purpose: these five are the same for every
                // visitor, so they do not depend on having resolved a user id.
                await LoadTopStreamedPlaylistsAsync();
            }
            catch (Exception ex)
            {
                _error = $"Error loading playlists: {ex.Message}";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task LoadPlaylists()
    {
        _playlists = await PlaylistService.GetUserPlaylistsAsync(_currentUserId);
        
        // Load song counts for each playlist
        _playlistSongCounts.Clear();
        foreach (var playlist in _playlists)
        {
            var songs = await PlaylistService.GetPlaylistSongsAsync(playlist.Id);
            _playlistSongCounts[playlist.Id] = songs.Count;
        }
    }

    /// <summary>
    /// The five global "most streamed" playlists. Not personal, so no user id is involved.
    /// </summary>
    private async Task LoadTopStreamedPlaylistsAsync()
    {
        try
        {
            _topStreamedCounts = await TopStreamedPlaylistService.GetCountsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading the most-streamed playlists");
            _topStreamedCounts = new Dictionary<string, int>();
        }
    }

    /// <summary>Only the playlists that have songs, in Day, Week, Month, Year, All Time order.</summary>
    protected IReadOnlyList<TopStreamedWindowDescriptor> VisibleTopStreamedPlaylists =>
        TopStreamedPlaylists.All
            .OrderBy(descriptor => descriptor.DisplayOrder)
            .Where(descriptor => _topStreamedCounts.ContainsKey(descriptor.Window))
            .ToList();

    protected int TopStreamedSongCount(TopStreamedWindowDescriptor descriptor) =>
        _topStreamedCounts.TryGetValue(descriptor.Window, out var count) ? count : 0;

    protected void PlayTopStreamedPlaylist(TopStreamedWindowDescriptor descriptor)
    {
        NavigationManager.NavigateTo($"/top-streamed/{descriptor.Window}");
    }

    private async Task LoadRecommendedPlaylist()
    {
        try
        {
            _recommendedPlaylist = await RecommendationService.GetRecommendedPlaylistAsync(_currentUserId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading recommended playlist for user {UserId}", _currentUserId);
            // Don't show error to user, just don't display recommended playlist
            _recommendedPlaylist = new List<RecommendedPlaylist>();
        }
    }

    protected void PlayRecommendedPlaylist()
    {
        NavigationManager.NavigateTo($"/recommended-playlist/{_currentUserId}");
    }

    protected void ShowCreatePlaylistDialog()
    {
        // Users can create playlists if they have a subscription
        if (!_hasActiveSubscription)
        {
            _error = "To create playlists, you need an active subscription. Subscribe for unlimited access!";
            return;
        }
        
        _editingPlaylist = null;
        _playlistName = string.Empty;
        _showPlaylistDialog = true;
    }

    protected void ShowEditPlaylistDialog(Playlist playlist)
    {
        _editingPlaylist = playlist;
        _playlistName = playlist.PlaylistName;
        _showPlaylistDialog = true;
    }

    protected void ClosePlaylistDialog()
    {
        _showPlaylistDialog = false;
        _editingPlaylist = null;
        _playlistName = string.Empty;
    }

    protected async Task SavePlaylist()
    {
        if (string.IsNullOrWhiteSpace(_playlistName))
        {
            _error = "Playlist name is required";
            return;
        }

        try
        {
            if (_editingPlaylist == null)
            {
                // Create new playlist
                await PlaylistService.CreatePlaylistAsync(_currentUserId, _playlistName);
            }
            else
            {
                // Update existing playlist
                await PlaylistService.UpdatePlaylistAsync(_editingPlaylist.Id, _currentUserId, _playlistName);
            }

            await LoadPlaylists();
            ClosePlaylistDialog();
            _error = null;
        }
        catch (Exception ex)
        {
            _error = $"Error saving playlist: {ex.Message}";
        }
    }

    protected void ShowDeletePlaylistDialog(Playlist playlist)
    {
        _playlistToDelete = playlist;
        _showDeleteDialog = true;
    }

    protected void CloseDeleteDialog()
    {
        _showDeleteDialog = false;
        _playlistToDelete = null;
    }

    protected async Task ConfirmDeletePlaylist()
    {
        if (_playlistToDelete != null)
        {
            try
            {
                var deletedPlaylistId = _playlistToDelete.Id;
                
                await PlaylistService.DeletePlaylistAsync(deletedPlaylistId, _currentUserId);
                await LoadPlaylists();
                
                // If we were viewing this playlist, close the view
                if (_selectedPlaylist?.Id == deletedPlaylistId)
                {
                    ClosePlaylistView();
                }
                
                CloseDeleteDialog();
                _error = null;
            }
            catch (Exception ex)
            {
                _error = $"Error deleting playlist: {ex.Message}";
            }
        }
    }

    protected async Task ViewPlaylist(Playlist playlist)
    {
        try
        {
            _selectedPlaylist = playlist;
            _playlistSongs = await PlaylistService.GetPlaylistSongsAsync(playlist.Id);
            _viewingSongs = true;
            _error = null;
        }
        catch (Exception ex)
        {
            _error = $"Error loading playlist songs: {ex.Message}";
        }
    }

    protected void ClosePlaylistView()
    {
        _selectedPlaylist = null;
        _playlistSongs = null;
        _viewingSongs = false;
    }

    protected async Task ShowAddSongDialog()
    {
        try
        {
            // Use the service method to get available songs
            _availableSongs = await PlaylistService.GetAvailableSongsForPlaylistAsync(_currentUserId, _selectedPlaylist.Id);
            _showAddSongDialog = true;
        }
        catch (Exception ex)
        {
            _error = $"Error loading available songs: {ex.Message}";
        }
    }

    protected void CloseAddSongDialog()
    {
        _showAddSongDialog = false;
        _availableSongs = null;
    }

    protected async Task AddSongToPlaylist(int songMetadataId)
    {
        try
        {
            var success = await PlaylistService.AddSongToPlaylistAsync(_currentUserId, _selectedPlaylist.Id, songMetadataId);
            
            if (success)
            {
                // Reload playlist songs
                _playlistSongs = await PlaylistService.GetPlaylistSongsAsync(_selectedPlaylist.Id);
                
                // Update the song count
                _playlistSongCounts[_selectedPlaylist.Id] = _playlistSongs.Count;
                
                // Remove the added song from available songs
                _availableSongs = _availableSongs?.Where(s => s.Id != songMetadataId).ToList();
                
                _error = null;
            }
            else
            {
                _error = "Failed to add song to playlist";
            }
        }
        catch (Exception ex)
        {
            _error = $"Error adding song to playlist: {ex.Message}";
        }
    }

    protected async Task RemoveSongFromPlaylist(UserPlaylist userPlaylist)
    {
        try
        {
            var success = await PlaylistService.RemoveSongFromPlaylistAsync(
                _selectedPlaylist.Id, 
                userPlaylist.Id, 
                _currentUserId);
            
            if (success)
            {
                // Reload playlist songs
                _playlistSongs = await PlaylistService.GetPlaylistSongsAsync(_selectedPlaylist.Id);
                
                // Update the song count
                _playlistSongCounts[_selectedPlaylist.Id] = _playlistSongs.Count;
                
                _error = null;
            }
            else
            {
                _error = "Failed to remove song from playlist";
            }
        }
        catch (Exception ex)
        {
            _error = $"Error removing song from playlist: {ex.Message}";
        }
    }

    protected string GetSongTitle(SongMetadata songMetadata)
        => songMetadata == null
            ? "Unknown Song"
            : SongTitleHelper.GetEffectiveTitle(
                songMetadata.SongTitle, songMetadata.Mp3BlobPath, songMetadata.BlobPath);

    protected void PlayPlaylist(Playlist playlist)
    {
        NavigationManager.NavigateTo($"/playlist/{playlist.Id}");
    }

    protected string GetPlaylistCardClass(Playlist playlist)
    {
        var baseClass = "playlists-page-card music-card";
        if (playlist.IsSystemGenerated)
        {
            return $"{baseClass} liked-songs-playlist-card";
        }
        return baseClass;
    }
}
