using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using Syncfusion.Blazor;
using Syncfusion.Blazor.Data;
using Syncfusion.Blazor.Grids;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicSalesApp.Components.Pages;

public class AdminSongManagementModel : ComponentBase
{
    private const long MaxFileSize = 10 * 1024 * 1024; // 10MB
    private const string PriceFormat = "F2";

    [Inject] protected IAzureStorageService StorageService { get; set; }
    [Inject] protected ISongAdminService SongAdminService { get; set; }
    [Inject] protected ISongMetadataService MetadataService { get; set; }
    [Inject] protected NavigationManager NavigationManager { get; set; }

    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<SongAdminViewModel> _allSongs = new();
    protected SfGrid<SongAdminViewModel> _grid;
    protected int _totalCount = 0;

    // Edit modal fields
    protected bool _showEditModal = false;
    protected SongAdminViewModel _editingSong = null;
    protected string _editGenre = string.Empty;
    protected bool _editDisplayOnHomePage = false;
    protected IBrowserFile _songImageFile = null;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // Pre-load the cache
            await SongAdminService.RefreshCacheAsync();
            
            // Load all songs for the grid
            await LoadSongsAsync();
            
            _totalCount = _allSongs.Count;
        }
        catch (Exception ex)
        {
            _errorMessage = $"Failed to load songs: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
        }
    }

    protected async Task LoadSongsAsync()
    {
        // Load all metadata from database for validation purposes
        var allMetadata = await MetadataService.GetAllAsync();
        _allSongs = allMetadata
            .Where(m => !m.IsAlbumCover) // Filter out album covers since we no longer use albums
            .Select(m => new SongAdminViewModel
        {
            Id = m.Id.ToString(),
            SongTitle = !string.IsNullOrEmpty(m.SongTitle) ? m.SongTitle : System.IO.Path.GetFileNameWithoutExtension(m.Mp3BlobPath ?? m.ImageBlobPath ?? m.BlobPath),
            Mp3FileName = m.Mp3BlobPath ?? (m.FileExtension == ".mp3" ? m.BlobPath : string.Empty),
            JpegFileName = m.ImageBlobPath ?? ((m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") ? m.BlobPath : string.Empty),
            Genre = m.Genre ?? string.Empty,
            TrackLength = m.TrackLength,
            DisplayOnHomePage = m.DisplayOnHomePage
        }).ToList();
        
        // Generate SAS URLs for images
        foreach (var song in _allSongs)
        {
            if (!string.IsNullOrEmpty(song.JpegFileName))
            {
                song.SongImageUrl = StorageService.GetReadSasUri(song.JpegFileName, TimeSpan.FromHours(1)).ToString();
            }
        }
    }

    protected Task OnActionBegin(ActionEventArgs<SongAdminViewModel> args)
    {
        // Let Syncfusion handle paging, sorting, and filtering natively
        // since we're now using _allSongs as the DataSource
        return Task.CompletedTask;
    }

    protected void EditSong(SongAdminViewModel song)
    {
        _editingSong = song;
        _editGenre = song.Genre;
        _editDisplayOnHomePage = song.DisplayOnHomePage;
        _songImageFile = null;
        _validationErrors.Clear();
        _showEditModal = true;
    }

    protected void CancelEdit()
    {
        _showEditModal = false;
        _editingSong = null;
        _validationErrors.Clear();
        _songImageFile = null;
    }

    protected async Task SaveEdit()
    {
        if (_editingSong == null) return;

        _validationErrors.Clear();
        _isSaving = true;

        try
        {
            // All songs are now standalone songs - validate accordingly
            var hasMP3 = !string.IsNullOrEmpty(_editingSong.Mp3FileName);

            // Validate standalone songs (all songs)
            if (hasMP3)
            {
                if (string.IsNullOrEmpty(_editingSong.JpegFileName) && _songImageFile == null)
                {
                    _validationErrors.Add("All songs must have a cover image.");
                }

                if (string.IsNullOrWhiteSpace(_editGenre))
                {
                    _validationErrors.Add("All songs must have a genre.");
                }
            }

            if (_validationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            // Upload new images if provided
            if (_songImageFile != null)
            {
                using var stream = _songImageFile.OpenReadStream(maxAllowedSize: MaxFileSize);
                
                // Get the file extension from the uploaded file
                var fileExtension = Path.GetExtension(_songImageFile.Name).ToLowerInvariant();
                var contentType = GetImageContentType(fileExtension);
                
                var oldFileName = _editingSong.JpegFileName;
                var newFileName = oldFileName;
                if (string.IsNullOrEmpty(newFileName))
                {
                    newFileName = $"{_editingSong.SongTitle}{fileExtension}";
                }
                else
                {
                    // Replace the old extension with the new one
                    newFileName = Path.ChangeExtension(newFileName, fileExtension);
                }

                // Delete old blob before uploading new one (always delete when replacing)
                if (!string.IsNullOrEmpty(oldFileName))
                {
                    await StorageService.DeleteAsync(oldFileName);
                }

                await StorageService.UploadAsync(newFileName, stream, contentType);
                _editingSong.JpegFileName = newFileName;

                // Get existing metadata by old filename and update it, or by the associated MP3 file
                SongMetadata existingMetadata = null;
                if (!string.IsNullOrEmpty(oldFileName))
                {
                    existingMetadata = await MetadataService.GetByBlobPathAsync(oldFileName);
                }
                
                // If no existing metadata found by old image path, try to find it by MP3 path
                if (existingMetadata == null && !string.IsNullOrEmpty(_editingSong.Mp3FileName))
                {
                    existingMetadata = await MetadataService.GetByBlobPathAsync(_editingSong.Mp3FileName);
                }

                if (existingMetadata != null)
                {
                    // Update existing record with new image path
                    existingMetadata.ImageBlobPath = newFileName;
                    existingMetadata.FileExtension = fileExtension;
                    existingMetadata.IsAlbumCover = false;
                    existingMetadata.Genre = _editGenre;
                    existingMetadata.DisplayOnHomePage = _editDisplayOnHomePage;
                    await MetadataService.UpsertAsync(existingMetadata);
                }
                else
                {
                    // Create new metadata if none exists
                    await MetadataService.UpsertAsync(new SongMetadata
                    {
                        BlobPath = newFileName,
                        ImageBlobPath = newFileName,
                        FileExtension = fileExtension,
                        IsAlbumCover = false,
                        Genre = _editGenre,
                        DisplayOnHomePage = _editDisplayOnHomePage
                    });
                }
            }

            // Update metadata in database for existing files
            var filesToUpdate = new List<string>();

            if (!string.IsNullOrEmpty(_editingSong.Mp3FileName))
            {
                filesToUpdate.Add(_editingSong.Mp3FileName);
            }

            if (!string.IsNullOrEmpty(_editingSong.JpegFileName) && _songImageFile == null)
            {
                filesToUpdate.Add(_editingSong.JpegFileName);
            }

            // Process updates sequentially to avoid DbContext concurrency issues
            foreach (var fileName in filesToUpdate)
            {
                var metadata = await MetadataService.GetByBlobPathAsync(fileName);
                if (metadata == null) continue;

                var isMP3 = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

                // Update DisplayOnHomePage for all file types
                metadata.DisplayOnHomePage = _editDisplayOnHomePage;

                // Update MP3 metadata
                if (isMP3)
                {
                    // Set genre for all MP3s
                    if (!string.IsNullOrEmpty(_editGenre))
                    {
                        metadata.Genre = _editGenre;
                    }
                }

                // Each upsert awaited sequentially
                await MetadataService.UpsertAsync(metadata);
            }

            // Update local model
            _editingSong.Genre = _editGenre;
            _editingSong.DisplayOnHomePage = _editDisplayOnHomePage;

            // Close modal and refresh
            _showEditModal = false;
            
            // Refresh the cache and reload all songs
            await SongAdminService.RefreshCacheAsync();
            await LoadSongsAsync();
            _totalCount = _allSongs.Count;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"Error saving changes: {ex.Message}");
        }
        finally
        {
            _isSaving = false;
        }
    }

    protected void HandleSongImageUpload(InputFileChangeEventArgs e)
    {
        _songImageFile = e.File;
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
