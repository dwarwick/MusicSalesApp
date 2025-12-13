using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages;

public partial class MyPlaylistsModel : BlazorBase
{
    protected List<Playlist> _playlists;
    protected Dictionary<int, int> _playlistSongCounts = new();
    protected List<UserPlaylist> _playlistSongs;
    protected List<OwnedSong> _availableSongs;
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
    protected bool _hasOwnedSongs = false;
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
                    var appUser = await UserManager.GetUserAsync(user);
                    if (appUser != null)
                    {
                        _currentUserId = appUser.Id;
                        
                        // Check if user has active subscription
                        _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(_currentUserId);
                        
                        // Check if user owns any songs (needed for creating playlists)
                        var ownedSongs = await CartService.GetOwnedSongsAsync(_currentUserId);
                        _hasOwnedSongs = ownedSongs.Any();
                        
                        await LoadPlaylists();
                    }
                }
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

    protected void ShowCreatePlaylistDialog()
    {
        // Users can create playlists if they have a subscription OR own at least one song
        if (!_hasActiveSubscription && !_hasOwnedSongs)
        {
            _error = "To create playlists, you need to either have an active subscription or own at least one song. Subscribe for unlimited access or purchase songs to get started.";
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
                await PlaylistService.DeletePlaylistAsync(_playlistToDelete.Id, _currentUserId);
                await LoadPlaylists();
                CloseDeleteDialog();
                
                // If we were viewing this playlist, close the view
                if (_selectedPlaylist?.Id == _playlistToDelete.Id)
                {
                    ClosePlaylistView();
                }
                
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

    protected async Task AddSongToPlaylist(int ownedSongId)
    {
        try
        {
            var success = await PlaylistService.AddSongToPlaylistAsync(_currentUserId, _selectedPlaylist.Id, ownedSongId);
            
            if (success)
            {
                // Reload playlist songs
                _playlistSongs = await PlaylistService.GetPlaylistSongsAsync(_selectedPlaylist.Id);
                
                // Update the song count
                _playlistSongCounts[_selectedPlaylist.Id] = _playlistSongs.Count;
                
                // Remove the added song from available songs
                _availableSongs = _availableSongs?.Where(s => s.Id != ownedSongId).ToList();
                
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
    {
        if (!string.IsNullOrEmpty(songMetadata.Mp3BlobPath))
        {
            return Path.GetFileNameWithoutExtension(songMetadata.Mp3BlobPath);
        }
        if (!string.IsNullOrEmpty(songMetadata.BlobPath))
        {
            return Path.GetFileNameWithoutExtension(songMetadata.BlobPath);
        }
        return "Unknown Song";
    }

    protected void PlayPlaylist(Playlist playlist)
    {
        NavigationManager.NavigateTo($"/playlist/{playlist.Id}");
    }
}
