using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages;

public partial class CreatorSongManagementModel : BlazorBase
{
    protected bool _loading = true;
    protected string _errorMessage = string.Empty;
    protected string _successMessage = string.Empty;
    protected List<SongAdminViewModel> _songs = new();
    protected SfGrid<SongAdminViewModel> _grid;

    // Delete dialog
    protected bool _showDeleteDialog = false;
    protected SongAdminViewModel _songToDelete;
    protected bool _isDeleting = false;

    // Edit dialog
    protected bool _showEditDialog = false;
    protected SongAdminViewModel _editingSong;
    protected string _editGenre = string.Empty;
    protected string _editSongTitle = string.Empty;
    protected string _editAlbumName = string.Empty;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;

    private int? _creatorId;
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
                        // Get the creator ID for this user
                        _creatorId = await CreatorService.GetCreatorIdForUserAsync(appUser.Id);

                        if (_creatorId.HasValue)
                        {
                            await LoadSongsAsync();
                        }
                        else
                        {
                            _errorMessage = "You are not registered as a creator. Please complete creator onboarding first.";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load songs: {ex.Message}";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadSongsAsync()
    {
        if (!_creatorId.HasValue)
            return;

        var creatorSongs = await CreatorService.GetCreatorSongsAsync(_creatorId.Value);
        
        _songs = creatorSongs.Select(m => new SongAdminViewModel
        {
            Id = m.Id.ToString(),
            AlbumName = m.AlbumName ?? string.Empty,
            SongTitle = GetSongTitleFromMetadata(m),
            Mp3FileName = m.Mp3BlobPath ?? (m.FileExtension == ".mp3" ? m.BlobPath : string.Empty),
            JpegFileName = m.IsAlbumCover ? string.Empty : (m.ImageBlobPath ?? ((m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") ? m.BlobPath : string.Empty)),
            AlbumCoverBlobName = m.IsAlbumCover ? (m.ImageBlobPath ?? m.BlobPath) : string.Empty,
            IsAlbum = m.IsAlbumCover,
            Genre = m.Genre ?? string.Empty,
            TrackNumber = m.TrackNumber,
            TrackLength = m.TrackLength,
            DisplayOnHomePage = m.DisplayOnHomePage,
            HasAlbumCover = m.IsAlbumCover,
            CreatorId = m.CreatorId,
            IsActive = m.IsActive,
            NumberOfStreams = m.NumberOfStreams
        }).ToList();

        // Generate SAS URLs for images
        foreach (var song in _songs)
        {
            if (!string.IsNullOrEmpty(song.JpegFileName))
            {
                song.SongImageUrl = AzureStorageService.GetReadSasUri(song.JpegFileName, TimeSpan.FromHours(1)).ToString();
            }
            if (!string.IsNullOrEmpty(song.AlbumCoverBlobName))
            {
                song.AlbumCoverImageUrl = AzureStorageService.GetReadSasUri(song.AlbumCoverBlobName, TimeSpan.FromHours(1)).ToString();
            }
        }
    }

    /// <summary>
    /// Extracts the song title from metadata. Prefers the stored SongTitle,
    /// but falls back to extracting from file path if not set.
    /// Priority: SongTitle > Mp3BlobPath > ImageBlobPath > BlobPath (deprecated)
    /// </summary>
    private static string GetSongTitleFromMetadata(SongMetadata metadata)
    {
        // Prefer the stored SongTitle if set
        if (!string.IsNullOrEmpty(metadata.SongTitle))
        {
            return metadata.SongTitle;
        }

        // Try MP3 path first (most common for songs)
        if (!string.IsNullOrEmpty(metadata.Mp3BlobPath))
        {
            return System.IO.Path.GetFileNameWithoutExtension(metadata.Mp3BlobPath);
        }
        
        // Then try image path (for album covers)
        if (!string.IsNullOrEmpty(metadata.ImageBlobPath))
        {
            return System.IO.Path.GetFileNameWithoutExtension(metadata.ImageBlobPath);
        }
        
        // Fall back to deprecated BlobPath
        if (!string.IsNullOrEmpty(metadata.BlobPath))
        {
            return System.IO.Path.GetFileNameWithoutExtension(metadata.BlobPath);
        }

        return "Unknown";
    }

    protected void NavigateToUpload()
    {
        NavigationManager.NavigateTo("/upload-files");
    }

    protected void ShowDeleteConfirmation(SongAdminViewModel song)
    {
        _songToDelete = song;
        _showDeleteDialog = true;
    }

    protected void CancelDelete()
    {
        _songToDelete = null;
        _showDeleteDialog = false;
    }

    protected async Task ConfirmDelete()
    {
        if (_songToDelete == null || !_creatorId.HasValue)
            return;

        _isDeleting = true;

        try
        {
            if (int.TryParse(_songToDelete.Id, out var songMetadataId))
            {
                var success = await CreatorService.DeleteCreatorSongAsync(songMetadataId, _creatorId.Value);
                
                if (success)
                {
                    _successMessage = $"'{_songToDelete.SongTitle}' has been deleted successfully.";
                    await LoadSongsAsync();
                }
                else
                {
                    _errorMessage = "Failed to delete the song. Please try again.";
                }
            }
            else
            {
                _errorMessage = "Invalid song ID.";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error deleting song: {ex.Message}";
        }
        finally
        {
            _isDeleting = false;
            _showDeleteDialog = false;
            _songToDelete = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void EditSong(SongAdminViewModel song)
    {
        _editingSong = song;
        _editGenre = song.Genre;
        _editSongTitle = song.SongTitle;
        _editAlbumName = song.AlbumName;
        _validationErrors.Clear();
        _showEditDialog = true;
    }

    protected void CancelEdit()
    {
        _editingSong = null;
        _showEditDialog = false;
        _validationErrors.Clear();
        _editAlbumName = string.Empty;
    }

    protected async Task SaveEdit()
    {
        if (_editingSong == null)
            return;

        _validationErrors.Clear();
        _isSaving = true;

        try
        {
            // Validate song title
            if (string.IsNullOrWhiteSpace(_editSongTitle))
            {
                _validationErrors.Add("Song title is required.");
            }

            // Validate album name if editing an album
            if (_editingSong.IsAlbum && string.IsNullOrWhiteSpace(_editAlbumName))
            {
                _validationErrors.Add("Album name is required.");
            }

            // Validate other fields
            if (!_editingSong.IsAlbum)
            {
                {
                    _validationErrors.Add("Song price is required and must be greater than 0.");
                }
                if (string.IsNullOrWhiteSpace(_editGenre))
                {
                    _validationErrors.Add("Genre is required.");
                }
            }
            else
            {
                {
                    _validationErrors.Add("Album price is required and must be greater than 0.");
                }
            }

            if (_validationErrors.Any())
            {
                return;
            }

            // Get the metadata by ID directly (more reliable than blob path)
            if (int.TryParse(_editingSong.Id, out var metadataId))
            {
                var metadata = await SongMetadataService.GetByIdAsync(metadataId);

                if (metadata != null)
                {
                    // Always update the title
                    metadata.SongTitle = _editSongTitle;

                    if (_editingSong.IsAlbum)
                    {
                        
                        // Update album name if changed
                        var oldAlbumName = metadata.AlbumName;
                        if (!string.IsNullOrEmpty(oldAlbumName) && oldAlbumName != _editAlbumName)
                        {
                            // Update album name for all tracks in this album
                            var albumTracks = await SongMetadataService.GetByAlbumNameAsync(oldAlbumName);
                            foreach (var track in albumTracks)
                            {
                                if (track.CreatorId == _creatorId) // Only update tracks owned by this creator
                                {
                                    track.AlbumName = _editAlbumName;
                                    await SongMetadataService.UpsertAsync(track);
                                }
                            }
                        }
                        metadata.AlbumName = _editAlbumName;
                    }
                    else
                    {
                        metadata.Genre = _editGenre;
                    }

                    await SongMetadataService.UpsertAsync(metadata);
                    
                    _successMessage = $"'{_editSongTitle}' has been updated successfully.";
                    await LoadSongsAsync();
                    _showEditDialog = false;
                    _editingSong = null;
                }
                else
                {
                    _validationErrors.Add("Could not find the song metadata.");
                }
            }
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"Error saving changes: {ex.Message}");
        }
        finally
        {
            _isSaving = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}
