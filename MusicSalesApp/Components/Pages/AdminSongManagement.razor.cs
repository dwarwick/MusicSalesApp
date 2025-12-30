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
    protected decimal? _editAlbumPrice = null;
    protected decimal? _editSongPrice = null;
    protected string _editGenre = string.Empty;
    protected int? _editTrackNumber = null;
    protected bool _editDisplayOnHomePage = false;
    protected IBrowserFile _songImageFile = null;
    protected IBrowserFile _albumImageFile = null;
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
        _allSongs = allMetadata.Select(m => new SongAdminViewModel
        {
            Id = m.Id.ToString(),
            AlbumName = m.AlbumName ?? string.Empty,
            SongTitle = System.IO.Path.GetFileNameWithoutExtension(m.Mp3BlobPath ?? m.ImageBlobPath ?? m.BlobPath),
            Mp3FileName = m.Mp3BlobPath ?? (m.FileExtension == ".mp3" ? m.BlobPath : string.Empty),
            JpegFileName = m.IsAlbumCover ? string.Empty : (m.ImageBlobPath ?? ((m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") ? m.BlobPath : string.Empty)),
            AlbumCoverBlobName = m.IsAlbumCover ? (m.ImageBlobPath ?? m.BlobPath) : string.Empty,
            IsAlbum = m.IsAlbumCover,
            AlbumPrice = m.AlbumPrice,
            SongPrice = m.SongPrice,
            Genre = m.Genre ?? string.Empty,
            TrackNumber = m.TrackNumber,
            TrackLength = m.TrackLength,
            DisplayOnHomePage = m.DisplayOnHomePage,
            HasAlbumCover = m.IsAlbumCover
        }).ToList();
        
        // Generate SAS URLs for images
        foreach (var song in _allSongs)
        {
            if (!string.IsNullOrEmpty(song.JpegFileName))
            {
                song.SongImageUrl = StorageService.GetReadSasUri(song.JpegFileName, TimeSpan.FromHours(1)).ToString();
            }
            if (!string.IsNullOrEmpty(song.AlbumCoverBlobName))
            {
                song.AlbumCoverImageUrl = StorageService.GetReadSasUri(song.AlbumCoverBlobName, TimeSpan.FromHours(1)).ToString();
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
        _editAlbumPrice = song.AlbumPrice;
        _editSongPrice = song.SongPrice;
        _editGenre = song.Genre;
        _editTrackNumber = song.TrackNumber;
        _editDisplayOnHomePage = song.DisplayOnHomePage;
        _songImageFile = null;
        _albumImageFile = null;
        _validationErrors.Clear();
        _showEditModal = true;
    }

    protected void CancelEdit()
    {
        _showEditModal = false;
        _editingSong = null;
        _validationErrors.Clear();
        _songImageFile = null;
        _albumImageFile = null;
    }

    protected async Task SaveEdit()
    {
        if (_editingSong == null) return;

        _validationErrors.Clear();
        _isSaving = true;

        try
        {
            // Determine what type of entry this is
            var hasMP3 = !string.IsNullOrEmpty(_editingSong.Mp3FileName);
            var isAlbumCoverEntry = _editingSong.IsAlbum && !hasMP3; // Album cover JPEG (no MP3)
            var isAlbumTrack = hasMP3 && !string.IsNullOrEmpty(_editingSong.AlbumName); // MP3 with album name
            var isStandaloneSong = hasMP3 && string.IsNullOrEmpty(_editingSong.AlbumName); // MP3 without album name

            // Validate album cover entries (JPEG with IsAlbumCover = true)
            if (isAlbumCoverEntry)
            {
                if (!_editingSong.HasAlbumCover && _albumImageFile == null)
                {
                    _validationErrors.Add("All albums must have an album cover image.");
                }

                if (!_editAlbumPrice.HasValue)
                {
                    _validationErrors.Add("All albums must have a price.");
                }
            }

            // Validate MP3 files that are part of an album
            if (isAlbumTrack)
            {
                // Track number is required for album tracks
                if (!_editTrackNumber.HasValue)
                {
                    _validationErrors.Add("Track Number is required for album tracks.");
                }
                else if (_editTrackNumber.Value < 1)
                {
                    _validationErrors.Add("Track Number must be at least 1.");
                }
                else
                {
                    // Check track number uniqueness within the album
                    // Get all other MP3 tracks in the same album (excluding the current track being edited)
                    var albumTracks = _allSongs.Where(s => 
                        !string.IsNullOrEmpty(s.Mp3FileName) &&
                        !string.IsNullOrEmpty(s.AlbumName) &&
                        s.AlbumName.Equals(_editingSong.AlbumName, StringComparison.OrdinalIgnoreCase) &&
                        s.Id != _editingSong.Id).ToList();
                    
                    // Total tracks = other tracks + current track
                    var totalTracksInAlbum = albumTracks.Count + 1;
                    if (_editTrackNumber.Value > totalTracksInAlbum)
                    {
                        _validationErrors.Add($"Track Number cannot exceed {totalTracksInAlbum} (total tracks in album).");
                    }

                    // Check for duplicate track number among other tracks
                    if (albumTracks.Any(t => t.TrackNumber == _editTrackNumber.Value))
                    {
                        _validationErrors.Add($"Track Number {_editTrackNumber.Value} is already used by another track in this album.");
                    }
                }

                // All MP3s need a price (song price for album tracks)
                if (!_editSongPrice.HasValue)
                {
                    _validationErrors.Add("All tracks must have a price.");
                }

                // All MP3s need a genre
                if (string.IsNullOrWhiteSpace(_editGenre))
                {
                    _validationErrors.Add("All tracks must have a genre.");
                }
            }

            // Validate standalone songs (MP3 without album)
            if (isStandaloneSong)
            {
                if (string.IsNullOrEmpty(_editingSong.JpegFileName) && _songImageFile == null)
                {
                    _validationErrors.Add("All standalone songs must have a cover image.");
                }

                if (!_editSongPrice.HasValue)
                {
                    _validationErrors.Add("All standalone songs must have a price.");
                }

                if (string.IsNullOrWhiteSpace(_editGenre))
                {
                    _validationErrors.Add("All standalone songs must have a genre.");
                }
            }

            if (_validationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            // Upload new images if provided (no tags)
            if (_songImageFile != null && !_editingSong.IsAlbum)
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
                // This handles the case where a standalone song already has metadata for its MP3
                if (existingMetadata == null && !string.IsNullOrEmpty(_editingSong.Mp3FileName))
                {
                    existingMetadata = await MetadataService.GetByBlobPathAsync(_editingSong.Mp3FileName);
                }

                if (existingMetadata != null)
                {
                    // Update existing record with new image path
                    existingMetadata.ImageBlobPath = newFileName;
                    existingMetadata.FileExtension = fileExtension;
                    existingMetadata.AlbumName = _editingSong.AlbumName ?? string.Empty;
                    existingMetadata.IsAlbumCover = false;
                    existingMetadata.Genre = _editGenre;
                    existingMetadata.SongPrice = _editSongPrice;
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
                        AlbumName = _editingSong.AlbumName ?? string.Empty,
                        IsAlbumCover = false,
                        Genre = _editGenre,
                        SongPrice = _editSongPrice,
                        DisplayOnHomePage = _editDisplayOnHomePage
                    });
                }
            }

            if (_albumImageFile != null && _editingSong.IsAlbum)
            {
                using var stream = _albumImageFile.OpenReadStream(maxAllowedSize: MaxFileSize);
                
                // Get the file extension from the uploaded file
                var fileExtension = Path.GetExtension(_albumImageFile.Name).ToLowerInvariant();
                var contentType = GetImageContentType(fileExtension);
                
                var oldFileName = _editingSong.AlbumCoverBlobName;
                var newFileName = oldFileName;
                if (string.IsNullOrEmpty(newFileName))
                {
                    newFileName = $"{_editingSong.AlbumName}_cover{fileExtension}";
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
                _editingSong.AlbumCoverBlobName = newFileName;

                // Get existing metadata by old filename and update it
                SongMetadata existingMetadata = null;
                if (!string.IsNullOrEmpty(oldFileName))
                {
                    existingMetadata = await MetadataService.GetByBlobPathAsync(oldFileName);
                }

                if (existingMetadata != null)
                {
                    // Update existing record with new image path
                    existingMetadata.ImageBlobPath = newFileName;
                    existingMetadata.BlobPath = newFileName; // Also update legacy BlobPath for album covers
                    existingMetadata.FileExtension = fileExtension;
                    existingMetadata.AlbumName = _editingSong.AlbumName;
                    existingMetadata.IsAlbumCover = true;
                    existingMetadata.AlbumPrice = _editAlbumPrice;
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
                        AlbumName = _editingSong.AlbumName,
                        IsAlbumCover = true,
                        AlbumPrice = _editAlbumPrice,
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

            if (!string.IsNullOrEmpty(_editingSong.AlbumCoverBlobName) && _albumImageFile == null)
            {
                filesToUpdate.Add(_editingSong.AlbumCoverBlobName);
            }

            // Process updates sequentially to avoid DbContext concurrency issues
            foreach (var fileName in filesToUpdate)
            {
                var metadata = await MetadataService.GetByBlobPathAsync(fileName);
                if (metadata == null) continue;

                var isAlbumCover = metadata.IsAlbumCover;
                var isMP3 = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

                // Update DisplayOnHomePage for all file types
                metadata.DisplayOnHomePage = _editDisplayOnHomePage;

                // Update album cover metadata
                if (isAlbumCover)
                {
                    if (_editAlbumPrice.HasValue)
                    {
                        metadata.AlbumPrice = _editAlbumPrice.Value;
                    }
                }
                // Update MP3 metadata (both album tracks and standalone songs)
                else if (isMP3)
                {
                    // Set genre for all MP3s
                    if (!string.IsNullOrEmpty(_editGenre))
                    {
                        metadata.Genre = _editGenre;
                    }

                    // Set song price for all MP3s
                    if (_editSongPrice.HasValue)
                    {
                        metadata.SongPrice = _editSongPrice.Value;
                    }

                    // Set track number only for MP3s that are part of an album
                    if (!string.IsNullOrEmpty(_editingSong.AlbumName) && _editTrackNumber.HasValue)
                    {
                        metadata.TrackNumber = _editTrackNumber.Value;
                    }
                }

                // Each upsert awaited sequentially
                await MetadataService.UpsertAsync(metadata);
            }

            // Update local model
            _editingSong.AlbumPrice = _editAlbumPrice;
            _editingSong.SongPrice = _editSongPrice;
            _editingSong.Genre = _editGenre;
            _editingSong.TrackNumber = _editTrackNumber;
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

    protected void HandleAlbumImageUpload(InputFileChangeEventArgs e)
    {
        _albumImageFile = e.File;
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
