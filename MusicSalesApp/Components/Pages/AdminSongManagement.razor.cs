using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
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

public class AdminSongManagementModel : ComponentBase, IAsyncDisposable
{
    private long _maxImageFileSize = 10 * 1024 * 1024; // default 10MB, loaded from settings
    private const string PriceFormat = "F2";
    private const int CropOutputSize = 800; // Output size in pixels for cropped square images

    [Inject] protected IAzureStorageService StorageService { get; set; }
    [Inject] protected ISongAdminService SongAdminService { get; set; }
    [Inject] protected ISongMetadataService MetadataService { get; set; }
    [Inject] protected NavigationManager NavigationManager { get; set; }
    [Inject] protected ISongStatusService SongStatusService { get; set; }
    [Inject] protected AuthenticationStateProvider AuthenticationStateProvider { get; set; }
    [Inject] protected UserManager<ApplicationUser> UserManager { get; set; }
    [Inject] protected IJSRuntime JS { get; set; }
    [Inject] protected IGenreService GenreService { get; set; }
    [Inject] protected IEmailService EmailService { get; set; }
    [Inject] protected IConfiguration Configuration { get; set; }
    [Inject] protected IAppSettingsService AppSettingsService { get; set; }

    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<SongAdminViewModel> _allSongs = new();
    protected SfGrid<SongAdminViewModel> _grid;
    protected int _totalCount = 0;

    // Edit modal fields
    protected bool _showEditModal = false;
    protected SongAdminViewModel _editingSong = null;
    protected string _editGenre = string.Empty;
    protected string _editArtistName = string.Empty;
    protected bool _editDisplayOnHomePage = false;
    protected IBrowserFile _songImageFile = null;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;

    // Enable/Disable fields
    protected bool _editIsDisabled = false;
    protected string _editStatusReason = string.Empty;

    // Crop tool fields
    protected bool _showCropTool = false;
    protected bool _cropApplied = false;
    protected string _cropTargetBlobPath = null;
    protected int _cropZoom = 50;
    private IJSObjectReference _cropModule;
    private Dictionary<string, SongMetadata> _metadataById = new();

    // Genre management fields
    protected List<string> _genreOptions = new();
    protected string _newGenreName = string.Empty;
    protected bool _showAddGenre = false;
    protected List<Genre> _allGenres = new();
    protected bool _showGenreManagement = false;
    private string _currentAdminEmail = string.Empty;
    private bool _hasLoadedData = false;
    private bool _imageDimensionsChecked = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                // Load max image upload size from settings
                try
                {
                    var sizeMB = await AppSettingsService.GetMaxImageUploadSizeMBAsync();
                    _maxImageFileSize = (long)sizeMB * 1024 * 1024;
                }
                catch
                {
                    // Use default if setting cannot be loaded
                }

                // Load genres from database
                await LoadGenresAsync();

                // Get current admin email
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;
                if (user.Identity?.IsAuthenticated == true)
                {
                    var appUser = await UserManager.GetUserAsync(user);
                    _currentAdminEmail = appUser?.Email ?? string.Empty;
                }

                // Pre-load the cache
                await SongAdminService.RefreshCacheAsync();

                // Load all songs for the grid (including disabled songs for admin view)
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
                await InvokeAsync(StateHasChanged);
            }
        }

        if (!firstRender && _allSongs.Count > 0 && !_imageDimensionsChecked)
        {
            _imageDimensionsChecked = true;
            await CheckAllImageDimensions();
        }
    }

    protected async Task LoadSongsAsync()
    {
        // Load all metadata from database for validation purposes
        // For admin view, we need to include disabled songs as well
        var allMetadata = await MetadataService.GetAllIncludingDisabledAsync();
        _metadataById.Clear();
        _allSongs = allMetadata
            .Where(m => !m.IsAlbumCover) // Filter out album covers since we no longer use albums
            .Select(m => {
                _metadataById[m.Id.ToString()] = m;
                return new SongAdminViewModel
                {
                    Id = m.Id.ToString(),
                    SongTitle = !string.IsNullOrEmpty(m.SongTitle) ? m.SongTitle : System.IO.Path.GetFileNameWithoutExtension(m.Mp3BlobPath ?? m.ImageBlobPath ?? m.BlobPath),
                    Mp3FileName = m.Mp3BlobPath ?? (m.FileExtension == ".mp3" ? m.BlobPath : string.Empty),
                    JpegFileName = m.ImageBlobPath ?? ((m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") ? m.BlobPath : string.Empty),
                    Genre = m.Genre ?? string.Empty,
                    ArtistName = m.GetEffectiveArtistNameFull(),
                    RawArtistName = m.ArtistName ?? string.Empty,
                    CreatorEmail = m.Creator?.User?.Email ?? string.Empty,
                    TrackLength = m.TrackLength,
                    DisplayOnHomePage = m.DisplayOnHomePage,
                    IsEnabled = m.IsEnabled,
                    StatusReason = m.StatusReason ?? string.Empty,
                    IsImageSquare = m.IsImageSquare
                };
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
        // If RawArtistName is empty, default to the effective artist name shown in the grid
        _editArtistName = string.IsNullOrWhiteSpace(song.RawArtistName) ? song.ArtistName : song.RawArtistName;
        _editDisplayOnHomePage = song.DisplayOnHomePage;
        _editIsDisabled = !song.IsEnabled;
        _editStatusReason = string.Empty; // Clear the reason field for new edits
        _songImageFile = null;
        _cropApplied = false;
        _cropTargetBlobPath = null;
        _showCropTool = false;
        _cropZoom = 50;
        _newGenreName = string.Empty;
        _showAddGenre = false;
        _validationErrors.Clear();
        _showEditModal = true;
    }

    protected async Task CancelEdit()
    {
        _showEditModal = false;
        _editingSong = null;
        _validationErrors.Clear();
        _songImageFile = null;
        _cropApplied = false;
        _cropTargetBlobPath = null;
        _showCropTool = false;
        _editStatusReason = string.Empty;
        _editArtistName = string.Empty;
        await DisposeCropTool();
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

            // Genre is optional on admin page

            // Check if enabled/disabled status is changing
            var wasEnabled = _editingSong.IsEnabled;
            var willBeEnabled = !_editIsDisabled;
            var statusIsChanging = wasEnabled != willBeEnabled;

            // Validate reason is required when enabling or disabling
            if (statusIsChanging && string.IsNullOrWhiteSpace(_editStatusReason))
            {
                _validationErrors.Add("A reason is required when enabling or disabling a song.");
            }

            // Check for artist name uniqueness across creators
            if (!string.IsNullOrWhiteSpace(_editArtistName))
            {
                // Get the creator ID from the metadata dictionary
                int? songCreatorId = null;
                if (_metadataById.TryGetValue(_editingSong.Id, out var songMetadata))
                {
                    songCreatorId = songMetadata.CreatorId;
                }

                var isArtistNameTaken = await MetadataService.IsArtistNameTakenByAnotherCreatorAsync(_editArtistName.Trim(), songCreatorId);
                if (isArtistNameTaken)
                {
                    var displayName = _editArtistName.Trim();
                    if (displayName.Contains('@'))
                        displayName = displayName.Split('@')[0];
                    _validationErrors.Add($"The artist name '{displayName}' is already used by another creator.");
                }
            }

            if (_validationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            // Handle enable/disable status change
            if (statusIsChanging)
            {
                // Get current user ID for audit trail
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;
                var appUser = await UserManager.GetUserAsync(user);
                var adminUserId = appUser?.Id ?? 0;

                var baseUrl = NavigationManager.BaseUri;

                if (int.TryParse(_editingSong.Id, out var songMetadataId))
                {
                    if (willBeEnabled)
                    {
                        await SongStatusService.EnableSongAsync(songMetadataId, _editStatusReason.Trim(), adminUserId, baseUrl);
                    }
                    else
                    {
                        await SongStatusService.DisableSongAsync(songMetadataId, _editStatusReason.Trim(), adminUserId, baseUrl);
                    }

                    // Update local model
                    _editingSong.IsEnabled = willBeEnabled;
                    _editingSong.StatusReason = _editStatusReason.Trim();
                }
            }

            // Handle cropped image (already uploaded to blob storage by JS)
            if (_cropApplied && !string.IsNullOrEmpty(_cropTargetBlobPath))
            {
                var oldFileName = _editingSong.JpegFileName;
                var newFileName = _cropTargetBlobPath;

                // Delete old blob if it differs from the new one
                if (!string.IsNullOrEmpty(oldFileName) && oldFileName != newFileName)
                {
                    await StorageService.DeleteAsync(oldFileName);
                }

                _editingSong.JpegFileName = newFileName;

                // Update metadata
                SongMetadata existingMetadata = null;
                if (int.TryParse(_editingSong.Id, out var cropSongId))
                {
                    existingMetadata = await MetadataService.GetByIdAsync(cropSongId);
                }
                if (existingMetadata == null && !string.IsNullOrEmpty(oldFileName))
                {
                    existingMetadata = await MetadataService.GetByBlobPathAsync(oldFileName);
                }
                if (existingMetadata == null && !string.IsNullOrEmpty(_editingSong.Mp3FileName))
                {
                    existingMetadata = await MetadataService.GetByBlobPathAsync(_editingSong.Mp3FileName);
                }

                if (existingMetadata != null)
                {
                    existingMetadata.ImageBlobPath = newFileName;
                    existingMetadata.FileExtension = ".png";
                    existingMetadata.IsAlbumCover = false;
                    existingMetadata.Genre = _editGenre;
                    existingMetadata.DisplayOnHomePage = _editDisplayOnHomePage;
                    existingMetadata.ImageWidth = CropOutputSize;
                    existingMetadata.ImageHeight = CropOutputSize;
                    await MetadataService.UpsertAsync(existingMetadata);
                }

                _cropApplied = false;
                _cropTargetBlobPath = null;
                _showCropTool = false;
            }
            // Upload new images if provided
            else if (_songImageFile != null)
            {
                using var stream = _songImageFile.OpenReadStream(maxAllowedSize: _maxImageFileSize);
                
                // Get the file extension from the uploaded file
                var fileExtension = Path.GetExtension(_songImageFile.Name).ToLowerInvariant();
                var contentType = GetImageContentType(fileExtension);
                
                var oldFileName = _editingSong.JpegFileName;
                var newFileName = oldFileName;
                if (string.IsNullOrEmpty(newFileName))
                {
                    // No existing image - construct path in same folder as MP3
                    if (!string.IsNullOrEmpty(_editingSong.Mp3FileName))
                    {
                        var mp3Dir = Path.GetDirectoryName(_editingSong.Mp3FileName)?.Replace("\\", "/");
                        var baseName = Path.GetFileNameWithoutExtension(_editingSong.Mp3FileName);
                        newFileName = string.IsNullOrEmpty(mp3Dir) 
                            ? $"{baseName}{fileExtension}" 
                            : $"{mp3Dir}/{baseName}{fileExtension}";
                    }
                    else
                    {
                        // Fallback to song title if no MP3
                        newFileName = $"{_editingSong.SongTitle}{fileExtension}";
                    }
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

                // Also try by song metadata ID
                if (existingMetadata == null && int.TryParse(_editingSong.Id, out var songId))
                {
                    existingMetadata = await MetadataService.GetByIdAsync(songId);
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

            // Update metadata in database for existing files (only if status didn't change, as status service handles that)
            if (!statusIsChanging)
            {
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
                        // Set artist name (can be empty to clear it)
                        // Strip email domain if artist name contains @ to avoid persisting email addresses
                        var artistNameToSave = _editArtistName;
                        if (!string.IsNullOrWhiteSpace(artistNameToSave) && artistNameToSave.Contains('@'))
                        {
                            artistNameToSave = artistNameToSave.Split('@')[0];
                        }
                        metadata.ArtistName = string.IsNullOrWhiteSpace(artistNameToSave) ? null : artistNameToSave;
                    }

                    // Each upsert awaited sequentially
                    await MetadataService.UpsertAsync(metadata);
                }
            }

            // Update local model
            _editingSong.Genre = _editGenre;
            _editingSong.DisplayOnHomePage = _editDisplayOnHomePage;
            _editingSong.RawArtistName = _editArtistName;

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
            _validationErrors.Add($"Error saving changes: {FileSizeHelper.FormatFileSizeExceptionMessage(ex.Message)}");
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

    protected async Task OpenCropTool()
    {
        if (_editingSong == null || string.IsNullOrEmpty(_editingSong.JpegFileName)) return;

        _cropModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/image-crop-helper.js");
        _showCropTool = true;
        _cropZoom = 50;
        StateHasChanged();
        // Wait for DOM to update before initializing the canvas
        await Task.Delay(100);
        // Use same-origin proxy URL to avoid CORS/tainted-canvas issues
        var proxyUrl = $"api/music/{SafeEncodePath(_editingSong.JpegFileName)}";
        await _cropModule.InvokeVoidAsync("initCropTool", "admin-crop-canvas", proxyUrl, null);
    }

    protected async Task OnCropZoomChanged(int value)
    {
        _cropZoom = value;
        if (_cropModule != null)
        {
            await _cropModule.InvokeVoidAsync("setZoom", value);
        }
    }

    protected async Task ApplyCrop()
    {
        if (_cropModule == null || _editingSong == null) return;

        // Compute the target blob path for the cropped image
        var targetPath = _editingSong.JpegFileName;
        if (string.IsNullOrEmpty(targetPath))
        {
            if (!string.IsNullOrEmpty(_editingSong.Mp3FileName))
            {
                var mp3Dir = Path.GetDirectoryName(_editingSong.Mp3FileName)?.Replace("\\", "/");
                var baseName = Path.GetFileNameWithoutExtension(_editingSong.Mp3FileName);
                targetPath = string.IsNullOrEmpty(mp3Dir)
                    ? $"{baseName}.png"
                    : $"{mp3Dir}/{baseName}.png";
            }
            else
            {
                targetPath = $"{SanitizeFileName(_editingSong.SongTitle)}.png";
            }
        }
        else
        {
            targetPath = Path.ChangeExtension(targetPath, ".png");
        }

        // Build the upload URL for the JS fetch call
        var uploadUrl = $"api/music/upload-cropped-image?blobPath={Uri.EscapeDataString(targetPath)}";

        var success = await _cropModule.InvokeAsync<bool>("getCroppedImageAndUpload", uploadUrl);
        if (success)
        {
            _cropApplied = true;
            _cropTargetBlobPath = targetPath;
        }
        else
        {
            _validationErrors.Add("Failed to upload cropped image. Please try again.");
        }

        _showCropTool = false;
        await _cropModule.InvokeVoidAsync("disposeCropTool");
        StateHasChanged();
    }

    protected async Task CancelCrop()
    {
        _showCropTool = false;
        _cropApplied = false;
        _cropTargetBlobPath = null;
        if (_cropModule != null)
        {
            await _cropModule.InvokeVoidAsync("disposeCropTool");
        }
    }

    /// <summary>
    /// Check image dimensions for all songs with unknown dimensions.
    /// Updates the DB and local view model, then re-renders.
    /// </summary>
    private async Task CheckAllImageDimensions()
    {
        try
        {
            _cropModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/image-crop-helper.js");

            var songsToCheck = _allSongs
                .Where(s => !string.IsNullOrEmpty(s.SongImageUrl) && s.IsImageSquare == null)
                .ToList();

            if (songsToCheck.Count == 0) return;

            bool anyUpdated = false;
            foreach (var song in songsToCheck)
            {
                try
                {
                    var dimensions = await _cropModule.InvokeAsync<ImageDimensions>("checkImageDimensions", song.SongImageUrl);
                    if (dimensions != null)
                    {
                        song.IsImageSquare = dimensions.Width == dimensions.Height;
                        anyUpdated = true;

                        // Persist to DB
                        if (_metadataById.TryGetValue(song.Id, out var metadata))
                        {
                            metadata.ImageWidth = dimensions.Width;
                            metadata.ImageHeight = dimensions.Height;
                            await MetadataService.UpsertAsync(metadata);
                        }
                    }
                }
                catch
                {
                    // Best-effort per image
                }
            }

            if (anyUpdated)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
        catch
        {
            // Best-effort overall
        }
    }

    private async Task DisposeCropTool()
    {
        if (_cropModule != null)
        {
            try
            {
                await _cropModule.InvokeVoidAsync("disposeCropTool");
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeCropTool();
        if (_cropModule != null)
        {
            try
            {
                await _cropModule.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    protected record ImageDimensions(int Width, int Height);

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "image";
        // Remove path traversal sequences and invalid filename characters
        var sanitized = fileName
            .Replace("..", "_")
            .Replace("/", "_")
            .Replace("\\", "_");
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(sanitized) ? "image" : sanitized;
    }

    private static string SafeEncodePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;
        if (filePath.Contains("..") || filePath.Contains("~"))
            return string.Empty;
        var segments = filePath.Split('/');
        var encodedSegments = segments.Select(s => Uri.EscapeDataString(s));
        return string.Join("/", encodedSegments);
    }

    protected async Task LoadGenresAsync()
    {
        _allGenres = await GenreService.GetAllGenresAsync();
        _genreOptions = _allGenres.Where(g => g.IsActive).Select(g => g.Name).ToList();
    }

    protected async Task AddNewGenre()
    {
        if (string.IsNullOrWhiteSpace(_newGenreName))
            return;

        var genre = await GenreService.AddGenreAsync(_newGenreName.Trim(), _currentAdminEmail);
        if (genre != null)
        {
            await LoadGenresAsync();
            _editGenre = genre.Name;
            _newGenreName = string.Empty;
            _showAddGenre = false;

            // Send email to admin about new genre
            try
            {
                var logoHtml = EmailService.GetEmailLogoHtml();
                var body = $@"{logoHtml}
<h2>New Genre Added</h2>
<p>A new genre has been added to StreamTunes:</p>
<ul>
    <li><strong>Genre:</strong> {genre.Name}</li>
    <li><strong>Added by:</strong> {_currentAdminEmail}</li>
    <li><strong>Date:</strong> {genre.CreatedAt:MMMM dd, yyyy 'at' h:mm tt} UTC</li>
</ul>";
                await EmailService.SendEmailAsync("admin@streamtunes.net", $"New Genre Added: {genre.Name}", body);
            }
            catch
            {
                // Best-effort email notification
            }
        }
        else
        {
            _validationErrors.Add($"Genre '{_newGenreName.Trim()}' already exists.");
        }

        StateHasChanged();
    }

    protected async Task DisableGenre(int genreId)
    {
        var genre = _allGenres.FirstOrDefault(g => g.Id == genreId);
        if (genre == null) return;

        var genreName = genre.Name;

        // Disable the genre
        var success = await GenreService.DisableGenreAsync(genreId);
        if (!success) return;

        // Find all songs with this genre and null out the genre, collecting affected user emails
        var affectedSongs = await MetadataService.GetByGenreAsync(genreName);
        var creatorEmails = new HashSet<string>();

        foreach (var song in affectedSongs)
        {
            // Collect creator emails for notification
            if (song.Creator?.User?.Email != null)
            {
                creatorEmails.Add(song.Creator.User.Email);
            }

            song.Genre = null;
            await MetadataService.UpsertAsync(song);
        }

        // Send email to each affected creator
        foreach (var email in creatorEmails)
        {
            try
            {
                var logoHtml = EmailService.GetEmailLogoHtml();
                var body = $@"{logoHtml}
<h2>Genre Disabled</h2>
<p>The genre <strong>{genreName}</strong> has been disabled by an administrator.</p>
<p>Songs that were assigned this genre have had their genre cleared. Please update your songs with a new genre.</p>
<p>If you have any questions, please contact our customer service team at <a href='mailto:{Configuration["EmailSettings:CustomerServiceEmail"]}'>{Configuration["EmailSettings:CustomerServiceEmail"]}</a>.</p>
<p style='color: #999; font-size: 12px;'>
    <a href='{EmailService.GetAppBaseUrl()}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
</p>";
                await EmailService.SendEmailAsync(email, $"Genre Disabled: {genreName}", body);
            }
            catch
            {
                // Best-effort email notification
            }
        }

        // Refresh genres and songs
        await LoadGenresAsync();
        await SongAdminService.RefreshCacheAsync();
        await LoadSongsAsync();
        _totalCount = _allSongs.Count;
        StateHasChanged();
    }

    protected async Task EnableGenre(int genreId)
    {
        var genre = _allGenres.FirstOrDefault(g => g.Id == genreId);
        if (genre == null) return;

        var genreName = genre.Name;

        // Enable the genre
        var success = await GenreService.EnableGenreAsync(genreId);
        if (!success) return;

        // Find creators who had songs with this genre (genre was nulled on disable)
        // We notify creators who added the genre originally
        var creatorEmail = genre.CreatedByEmail;
        if (!string.IsNullOrEmpty(creatorEmail))
        {
            try
            {
                var logoHtml = EmailService.GetEmailLogoHtml();
                var body = $@"{logoHtml}
<h2>Genre Re-Enabled</h2>
<p>The genre <strong>{genreName}</strong> has been re-enabled by an administrator.</p>
<p>You can now assign this genre to your songs again.</p>
<p>If you have any questions, please contact our customer service team at <a href='mailto:{Configuration["EmailSettings:CustomerServiceEmail"]}'>{Configuration["EmailSettings:CustomerServiceEmail"]}</a>.</p>
<p style='color: #999; font-size: 12px;'>
    <a href='{EmailService.GetAppBaseUrl()}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
</p>";
                await EmailService.SendEmailAsync(creatorEmail, $"Genre Re-Enabled: {genreName}", body);
            }
            catch
            {
                // Best-effort email notification
            }
        }

        // Refresh genres
        await LoadGenresAsync();
        StateHasChanged();
    }
}
