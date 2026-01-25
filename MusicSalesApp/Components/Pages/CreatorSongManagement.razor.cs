using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages;

public partial class CreatorSongManagementModel : BlazorBase
{
    private const long MaxFileSize = 10 * 1024 * 1024; // 10MB

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
    protected string _editArtistName = string.Empty;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;
    protected IBrowserFile _songImageFile = null;

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
        
        // Filter out album covers for legacy data that may still exist in the database.
        // The IsAlbumCover field remains in the schema for backward compatibility but is no longer used for new uploads.
        _songs = creatorSongs
            .Where(m => !m.IsAlbumCover)
            .Select(m => new SongAdminViewModel
        {
            Id = m.Id.ToString(),
            SongTitle = GetSongTitleFromMetadata(m),
            Mp3FileName = m.Mp3BlobPath ?? (m.FileExtension == ".mp3" ? m.BlobPath : string.Empty),
            JpegFileName = m.ImageBlobPath ?? ((m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") ? m.BlobPath : string.Empty),
            Genre = m.Genre ?? string.Empty,
            ArtistName = m.GetEffectiveArtistNameFull(),
            RawArtistName = m.ArtistName ?? string.Empty,
            TrackLength = m.TrackLength,
            DisplayOnHomePage = m.DisplayOnHomePage,
            CreatorId = m.CreatorId,
            IsActive = m.IsActive,
            IsEnabled = m.IsEnabled,
            StatusReason = m.StatusReason ?? string.Empty,
            NumberOfStreams = m.NumberOfStreams
        }).ToList();

        // Generate SAS URLs for images
        foreach (var song in _songs)
        {
            if (!string.IsNullOrEmpty(song.JpegFileName))
            {
                song.SongImageUrl = AzureStorageService.GetReadSasUri(song.JpegFileName, TimeSpan.FromHours(1)).ToString();
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
        // If RawArtistName is empty, default to the effective artist name shown in the grid
        _editArtistName = string.IsNullOrWhiteSpace(song.RawArtistName) ? song.ArtistName : song.RawArtistName;
        _songImageFile = null;
        _validationErrors.Clear();
        _showEditDialog = true;
    }

    protected void CancelEdit()
    {
        _editingSong = null;
        _showEditDialog = false;
        _validationErrors.Clear();
        _songImageFile = null;
        _editArtistName = string.Empty;
    }

    protected void HandleSongImageUpload(InputFileChangeEventArgs e)
    {
        _songImageFile = e.File;
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

            // Validate genre
            if (string.IsNullOrWhiteSpace(_editGenre))
            {
                _validationErrors.Add("Genre is required.");
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
                    // Handle image upload if provided
                    if (_songImageFile != null)
                    {
                        using var stream = _songImageFile.OpenReadStream(maxAllowedSize: MaxFileSize);
                        
                        // Get the file extension from the uploaded file
                        var fileExtension = System.IO.Path.GetExtension(_songImageFile.Name).ToLowerInvariant();
                        var contentType = GetImageContentType(fileExtension);
                        
                        var oldFileName = metadata.ImageBlobPath;
                        string newFileName;
                        
                        if (string.IsNullOrEmpty(oldFileName))
                        {
                            // No existing image - construct path in same folder as MP3
                            if (!string.IsNullOrEmpty(metadata.Mp3BlobPath))
                            {
                                var mp3Dir = System.IO.Path.GetDirectoryName(metadata.Mp3BlobPath)?.Replace("\\", "/");
                                var baseName = System.IO.Path.GetFileNameWithoutExtension(metadata.Mp3BlobPath);
                                newFileName = string.IsNullOrEmpty(mp3Dir) 
                                    ? $"{baseName}{fileExtension}" 
                                    : $"{mp3Dir}/{baseName}{fileExtension}";
                            }
                            else
                            {
                                // Fallback to song title if no MP3
                                newFileName = $"{_editSongTitle}{fileExtension}";
                            }
                        }
                        else
                        {
                            // Replace the old extension with the new one
                            newFileName = System.IO.Path.ChangeExtension(oldFileName, fileExtension);
                        }

                        // Delete old blob before uploading new one (if it exists)
                        if (!string.IsNullOrEmpty(oldFileName))
                        {
                            await AzureStorageService.DeleteAsync(oldFileName);
                        }

                        await AzureStorageService.UploadAsync(newFileName, stream, contentType);
                        
                        // Update metadata with new image path
                        metadata.ImageBlobPath = newFileName;
                        _editingSong.JpegFileName = newFileName;
                    }

                    // Always update the title, genre, and artist name
                    metadata.SongTitle = _editSongTitle;
                    metadata.Genre = _editGenre;
                    metadata.ArtistName = string.IsNullOrWhiteSpace(_editArtistName) ? null : _editArtistName;

                    await SongMetadataService.UpsertAsync(metadata);
                    
                    _successMessage = $"'{_editSongTitle}' has been updated successfully.";
                    await LoadSongsAsync();
                    _showEditDialog = false;
                    _editingSong = null;
                    _songImageFile = null;
                    _editArtistName = string.Empty;
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

    private static string GetImageContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "image/jpeg" // Default fallback
        };
    }
}
