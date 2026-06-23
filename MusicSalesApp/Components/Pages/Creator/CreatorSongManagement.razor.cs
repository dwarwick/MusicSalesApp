using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages.Creator;

public partial class CreatorSongManagementModel : BlazorBase, IAsyncDisposable
{
    private long _maxImageFileSize = 10 * 1024 * 1024; // default 10MB, loaded from settings
    private const int CropOutputSize = 800; // Output size in pixels for cropped square images

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
    protected bool _editIsAiGenerated = false;
    protected bool _editIsAiVocals = false;
    protected bool _editIsAiLyrics = false;
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;
    protected IBrowserFile _songImageFile = null;

    // New-image preview (before saving)
    protected string _newSongImagePreviewUrl = null;
    protected bool? _newSongImageIsSquare = null;
    private string _songImageTempPath = null;
    private string _songImageContentType = null;

    protected List<string> _genreOptions = new();
    protected string _newGenreName = string.Empty;
    protected bool _showAddGenre = false;

    // Crop tool fields
    protected bool _showCropTool = false;
    protected bool _cropApplied = false;
    protected string _cropTargetBlobPath = null;
    protected int _cropZoom = 50;
    private IJSObjectReference _cropModule;

    // Persona selection
    protected List<PersonaDropdownItem> _personaOptions = new();
    protected int? _editPersonaId = null;

    protected record PersonaDropdownItem(int? Id, string Name);

    private int? _creatorId;
    private bool _hasLoadedData = false;
    private string _currentUserEmail = string.Empty;

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
                catch (Exception ex)
                {
                    Logger.LogError(ex, "CreatorSongManagement: Failed to load max image upload size setting. Using default.");
                }

                // Load genres from database
                await LoadGenresAsync();

                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    var appUser = await UserManager.GetUserAsync(user);
                    if (appUser != null)
                    {
                        _currentUserEmail = appUser.Email ?? string.Empty;

                        // Get the creator ID for this user
                        _creatorId = await CreatorService.GetCreatorIdForUserAsync(appUser.Id);

                        if (_creatorId.HasValue)
                        {
                            await LoadSongsAsync();
                            await LoadPersonasAsync();
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

                // Check image dimensions after initial render is complete
                if (_songs.Count > 0)
                {
                    await CheckAllImageDimensions();
                }
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
            IsAiGenerated = m.IsAiGenerated,
            IsAiVocals = m.IsAiVocals,
            IsAiLyrics = m.IsAiLyrics,
            CreatorId = m.CreatorId,
            IsActive = m.IsActive,
            IsEnabled = m.IsEnabled,
            StatusReason = m.StatusReason ?? string.Empty,
            NumberOfStreams = m.NumberOfStreams,
            IsImageSquare = m.IsImageSquare,
            PersonaId = m.PersonaId,
            PersonaName = m.Persona?.Name ?? string.Empty
        }).ToList();

        // Generate SAS URLs for images, persona images, and load like counts
        var songIds = _songs
            .Select(s => int.TryParse(s.Id, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        var likeCounts = await SongLikeService.GetBulkLikeCountsAsync(songIds);

        foreach (var song in _songs)
        {
            if (!string.IsNullOrEmpty(song.JpegFileName))
            {
                song.SongImageUrl = AzureStorageService.GetReadSasUri(song.JpegFileName, TimeSpan.FromHours(1)).ToString();
            }
            // Generate persona image SAS URLs
            var personaSong = creatorSongs.FirstOrDefault(m => m.Id.ToString() == song.Id);
            if (personaSong?.Persona != null && !string.IsNullOrEmpty(personaSong.Persona.ImageBlobPath))
            {
                song.PersonaImageUrl = CreatorPersonaService.GetPersonaImageSasUrl(personaSong.Persona.ImageBlobPath, TimeSpan.FromHours(1));
            }

            if (int.TryParse(song.Id, out var songId))
            {
                song.LikeCount = likeCounts.GetValueOrDefault(songId, 0);
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
        _editIsAiGenerated = song.IsAiGenerated;
        _editIsAiVocals = song.IsAiVocals;
        _editIsAiLyrics = song.IsAiLyrics;
        _editPersonaId = song.PersonaId;
        _songImageFile = null;
        _newSongImagePreviewUrl = null;
        _newSongImageIsSquare = null;
        CleanupSongImageTempFile();
        _cropApplied = false;
        _cropTargetBlobPath = null;
        _showCropTool = false;
        _cropZoom = 50;
        _newGenreName = string.Empty;
        _showAddGenre = false;
        _validationErrors.Clear();
        _showEditDialog = true;
    }

    protected async Task LoadGenresAsync()
    {
        var genres = await GenreService.GetActiveGenresAsync();
        _genreOptions = genres.Select(g => g.Name).ToList();
    }

    protected async Task LoadPersonasAsync()
    {
        if (!_creatorId.HasValue) return;
        var personas = await CreatorPersonaService.GetPersonasByCreatorIdAsync(_creatorId.Value);
        _personaOptions = new List<PersonaDropdownItem> { new PersonaDropdownItem(null, "— No Persona —") };
        _personaOptions.AddRange(personas.Select(p => new PersonaDropdownItem(p.Id, p.Name)));
    }

    protected async Task AddNewGenre()
    {
        if (string.IsNullOrWhiteSpace(_newGenreName))
            return;

        var genre = await GenreService.AddGenreAsync(_newGenreName.Trim(), _currentUserEmail);
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
    <li><strong>Added by:</strong> {_currentUserEmail}</li>
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

        await InvokeAsync(StateHasChanged);
    }

    protected async Task CancelEdit()
    {
        _editingSong = null;
        _showEditDialog = false;
        _validationErrors.Clear();
        _songImageFile = null;
        _newSongImagePreviewUrl = null;
        _newSongImageIsSquare = null;
        CleanupSongImageTempFile();
        _cropApplied = false;
        _cropTargetBlobPath = null;
        _showCropTool = false;
        _editArtistName = string.Empty;
        await DisposeCropTool();
    }

    protected async Task HandleSongImageUpload(InputFileChangeEventArgs e)
    {
        _songImageFile = e.File;
        _newSongImagePreviewUrl = null;
        _newSongImageIsSquare = null;
        CleanupSongImageTempFile();
        _cropApplied = false;
        _cropTargetBlobPath = null;

        var fileExtension = System.IO.Path.GetExtension(e.File.Name).ToLowerInvariant();
        if (fileExtension != ".jpg" && fileExtension != ".jpeg" && fileExtension != ".png")
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (e.File.Size > _maxImageFileSize)
        {
            _validationErrors.Add($"Image file is too large. Maximum size is {_maxImageFileSize / (1024 * 1024)} MB.");
            _songImageFile = null;
            await InvokeAsync(StateHasChanged);
            return;
        }

        try
        {
            _songImageContentType = GetImageContentType(fileExtension);

            // Buffer to a temp file BEFORE any StateHasChanged — in .NET 9+ Blazor Server,
            // re-rendering the InputFile component invalidates IBrowserFile references.
            const int bufferSize = 81920;
            var tempPath = System.IO.Path.GetTempFileName();
            await using (var stream = e.File.OpenReadStream(_maxImageFileSize))
            await using (var tempFs = new System.IO.FileStream(tempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, bufferSize, useAsync: true))
            {
                await stream.CopyToAsync(tempFs, bufferSize);
            }
            _songImageTempPath = tempPath;

            // Generate a data-URL for immediate inline preview by reading the temp file back
            var imageBytes = await System.IO.File.ReadAllBytesAsync(tempPath);
            _newSongImagePreviewUrl = $"data:{_songImageContentType};base64,{Convert.ToBase64String(imageBytes)}";

            // Detect whether the selected image is square via JS (best-effort)
            try
            {
                _cropModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/image-crop-helper.js");
                var dimensions = await _cropModule.InvokeAsync<ImageDimensions>("checkImageDimensions", _newSongImagePreviewUrl);
                if (dimensions != null)
                    _newSongImageIsSquare = dimensions.Width == dimensions.Height;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "CreatorSongManagement: Could not determine new image dimensions for preview.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CreatorSongManagement: Failed to buffer song image for preview.");
            CleanupSongImageTempFile();
            _newSongImagePreviewUrl = null;
            _newSongImageIsSquare = null;
        }

        await InvokeAsync(StateHasChanged);
    }

    private void CleanupSongImageTempFile()
    {
        if (!string.IsNullOrEmpty(_songImageTempPath))
        {
            TempFileHelper.TryDelete(_songImageTempPath, Logger);
            _songImageTempPath = null;
        }
        _songImageContentType = null;
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

            // Check for duplicate song title (exclude the current song being edited)
            if (int.TryParse(_editingSong.Id, out var editId))
            {
                var isDuplicateTitle = await SongMetadataService.IsSongTitleDuplicateAsync(_editSongTitle.Trim(), editId);
                if (isDuplicateTitle)
                {
                    _validationErrors.Add($"A song with the title '{_editSongTitle.Trim()}' already exists.");
                    return;
                }
            }

            // Check for artist name uniqueness across creators
            if (!string.IsNullOrWhiteSpace(_editArtistName))
            {
                var isArtistNameTaken = await SongMetadataService.IsArtistNameTakenByAnotherCreatorAsync(_editArtistName.Trim(), _creatorId);
                if (isArtistNameTaken)
                {
                    var displayName = _editArtistName.Trim();
                    if (displayName.Contains('@'))
                        displayName = displayName.Split('@')[0];
                    _validationErrors.Add($"The artist name '{displayName}' is already used by another creator.");
                    return;
                }
            }

            // Get the metadata by ID directly (more reliable than blob path)
            if (int.TryParse(_editingSong.Id, out var metadataId))
            {
                var metadata = await SongMetadataService.GetByIdAsync(metadataId);

                if (metadata != null)
                {
                    var originalTitle = metadata.SongTitle;
                    var artChanged = false;

                    // Handle cropped image (already uploaded to blob storage by JS)
                    if (_cropApplied && !string.IsNullOrEmpty(_cropTargetBlobPath))
                    {
                        var oldFileName = metadata.ImageBlobPath;
                        var newFileName = _cropTargetBlobPath;

                        // Delete old blob if it differs from the new one
                        if (!string.IsNullOrEmpty(oldFileName) && oldFileName != newFileName)
                        {
                            await AzureStorageService.DeleteAsync(oldFileName);
                        }

                        metadata.ImageBlobPath = newFileName;
                        _editingSong.JpegFileName = newFileName;
                        metadata.ImageWidth = CropOutputSize;
                        metadata.ImageHeight = CropOutputSize;
                        artChanged = true;

                        _cropApplied = false;
                        _cropTargetBlobPath = null;
                        _showCropTool = false;
                    }
                    // Handle image upload if provided (_songImageTempPath is set together with _songImageFile)
                    else if (_songImageTempPath != null)
                    {
                        var fileExtension = System.IO.Path.GetExtension(_songImageFile!.Name).ToLowerInvariant();
                        var contentType = _songImageContentType ?? GetImageContentType(fileExtension);
                        
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

                        // Read from temp file (safe after any StateHasChanged since IBrowserFile is no longer needed)
                        await using var tempStream = System.IO.File.OpenRead(_songImageTempPath);
                        await AzureStorageService.UploadAsync(newFileName, tempStream, contentType);
                        
                        // Update metadata with new image path
                        metadata.ImageBlobPath = newFileName;
                        _editingSong.JpegFileName = newFileName;
                        artChanged = true;
                    }

                    // Always update the title, genre, artist name, and persona
                    metadata.SongTitle = _editSongTitle;
                    metadata.Genre = _editGenre;
                    metadata.IsAiGenerated = _editIsAiGenerated;
                    metadata.IsAiVocals = _editIsAiVocals;
                    metadata.IsAiLyrics = _editIsAiLyrics;
                    // Strip email domain if artist name contains @ to avoid persisting email addresses
                    var artistNameToSave = _editArtistName;
                    if (!string.IsNullOrWhiteSpace(artistNameToSave) && artistNameToSave.Contains('@'))
                    {
                        artistNameToSave = artistNameToSave.Split('@')[0];
                    }
                    metadata.ArtistName = string.IsNullOrWhiteSpace(artistNameToSave) ? null : artistNameToSave;
                    metadata.PersonaId = _editPersonaId;

                    await SongMetadataService.UpsertAsync(metadata);

                    // Send admin notifications for rename and art update
                    try
                    {
                        var titleChanged = !string.IsNullOrEmpty(originalTitle)
                            && !string.Equals(originalTitle, _editSongTitle, StringComparison.Ordinal);
                        if (titleChanged && !string.IsNullOrEmpty(_currentUserEmail))
                        {
                            await AdminNotificationService.NotifySongRenamedAsync(
                                _currentUserEmail, originalTitle!, _editSongTitle);
                        }
                        if (artChanged && !string.IsNullOrEmpty(_currentUserEmail))
                        {
                            await AdminNotificationService.NotifySongArtUpdatedAsync(
                                _currentUserEmail, _editSongTitle);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to send admin notification for song edit");
                    }
                    
                    _successMessage = $"'{_editSongTitle}' has been updated successfully.";
                    await LoadSongsAsync();
                    _showEditDialog = false;
                    _editingSong = null;
                    _songImageFile = null;
                    _newSongImagePreviewUrl = null;
                    _newSongImageIsSquare = null;
                    CleanupSongImageTempFile();
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
            _validationErrors.Add($"Error saving changes: {FileSizeHelper.FormatFileSizeExceptionMessage(ex.Message)}");
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

    protected async Task OpenCropTool()
    {
        if (_editingSong == null || string.IsNullOrEmpty(_editingSong.JpegFileName)) return;

        _cropModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/image-crop-helper.js");
        _showCropTool = true;
        _cropZoom = 50;
        await InvokeAsync(StateHasChanged);
        await Task.Delay(100);
        // Use same-origin proxy URL to avoid CORS/tainted-canvas issues
        var proxyUrl = $"api/music/{SafeEncodePath(_editingSong.JpegFileName)}";
        await _cropModule.InvokeVoidAsync("initCropTool", "creator-crop-canvas", proxyUrl, null);
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
                var mp3Dir = System.IO.Path.GetDirectoryName(_editingSong.Mp3FileName)?.Replace("\\", "/");
                var baseName = System.IO.Path.GetFileNameWithoutExtension(_editingSong.Mp3FileName);
                targetPath = string.IsNullOrEmpty(mp3Dir)
                    ? $"{baseName}.png"
                    : $"{mp3Dir}/{baseName}.png";
            }
            else
            {
                targetPath = $"{SanitizeFileName(_editSongTitle)}.png";
            }
        }
        else
        {
            targetPath = System.IO.Path.ChangeExtension(targetPath, ".png");
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
        await InvokeAsync(StateHasChanged);
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

            var songsToCheck = _songs
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
                        if (int.TryParse(song.Id, out var metadataId))
                        {
                            var metadata = await SongMetadataService.GetByIdAsync(metadataId);
                            if (metadata != null)
                            {
                                metadata.ImageWidth = dimensions.Width;
                                metadata.ImageHeight = dimensions.Height;
                                await SongMetadataService.UpsertAsync(metadata);
                            }
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

    protected record ImageDimensions(int Width, int Height);

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
        CleanupSongImageTempFile();
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

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "image";
        var sanitized = fileName
            .Replace("..", "_")
            .Replace("/", "_")
            .Replace("\\", "_");
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
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
}
