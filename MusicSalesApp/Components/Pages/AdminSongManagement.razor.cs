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
    protected List<SongAdminViewModel> _currentPageSongs = new();
    protected SfGrid<SongAdminViewModel> _grid;
    protected int _totalCount = 0;
    protected int _totalPages = 1;
    protected int _currentPage = 1;
    protected string _currentSortColumn = string.Empty;
    protected bool _currentSortAscending = true;

    // Removed: Filter fields - now using Syncfusion's native filtering

    // Edit modal fields
    protected bool _showEditModal = false;
    protected SongAdminViewModel _editingSong = null;
    protected decimal? _editAlbumPrice = null;
    protected decimal? _editSongPrice = null;
    protected string _editGenre = string.Empty;
    protected int? _editTrackNumber = null;
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
            
            // Load first page
            await LoadPageAsync(0, 10);
            
            // Load all songs for validation purposes (used in Edit)
            await LoadSongsAsync();
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

    protected async Task LoadPageAsync(int skip, int take)
    {
        var parameters = new SongQueryParameters
        {
            Skip = skip,
            Take = take,
            SortColumn = _currentSortColumn,
            SortAscending = _currentSortAscending
        };

        var result = await SongAdminService.GetSongsAsync(parameters);
        _currentPageSongs = result.Items.ToList();
        _totalCount = result.TotalCount;
        _totalPages = (int)Math.Ceiling((double)_totalCount / take);
    }

    protected async Task LoadSongsAsync()
    {
        // Load all metadata from database for validation purposes
        var allMetadata = await MetadataService.GetAllAsync();
        _allSongs = allMetadata.Select(m => new SongAdminViewModel
        {
            Id = m.Id.ToString(),
            AlbumName = m.AlbumName ?? string.Empty,
            SongTitle = System.IO.Path.GetFileNameWithoutExtension(m.BlobPath),
            Mp3FileName = m.FileExtension == ".mp3" ? m.BlobPath : string.Empty,
            JpegFileName = (m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") && !m.IsAlbumCover ? m.BlobPath : string.Empty,
            AlbumCoverBlobName = m.IsAlbumCover ? m.BlobPath : string.Empty,
            IsAlbum = m.IsAlbumCover,
            AlbumPrice = m.AlbumPrice,
            SongPrice = m.SongPrice,
            Genre = m.Genre ?? string.Empty,
            TrackNumber = m.TrackNumber,
            TrackLength = m.TrackLength,
            HasAlbumCover = m.IsAlbumCover
        }).ToList();
    }

    protected async Task OnActionBegin(ActionEventArgs<SongAdminViewModel> args)
    {
        if (args.RequestType == Syncfusion.Blazor.Grids.Action.Paging)
        {
            // Handle paging
            var pageSize = 10;
            var skip = (args.CurrentPage - 1) * pageSize;
            _currentPage = args.CurrentPage;
            await LoadPageAsync(skip, pageSize);
            args.Cancel = true; // Cancel default paging behavior
            StateHasChanged();
        }
        else if (args.RequestType == Syncfusion.Blazor.Grids.Action.Sorting && args.ColumnName != null)
        {
            // Handle sorting
            _currentSortColumn = args.ColumnName;
            _currentSortAscending = args.Direction == Syncfusion.Blazor.Grids.SortDirection.Ascending;
            await LoadPageAsync((_currentPage - 1) * 10, 10);
            args.Cancel = true; // Cancel default sorting behavior
            StateHasChanged();
        }
    }

    protected void EditSong(SongAdminViewModel song)
    {
        _editingSong = song;
        _editAlbumPrice = song.AlbumPrice;
        _editSongPrice = song.SongPrice;
        _editGenre = song.Genre;
        _editTrackNumber = song.TrackNumber;
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
                
                var newFileName = _editingSong.JpegFileName;
                if (string.IsNullOrEmpty(newFileName))
                {
                    newFileName = $"{_editingSong.SongTitle}{fileExtension}";
                }
                else
                {
                    // Replace the old extension with the new one
                    newFileName = Path.ChangeExtension(newFileName, fileExtension);
                }

                await StorageService.UploadAsync(newFileName, stream, contentType);
                _editingSong.JpegFileName = newFileName;

                // Create/update metadata in database
                await MetadataService.UpsertAsync(new SongMetadata
                {
                    BlobPath = newFileName,
                    FileExtension = fileExtension,
                    AlbumName = _editingSong.AlbumName ?? string.Empty,
                    IsAlbumCover = false,
                    Genre = _editGenre,
                    SongPrice = _editSongPrice
                });
            }

            if (_albumImageFile != null && _editingSong.IsAlbum)
            {
                using var stream = _albumImageFile.OpenReadStream(maxAllowedSize: MaxFileSize);
                
                // Get the file extension from the uploaded file
                var fileExtension = Path.GetExtension(_albumImageFile.Name).ToLowerInvariant();
                var contentType = GetImageContentType(fileExtension);
                
                var newFileName = _editingSong.AlbumCoverBlobName;
                if (string.IsNullOrEmpty(newFileName))
                {
                    newFileName = $"{_editingSong.AlbumName}_cover{fileExtension}";
                }
                else
                {
                    // Replace the old extension with the new one
                    newFileName = Path.ChangeExtension(newFileName, fileExtension);
                }

                await StorageService.UploadAsync(newFileName, stream, contentType);
                _editingSong.AlbumCoverBlobName = newFileName;

                // Create/update metadata in database
                await MetadataService.UpsertAsync(new SongMetadata
                {
                    BlobPath = newFileName,
                    FileExtension = fileExtension,
                    AlbumName = _editingSong.AlbumName,
                    IsAlbumCover = true,
                    AlbumPrice = _editAlbumPrice
                });
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

            // Close modal and refresh
            _showEditModal = false;
            
            // Refresh the cache, all songs, and current page
            await SongAdminService.RefreshCacheAsync();
            await LoadSongsAsync();
            await LoadPageAsync((_currentPage - 1) * 10, 10);
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
