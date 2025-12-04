using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
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
    [Inject] protected NavigationManager NavigationManager { get; set; }

    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<SongAdminViewModel> _allSongs = new();
    protected IEnumerable<SongAdminViewModel> _filteredSongs = new List<SongAdminViewModel>();

    // Filter fields
    protected string _filterAlbumName = string.Empty;
    protected string _filterSongTitle = string.Empty;
    protected string _filterGenre = string.Empty;
    protected string _filterType = string.Empty;

    // Sort fields
    protected string _sortColumn = string.Empty;
    protected bool _sortAscending = true;

    // Edit modal fields
    protected bool _showEditModal = false;
    protected SongAdminViewModel _editingSong = null;
    protected decimal? _editAlbumPrice = null;
    protected decimal? _editSongPrice = null;
    protected string _editGenre = string.Empty;
    protected IBrowserFile _songImageFile = null;
    protected IBrowserFile _albumImageFile = null;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await LoadSongsAsync();
            ApplyFiltersAndSort();
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
        var allFiles = await StorageService.ListFilesAsync();
        var songMap = new Dictionary<string, SongAdminViewModel>();

        // Group files by base name (song title)
        foreach (var file in allFiles)
        {
            var fileName = file.Name;
            var isImage = fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                         fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
            var isMp3 = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

            if (!isImage && !isMp3) continue;

            // Extract album name and song title from tags
            var albumName = file.Tags.TryGetValue(IndexTagNames.AlbumName, out var album) ? album : string.Empty;
            var isAlbumCover = file.Tags.TryGetValue(IndexTagNames.IsAlbumCover, out var coverFlag) && 
                              coverFlag.Equals("true", StringComparison.OrdinalIgnoreCase);

            // Get base name (remove extension and "_mastered" suffix)
            var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
            if (baseName.EndsWith("_mastered", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName.Substring(0, baseName.Length - "_mastered".Length);
            }

            // Create a unique key combining album name and base name
            var key = string.IsNullOrEmpty(albumName) ? baseName : $"{albumName}|{baseName}";

            if (!songMap.ContainsKey(key))
            {
                songMap[key] = new SongAdminViewModel
                {
                    Id = key,
                    AlbumName = albumName,
                    SongTitle = baseName,
                    IsAlbum = false
                };
            }

            var song = songMap[key];

            // Parse prices and genre from tags
            if (file.Tags.TryGetValue(IndexTagNames.AlbumPrice, out var albumPriceStr) && 
                decimal.TryParse(albumPriceStr, out var albumPrice))
            {
                song.AlbumPrice = albumPrice;
            }

            if (file.Tags.TryGetValue(IndexTagNames.SongPrice, out var songPriceStr) && 
                decimal.TryParse(songPriceStr, out var songPrice))
            {
                song.SongPrice = songPrice;
            }

            if (file.Tags.TryGetValue(IndexTagNames.Genre, out var genre))
            {
                song.Genre = genre;
            }

            if (isMp3)
            {
                song.Mp3FileName = fileName;
            }
            else if (isImage)
            {
                if (isAlbumCover)
                {
                    // This is an album cover
                    song.IsAlbum = true;
                    song.AlbumCoverBlobName = fileName;
                    song.AlbumCoverImageUrl = StorageService.GetReadSasUri(fileName, TimeSpan.FromHours(1)).ToString();
                    song.HasAlbumCover = true;
                }
                else
                {
                    // This is a song cover
                    song.JpegFileName = fileName;
                    song.SongImageUrl = StorageService.GetReadSasUri(fileName, TimeSpan.FromHours(1)).ToString();
                }
            }
        }

        // Now handle album covers - find all songs with the same album name and link them
        var albumCovers = songMap.Values.Where(s => s.IsAlbum && s.HasAlbumCover).ToList();
        foreach (var albumCover in albumCovers)
        {
            var songsInAlbum = songMap.Values.Where(s => 
                !s.IsAlbum && 
                !string.IsNullOrEmpty(s.AlbumName) && 
                s.AlbumName.Equals(albumCover.AlbumName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var song in songsInAlbum)
            {
                song.HasAlbumCover = true;
                song.AlbumCoverBlobName = albumCover.AlbumCoverBlobName;
                song.AlbumCoverImageUrl = albumCover.AlbumCoverImageUrl;
            }
        }

        _allSongs = songMap.Values.ToList();
    }

    protected void ApplyFiltersAndSort()
    {
        var filtered = _allSongs.AsEnumerable();

        // Apply filters
        if (!string.IsNullOrWhiteSpace(_filterAlbumName))
        {
            filtered = filtered.Where(s => s.AlbumName.Contains(_filterAlbumName, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_filterSongTitle))
        {
            filtered = filtered.Where(s => s.SongTitle.Contains(_filterSongTitle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_filterGenre))
        {
            filtered = filtered.Where(s => s.Genre.Equals(_filterGenre, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_filterType))
        {
            if (_filterType == "album")
            {
                filtered = filtered.Where(s => s.IsAlbum);
            }
            else if (_filterType == "song")
            {
                filtered = filtered.Where(s => !s.IsAlbum);
            }
        }

        // Apply sorting
        if (!string.IsNullOrEmpty(_sortColumn))
        {
            filtered = _sortColumn switch
            {
                nameof(SongAdminViewModel.AlbumName) => _sortAscending 
                    ? filtered.OrderBy(s => s.AlbumName) 
                    : filtered.OrderByDescending(s => s.AlbumName),
                nameof(SongAdminViewModel.SongTitle) => _sortAscending 
                    ? filtered.OrderBy(s => s.SongTitle) 
                    : filtered.OrderByDescending(s => s.SongTitle),
                nameof(SongAdminViewModel.IsAlbum) => _sortAscending 
                    ? filtered.OrderBy(s => s.IsAlbum) 
                    : filtered.OrderByDescending(s => s.IsAlbum),
                nameof(SongAdminViewModel.AlbumPrice) => _sortAscending 
                    ? filtered.OrderBy(s => s.AlbumPrice ?? 0) 
                    : filtered.OrderByDescending(s => s.AlbumPrice ?? 0),
                nameof(SongAdminViewModel.SongPrice) => _sortAscending 
                    ? filtered.OrderBy(s => s.SongPrice ?? 0) 
                    : filtered.OrderByDescending(s => s.SongPrice ?? 0),
                nameof(SongAdminViewModel.Genre) => _sortAscending 
                    ? filtered.OrderBy(s => s.Genre) 
                    : filtered.OrderByDescending(s => s.Genre),
                _ => filtered
            };
        }

        _filteredSongs = filtered.ToList();
    }

    protected void SortBy(string columnName)
    {
        if (_sortColumn == columnName)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = columnName;
            _sortAscending = true;
        }
        ApplyFiltersAndSort();
    }

    protected string GetSortIcon(string columnName)
    {
        if (_sortColumn != columnName)
        {
            return string.Empty;
        }
        return _sortAscending ? "▲" : "▼";
    }

    protected void ClearFilters()
    {
        _filterAlbumName = string.Empty;
        _filterSongTitle = string.Empty;
        _filterGenre = string.Empty;
        _filterType = string.Empty;
        ApplyFiltersAndSort();
    }

    protected void EditSong(SongAdminViewModel song)
    {
        _editingSong = song;
        _editAlbumPrice = song.AlbumPrice;
        _editSongPrice = song.SongPrice;
        _editGenre = song.Genre;
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
            // Validate
            if (_editingSong.IsAlbum && !_editingSong.HasAlbumCover && _albumImageFile == null)
            {
                _validationErrors.Add("All albums must have an album cover image.");
            }

            if (!string.IsNullOrEmpty(_editingSong.Mp3FileName) && string.IsNullOrEmpty(_editingSong.JpegFileName) && _songImageFile == null)
            {
                _validationErrors.Add("All MP3 files must have a song cover image.");
            }

            if (_editingSong.IsAlbum && !_editAlbumPrice.HasValue)
            {
                _validationErrors.Add("All albums must have a price.");
            }

            if (!string.IsNullOrEmpty(_editingSong.Mp3FileName) && !_editSongPrice.HasValue)
            {
                _validationErrors.Add("All songs must have a price.");
            }

            if (string.IsNullOrWhiteSpace(_editGenre) && !string.IsNullOrEmpty(_editingSong.Mp3FileName))
            {
                _validationErrors.Add("All songs must have a genre.");
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
                var newFileName = _editingSong.JpegFileName;
                if (string.IsNullOrEmpty(newFileName))
                {
                    newFileName = $"{_editingSong.SongTitle}.jpeg";
                }

                var tags = new Dictionary<string, string>
                {
                    { IndexTagNames.AlbumName, _editingSong.AlbumName },
                    { IndexTagNames.IsAlbumCover, "false" }
                };

                if (!string.IsNullOrEmpty(_editGenre))
                {
                    tags[IndexTagNames.Genre] = _editGenre;
                }

                if (_editSongPrice.HasValue)
                {
                    tags[IndexTagNames.SongPrice] = _editSongPrice.Value.ToString(PriceFormat);
                }

                if (_editAlbumPrice.HasValue)
                {
                    tags[IndexTagNames.AlbumPrice] = _editAlbumPrice.Value.ToString(PriceFormat);
                }

                await StorageService.UploadAsync(newFileName, stream, "image/jpeg", tags);
                _editingSong.JpegFileName = newFileName;
            }

            if (_albumImageFile != null)
            {
                using var stream = _albumImageFile.OpenReadStream(maxAllowedSize: MaxFileSize);
                var newFileName = _editingSong.AlbumCoverBlobName;
                if (string.IsNullOrEmpty(newFileName))
                {
                    newFileName = $"{_editingSong.AlbumName}_cover.jpeg";
                }

                var tags = new Dictionary<string, string>
                {
                    { IndexTagNames.AlbumName, _editingSong.AlbumName },
                    { IndexTagNames.IsAlbumCover, "true" }
                };

                if (_editAlbumPrice.HasValue)
                {
                    tags[IndexTagNames.AlbumPrice] = _editAlbumPrice.Value.ToString("F2");
                }

                await StorageService.UploadAsync(newFileName, stream, "image/jpeg", tags);
                _editingSong.AlbumCoverBlobName = newFileName;
            }

            // Update tags on existing blobs
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

            foreach (var fileName in filesToUpdate)
            {
                var existingTags = await StorageService.GetTagsAsync(fileName);
                var isAlbumCover = existingTags.TryGetValue(IndexTagNames.IsAlbumCover, out var coverFlag) && 
                                  coverFlag.Equals("true", StringComparison.OrdinalIgnoreCase);

                // Update or add new tags
                if (!string.IsNullOrEmpty(_editGenre) && !isAlbumCover)
                {
                    existingTags[IndexTagNames.Genre] = _editGenre;
                }

                if (_editSongPrice.HasValue && !isAlbumCover)
                {
                    existingTags[IndexTagNames.SongPrice] = _editSongPrice.Value.ToString(PriceFormat);
                }

                if (_editAlbumPrice.HasValue && !string.IsNullOrEmpty(_editingSong.AlbumName))
                {
                    existingTags[IndexTagNames.AlbumPrice] = _editAlbumPrice.Value.ToString(PriceFormat);
                }

                await StorageService.SetTagsAsync(fileName, existingTags);
            }

            // Update local model
            _editingSong.AlbumPrice = _editAlbumPrice;
            _editingSong.SongPrice = _editSongPrice;
            _editingSong.Genre = _editGenre;

            // Close modal and refresh
            _showEditModal = false;
            await LoadSongsAsync();
            ApplyFiltersAndSort();
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
}
