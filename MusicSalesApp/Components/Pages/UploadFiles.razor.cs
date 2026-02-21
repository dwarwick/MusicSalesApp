using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Components.Pages;



public class UploadFilesModel : BlazorBase, IAsyncDisposable
{
    private sealed class AntiforgeryTokenResponse
    {
        public string Token { get; set; }
        public string FieldName { get; set; }
    }

    private CancellationTokenSource _uploadCts = new CancellationTokenSource();

    protected List<UploadPairItem> _uploadItems = new List<UploadPairItem>();
    protected string _validationErrorMessage = string.Empty;
    protected List<string> _unmatchedMp3Files = new List<string>();
    protected List<string> _unmatchedCoverArtFiles = new List<string>();
    
    // Creator ID - will be populated if the current user is a creator
    private int? _currentCreatorId = null;
    private bool _hasLoadedCreatorId = false;

    // Track upload state for navigation/close warnings and cleanup
    protected bool _isUploading = false;
    private readonly List<string> _uploadedBlobPaths = new();
    private readonly object _blobPathsLock = new();
    private bool _disposed = false;

    // Configuration for chunked uploads
    private const int MaxFilesAllowed = 50;
    private const int ChunkSize = 8;

    private static readonly string[] ValidAudioExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
    private static readonly string[] ValidCoverArtExtensions = { ".jpeg", ".jpg", ".png" };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedCreatorId)
        {
            _hasLoadedCreatorId = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    var appUser = await UserManager.GetUserAsync(user);
                    if (appUser != null)
                    {
                        // Check if the user is a creator and get their creator ID
                        _currentCreatorId = await CreatorService.GetCreatorIdForUserAsync(appUser.Id);
                        Logger.LogInformation("UploadFiles: Loaded CreatorId={CreatorId} for UserId={UserId}", _currentCreatorId, appUser.Id);
                    }
                    else
                    {
                        Logger.LogWarning("UploadFiles: UserManager.GetUserAsync returned null for authenticated user");
                    }
                }
                else
                {
                    Logger.LogWarning("UploadFiles: User is not authenticated");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "UploadFiles: Failed to determine creator status");
                // If we can't determine creator status, uploads will be assigned to admin
                _currentCreatorId = null;
            }
        }
    }

    protected async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        // Clear previous validation errors
        ClearValidationError();
        _uploadItems.Clear();
        
        // Reset upload tracking for this batch
        _uploadCts = new CancellationTokenSource();
        lock (_blobPathsLock)
        {
            _uploadedBlobPaths.Clear();
        }

        var files = e.GetMultipleFiles(MaxFilesAllowed); // Allow up to 50 files

        // Separate files into audio and cover art
        var audioFiles = new Dictionary<string, IBrowserFile>();
        var coverArtFiles = new Dictionary<string, IBrowserFile>();

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            var baseName = MusicUploadService.GetNormalizedBaseName(file.Name).ToLowerInvariant();

            if (ValidAudioExtensions.Contains(extension))
            {
                audioFiles[baseName] = file;
            }
            else if (ValidCoverArtExtensions.Contains(extension))
            {
                coverArtFiles[baseName] = file;
            }
        }

        // Validate files - cover art is now optional, but orphan cover art files are not allowed
        var fileNames = files.Select(f => f.Name).ToList();
        var validationResult = MusicUploadService.ValidateAllFilePairings(fileNames);

        if (!validationResult.IsValid)
        {
            // Only show error for unmatched cover art (cover art without matching audio)
            // Audio files without cover art are now allowed
            if (validationResult.UnmatchedAlbumArtFiles.Any())
            {
                _validationErrorMessage = "Some cover art files do not have matching audio files with the same base name.";
                _unmatchedCoverArtFiles = validationResult.UnmatchedAlbumArtFiles;
                await InvokeAsync(StateHasChanged);
                return;
            }
        }

        // Build the list of upload items to display
        var uploadItemsWithFiles = new List<(UploadPairItem Item, IBrowserFile AudioFile, IBrowserFile CoverArtFile)>();

        // Create upload items for all audio files (with or without cover art)
        foreach (var audioEntry in audioFiles)
        {
            var audioFile = audioEntry.Value;
            var baseName = MusicUploadService.GetNormalizedBaseName(audioFile.Name);
            
            // Check if there's a matching cover art file
            coverArtFiles.TryGetValue(audioEntry.Key, out var coverArtFile);

            var uploadItem = new UploadPairItem
            {
                BaseName = baseName,
                AudioFileName = audioFile.Name,
                AudioFileSize = audioFile.Size,
                CoverArtFileName = coverArtFile?.Name ?? "(No cover art)",
                CoverArtFileSize = coverArtFile?.Size ?? 0,
                HasCoverArt = coverArtFile != null,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            _uploadItems.Add(uploadItem);
            uploadItemsWithFiles.Add((uploadItem, audioFile, coverArtFile));
        }

        await InvokeAsync(StateHasChanged);

        // Enable upload-in-progress state and browser warning
        _isUploading = true;
        try
        {
            await JS.InvokeVoidAsync("uploadFilesHelper.enableBeforeUnload");
        }
        catch (JSDisconnectedException) { }

        try
        {
            // Process uploads in chunks
            await ProcessUploadsInChunksAsync(uploadItemsWithFiles);
        }
        finally
        {
            // Uploads finished (completed or failed) — disable warnings
            _isUploading = false;
            try
            {
                await JS.InvokeVoidAsync("uploadFilesHelper.disableBeforeUnload");
            }
            catch (JSDisconnectedException) { }
        }
    }

    /// <summary>
    /// Processes upload items in chunks to avoid overwhelming the system.
    /// Audio/cover art pairs are kept together within their chunks.
    /// ChunkSize represents the maximum number of files (not pairs) to process concurrently.
    /// </summary>
    private async Task ProcessUploadsInChunksAsync(IEnumerable<(UploadPairItem Item, IBrowserFile AudioFile, IBrowserFile CoverArtFile)> uploadItemsWithFiles)
    {
        var itemsList = uploadItemsWithFiles.ToList();
        var currentIndex = 0;

        while (currentIndex < itemsList.Count)
        {
            // Build a chunk that respects the max file count
            var chunk = new List<(UploadPairItem Item, IBrowserFile AudioFile, IBrowserFile CoverArtFile)>();
            var currentFileCount = 0;

            while (currentIndex < itemsList.Count && currentFileCount < ChunkSize)
            {
                var item = itemsList[currentIndex];
                // Count files: 2 for pair (audio + cover art), 1 for audio only
                var fileCount = item.CoverArtFile != null ? 2 : 1;

                // Check if adding this item would exceed the limit
                // Always allow at least one item per chunk
                if (currentFileCount + fileCount > ChunkSize && chunk.Count > 0)
                {
                    break;
                }

                chunk.Add(item);
                currentFileCount += fileCount;
                currentIndex++;
            }

            // Start all uploads in this chunk concurrently
            var chunkTasks = new List<Task>();
            foreach (var (item, audioFile, coverArtFile) in chunk)
            {
                if (coverArtFile != null)
                {
                    chunkTasks.Add(UploadFilePairAsync(audioFile, coverArtFile, item));
                }
                else
                {
                    chunkTasks.Add(UploadAudioOnlyAsync(audioFile, item));
                }
            }

            // Wait for all uploads in this chunk to complete before starting the next chunk
            await Task.WhenAll(chunkTasks);
        }
    }

    private async Task UploadAudioOnlyAsync(IBrowserFile audioFile, UploadPairItem uploadItem)
    {
        const long maxFileSize = 100 * 1024 * 1024; // 100 MB
        const int bufferSize = 81920; // 80 KB buffer for better performance with large files

        MemoryStream audioMemoryStream = null;

        try
        {
            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Reading audio file...";
            uploadItem.Progress = 5;
            await InvokeAsync(StateHasChanged);

            // Buffer the audio file
            audioMemoryStream = new MemoryStream();
            await using (var audioStream = audioFile.OpenReadStream(maxFileSize))
            {
                await audioStream.CopyToAsync(audioMemoryStream, bufferSize, _uploadCts.Token);
            }
            audioMemoryStream.Position = 0;

            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 25;
            await InvokeAsync(StateHasChanged);

            // Upload without cover art
            var folderPath = await MusicUploadService.UploadMusicWithoutAlbumArtAsync(
                audioMemoryStream,
                audioFile.Name,
                null, // No album name
                _currentCreatorId,
                _uploadCts.Token);

            // Track the uploaded blob path for cleanup if user leaves
            var baseName = MusicUploadService.GetNormalizedBaseName(audioFile.Name);
            var mp3Path = $"{baseName}/{baseName}.mp3";
            lock (_blobPathsLock)
            {
                _uploadedBlobPaths.Add(mp3Path);
            }

            uploadItem.Progress = 100;
            uploadItem.Status = UploadStatus.Completed;
            uploadItem.StatusMessage = $"Uploaded to {folderPath}";
            uploadItem.ErrorMessage = null;
        }
        catch (InvalidDataException ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Validation failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        finally
        {
            // Dispose memory streams
            audioMemoryStream?.Dispose();
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task UploadFilePairAsync(IBrowserFile audioFile, IBrowserFile coverArtFile, UploadPairItem uploadItem)
    {
        const long maxFileSize = 100 * 1024 * 1024; // 100 MB
        const int bufferSize = 81920; // 80 KB buffer for better performance with large files

        MemoryStream audioMemoryStream = null;
        MemoryStream coverArtMemoryStream = null;

        try
        {
            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Reading audio file...";
            uploadItem.Progress = 5;
            await InvokeAsync(StateHasChanged);

            // Buffer the audio file first to avoid timeout issues with multiple open streams
            // In Blazor Server, BrowserFileStream has a timeout and only one stream can be
            // actively read at a time, so we buffer sequentially into memory.
            audioMemoryStream = new MemoryStream();
            await using (var audioStream = audioFile.OpenReadStream(maxFileSize))
            {
                await audioStream.CopyToAsync(audioMemoryStream, bufferSize, _uploadCts.Token);
            }
            audioMemoryStream.Position = 0;

            uploadItem.StatusMessage = "Reading cover art...";
            uploadItem.Progress = 15;
            await InvokeAsync(StateHasChanged);

            // Now buffer the cover art file
            coverArtMemoryStream = new MemoryStream();
            await using (var coverArtStream = coverArtFile.OpenReadStream(maxFileSize))
            {
                await coverArtStream.CopyToAsync(coverArtMemoryStream, bufferSize, _uploadCts.Token);
            }
            coverArtMemoryStream.Position = 0;

            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 25;
            await InvokeAsync(StateHasChanged);

            // Delegate to the service with buffered streams (no album name)
            var folderPath = await MusicUploadService.UploadMusicWithAlbumArtAsync(
                audioMemoryStream,
                audioFile.Name,
                coverArtMemoryStream,
                coverArtFile.Name,
                null, // No album name
                _currentCreatorId,
                _uploadCts.Token);

            // Track the uploaded blob paths for cleanup if user leaves
            var baseName = MusicUploadService.GetNormalizedBaseName(audioFile.Name);
            var mp3Path = $"{baseName}/{baseName}.mp3";
            var coverArtExtension = Path.GetExtension(coverArtFile.Name).ToLowerInvariant();
            var imagePath = $"{baseName}/{baseName}{coverArtExtension}";
            lock (_blobPathsLock)
            {
                _uploadedBlobPaths.Add(mp3Path);
                _uploadedBlobPaths.Add(imagePath);
            }

            uploadItem.Progress = 100;
            uploadItem.Status = UploadStatus.Completed;
            uploadItem.StatusMessage = $"Uploaded to {folderPath}";
            uploadItem.ErrorMessage = null;
        }
        catch (InvalidDataException ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Validation failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        finally
        {
            // Dispose memory streams
            audioMemoryStream?.Dispose();
            coverArtMemoryStream?.Dispose();
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void ClearValidationError()
    {
        _validationErrorMessage = string.Empty;
        _unmatchedMp3Files.Clear();
        _unmatchedCoverArtFiles.Clear();
    }

    protected string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    protected string GetProgressBarClass(UploadStatus status)
    {
        return status switch
        {
            UploadStatus.Completed => "bg-success",
            UploadStatus.Failed => "bg-danger",
            UploadStatus.Converting => "bg-info progress-bar-striped progress-bar-animated",
            UploadStatus.Uploading => "bg-primary progress-bar-striped progress-bar-animated",
            _ => "bg-secondary"
        };
    }

    protected class UploadPairItem
    {
        public string BaseName { get; set; } = string.Empty;
        public string AudioFileName { get; set; } = string.Empty;
        public long AudioFileSize { get; set; }
        public string CoverArtFileName { get; set; } = string.Empty;
        public long CoverArtFileSize { get; set; }
        public bool HasCoverArt { get; set; }
        public UploadStatus Status { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public int Progress { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    protected enum UploadStatus
    {
        Pending,
        Uploading,
        Converting,
        Completed,
        Failed
    }

    /// <summary>
    /// Intercepts in-app navigation while uploads are in progress.
    /// Shows a confirmation dialog — if the user confirms leaving,
    /// cancels pending uploads and cleans up already-uploaded files.
    /// </summary>
    protected async Task OnBeforeInternalNavigation(LocationChangingContext context)
    {
        if (!_isUploading)
            return;

        var isConfirmed = await JS.InvokeAsync<bool>("confirm",
            "Uploads are in progress. If you leave now, all uploads will be cancelled and any files already uploaded in this session will be removed. Are you sure you want to leave?");

        if (isConfirmed)
        {
            // User chose to leave — cancel pending uploads and clean up
            _uploadCts.Cancel();
            await CleanupUploadedFilesAsync();
        }
        else
        {
            // User chose to stay — prevent navigation
            context.PreventNavigation();
        }
    }

    /// <summary>
    /// Deletes all blobs uploaded during this session from Azure Blob Storage
    /// and sets their SongMetadata records to inactive and not enabled.
    /// </summary>
    private async Task CleanupUploadedFilesAsync()
    {
        List<string> pathsToCleanup;
        lock (_blobPathsLock)
        {
            pathsToCleanup = new List<string>(_uploadedBlobPaths);
            _uploadedBlobPaths.Clear();
        }

        foreach (var blobPath in pathsToCleanup)
        {
            try
            {
                // Delete the blob from Azure Storage
                await AzureStorageService.DeleteAsync(blobPath);

                // Set the SongMetadata record to inactive and not enabled
                await SongMetadataService.DeactivateByBlobPathAsync(blobPath,
                    "Upload cancelled — user left before uploads completed.");
            }
            catch
            {
                // Best-effort cleanup — don't throw if a single file fails
            }
        }
    }

    /// <summary>
    /// Handles circuit disconnection (e.g., browser tab closed during upload).
    /// Cancels pending uploads and removes already-uploaded files.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_isUploading)
        {
            _uploadCts.Cancel();
            await CleanupUploadedFilesAsync();
        }

        _uploadCts.Dispose();
    }
}
