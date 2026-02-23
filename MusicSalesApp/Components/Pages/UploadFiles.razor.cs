using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Grids.Internal;
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
    private string _currentUserEmail = null;
    private bool _hasLoadedCreatorId = false;

    // Track upload state for navigation/close warnings and cleanup
    protected bool _isUploading = false;
    private readonly List<string> _uploadedBlobPaths = new();
    private readonly object _blobPathsLock = new();
    private bool _disposed = false;

    // Configuration for chunked uploads
    private const int MaxFilesAllowed = 50;
    private const int ChunkSize = 8;
    private const int ImageOcrChunkSize = 4; // Max images buffered at once during the OCR matching phase
    private const string UploadFailedUserMessage = "There was an issue uploading your files. It is being investigated. Please try again later.";
    private const long MaxOcrImageSizeBytes = 20 * 1024 * 1024; // 20 MB per cover art image

    private static readonly string[] ValidAudioExtensions = MusicFileExtensions.ValidAudioExtensions;
    private static readonly string[] ValidCoverArtExtensions = MusicFileExtensions.ValidCoverArtExtensions;
    
    protected InputFile FileInput { get; set;}

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedCreatorId)
        {
            _hasLoadedCreatorId = true;
            await LoadCreatorIdAsync();
        }
    }

    /// <summary>
    /// Loads the current user's Creator ID and email from the authentication state.
    /// Can be called multiple times safely - always refreshes from the database.
    /// </summary>
    private async Task LoadCreatorIdAsync()
    {
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
                    _currentUserEmail = appUser.Email;
                    Logger.LogInformation("UploadFiles: Loaded CreatorId={CreatorId} for UserId={UserId}, Email={Email}", _currentCreatorId, appUser.Id, _currentUserEmail);
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
            _currentCreatorId = null;
        }
    }

    protected async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        // Clear previous validation errors
        ClearValidationError();
        _uploadItems.Clear();
        _unmatchedCoverArtFiles.Clear();

        // Ensure CreatorId is loaded - retry if not yet available (e.g. race with OnAfterRenderAsync)
        if (_currentCreatorId == null)
        {
            Logger.LogWarning("UploadFiles: CreatorId is null at file selection time. Attempting to reload...");
            await LoadCreatorIdAsync();
        }

        // Final check - if CreatorId is still null, the upload cannot proceed
        if (_currentCreatorId == null)
        {
            var selectedFileNames = string.Join(", ", e.GetMultipleFiles(MaxFilesAllowed).Select(f => f.Name));
            Logger.LogError("UploadFiles: CreatorId is still null after reload for user {Email}. Cannot proceed with upload of: {FileNames}", _currentUserEmail, selectedFileNames);
            await SendCreatorIdFailureEmailAsync(selectedFileNames);
            _validationErrorMessage = UploadFailedUserMessage;
            await InvokeAsync(StateHasChanged);
            return;
        }
        
        // Reset upload tracking for this batch
        _uploadCts = new CancellationTokenSource();
        lock (_blobPathsLock)
        {
            _uploadedBlobPaths.Clear();
        }

        // Show spinner immediately — hide upload box, prevent further drops
        _isUploading = true;
        await InvokeAsync(StateHasChanged);

        var files = e.GetMultipleFiles(MaxFilesAllowed); // Allow up to 50 files

        // Separate files into audio and cover art by original filename
        var audioFilesByName = new Dictionary<string, IBrowserFile>(StringComparer.OrdinalIgnoreCase);
        var coverArtFilesByName = new Dictionary<string, IBrowserFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            if (ValidAudioExtensions.Contains(extension))
                audioFilesByName[file.Name] = file;
            else if (ValidCoverArtExtensions.Contains(extension))
                coverArtFilesByName[file.Name] = file;
        }

        // Show a "matching" status before calling AI
        if (audioFilesByName.Any() && coverArtFilesByName.Any())
        {
            _validationErrorMessage = string.Empty;
            await InvokeAsync(StateHasChanged);
        }

        // Process all image files in chunks of ImageOcrChunkSize (4).
        // Each chunk: filename-only match first (no bytes), then OCR for any still-unmatched
        // images in that chunk. At most ImageOcrChunkSize images are buffered at once.
        var (matchingResult, bufferedOcrImages) = await PerformChunkedMatchingAsync(
            audioFilesByName.Keys.ToList(),
            coverArtFilesByName.Keys.ToList(),
            coverArtFilesByName);

        // Track image files that could not be matched (shown below progress table, no error)
        _unmatchedCoverArtFiles = matchingResult.UnmatchedImageFiles;

        // Build the upload item list.
        // • Images matched by OCR  → already have pre-buffered bytes from Phase 2; use those bytes.
        // • All other images       → store the IBrowserFile reference; buffered lazily at upload time (one at a time).
        var uploadItemsWithFiles = new List<(UploadPairItem Item, IBrowserFile AudioFile, IBrowserFile CoverArtFile, byte[] CoverArtBytes, string CoverArtContentType, string NormalizedName)>();

        foreach (var pair in matchingResult.Pairs)
        {
            audioFilesByName.TryGetValue(pair.AudioFileName, out var audioFile);
            if (audioFile == null)
                continue;

            IBrowserFile coverArtFile = null;
            byte[] coverArtBytes = null;
            string coverArtContentType = null;
            string coverArtFileName = "(No cover art)";
            long coverArtFileSize = 0;

            if (!string.IsNullOrEmpty(pair.ImageFileName))
            {
                if (bufferedOcrImages != null && bufferedOcrImages.TryGetValue(pair.ImageFileName, out var ocrData))
                {
                    // Image was buffered during OCR phase — use pre-buffered bytes
                    coverArtBytes = ocrData.Data;
                    coverArtContentType = ocrData.ContentType;
                }
                else
                {
                    // Image stream not yet read — buffer it just-in-time at upload time
                    coverArtFilesByName.TryGetValue(pair.ImageFileName, out coverArtFile);
                }

                // Metadata for display in the progress table
                if (coverArtFilesByName.TryGetValue(pair.ImageFileName, out var metaFile))
                {
                    coverArtFileName = metaFile.Name;
                    coverArtFileSize = metaFile.Size;
                }
            }

            var normalizedName = string.IsNullOrWhiteSpace(pair.NormalizedName)
                ? MusicUploadService.GetNormalizedBaseName(pair.AudioFileName)
                : pair.NormalizedName;

            var uploadItem = new UploadPairItem
            {
                BaseName = normalizedName,
                AudioFileName = audioFile.Name,
                AudioFileSize = audioFile.Size,
                CoverArtFileName = coverArtFileName,
                CoverArtFileSize = coverArtFileSize,
                HasCoverArt = coverArtFile != null || coverArtBytes != null,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            _uploadItems.Add(uploadItem);
            uploadItemsWithFiles.Add((uploadItem, audioFile, coverArtFile, coverArtBytes, coverArtContentType, normalizedName));
        }

        await InvokeAsync(StateHasChanged);

        // Enable browser warning for navigation away during upload
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
            // Uploads finished (completed or failed) — hide spinner, re-show upload box, disable warnings
            _isUploading = false;
            try
            {
                await JS.InvokeVoidAsync("uploadFilesHelper.disableBeforeUnload");
            }
            catch (JSDisconnectedException) { }
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Processes upload items in chunks to avoid overwhelming the system.
    /// Audio/cover art pairs are kept together within their chunks.
    /// ChunkSize represents the maximum number of files (not pairs) to process concurrently.
    /// </summary>
    private async Task ProcessUploadsInChunksAsync(IEnumerable<(UploadPairItem Item, IBrowserFile AudioFile, IBrowserFile CoverArtFile, byte[] CoverArtBytes, string CoverArtContentType, string NormalizedName)> uploadItemsWithFiles)
    {
        var itemsList = uploadItemsWithFiles.ToList();
        var currentIndex = 0;

        while (currentIndex < itemsList.Count)
        {
            // Build a chunk that respects the max file count
            var chunk = new List<(UploadPairItem Item, IBrowserFile AudioFile, IBrowserFile CoverArtFile, byte[] CoverArtBytes, string CoverArtContentType, string NormalizedName)>();
            var currentFileCount = 0;

            while (currentIndex < itemsList.Count && currentFileCount < ChunkSize)
            {
                var item = itemsList[currentIndex];
                // Count files: 2 for pair (audio + cover art), 1 for audio only
                var fileCount = (item.CoverArtFile != null || item.CoverArtBytes != null) ? 2 : 1;

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
            foreach (var (item, audioFile, coverArtFile, coverArtBytes, coverArtContentType, normalizedName) in chunk)
            {
                if (coverArtFile != null || coverArtBytes != null)
                {
                    chunkTasks.Add(UploadFilePairAsync(audioFile, coverArtFile, coverArtBytes, coverArtContentType, item, normalizedName));
                }
                else
                {
                    chunkTasks.Add(UploadAudioOnlyAsync(audioFile, item, normalizedName));
                }
            }

            // Wait for all uploads in this chunk to complete before starting the next chunk
            await Task.WhenAll(chunkTasks);
        }
    }

    private async Task UploadAudioOnlyAsync(IBrowserFile audioFile, UploadPairItem uploadItem, string normalizedName)
    {
        const long maxFileSize = 100 * 1024 * 1024; // 100 MB
        const int bufferSize = 81920; // 80 KB buffer for better performance with large files

        MemoryStream audioMemoryStream = null;

        try
        {
            // Validate CreatorId before uploading - songs without a creator cannot be tracked or paid
            if (_currentCreatorId == null)
            {
                Logger.LogError("UploadFiles: CreatorId is null for user {Email}. Cannot upload {FileName} without a creator association.", _currentUserEmail, audioFile.Name);
                await SendCreatorIdFailureEmailAsync(audioFile.Name);
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.Progress = 0;
                uploadItem.StatusMessage = "Upload failed";
                uploadItem.ErrorMessage = UploadFailedUserMessage;
                await InvokeAsync(StateHasChanged);
                return;
            }

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

            // Upload without cover art - use the normalized name as the filename so storage uses clean names
            var audioExtension = Path.GetExtension(audioFile.Name).ToLowerInvariant();
            var audioFileNameForStorage = normalizedName + audioExtension;
            var folderPath = await MusicUploadService.UploadMusicWithoutAlbumArtAsync(
                audioMemoryStream,
                audioFileNameForStorage,
                null, // No album name
                _currentCreatorId,
                _uploadCts.Token);

            // Track the uploaded blob path for cleanup if user leaves
            var mp3Path = $"{normalizedName}/{normalizedName}.mp3";
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

    private async Task UploadFilePairAsync(IBrowserFile audioFile, IBrowserFile coverArtFile, byte[] coverArtBytes, string coverArtContentType, UploadPairItem uploadItem, string normalizedName)
    {
        const long maxFileSize = 100 * 1024 * 1024; // 100 MB
        const int bufferSize = 81920; // 80 KB buffer for better performance with large files

        MemoryStream audioMemoryStream = null;
        MemoryStream coverArtMemoryStream = null;

        try
        {
            // Validate CreatorId before uploading - songs without a creator cannot be tracked or paid
            if (_currentCreatorId == null)
            {
                Logger.LogError("UploadFiles: CreatorId is null for user {Email}. Cannot upload audio '{AudioFile}' + cover art '{CoverArtFile}' without a creator association.", _currentUserEmail, audioFile.Name, uploadItem.CoverArtFileName);
                await SendCreatorIdFailureEmailAsync($"{audioFile.Name}, {uploadItem.CoverArtFileName}");
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.Progress = 0;
                uploadItem.StatusMessage = "Upload failed";
                uploadItem.ErrorMessage = UploadFailedUserMessage;
                await InvokeAsync(StateHasChanged);
                return;
            }

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

            if (coverArtBytes != null)
            {
                // OCR-matched image: bytes were pre-buffered during Phase 2 chunked matching.
                coverArtMemoryStream = new MemoryStream(coverArtBytes);
            }
            else
            {
                // Image not pre-buffered: buffer it now — this is the first (and only) read of this
                // IBrowserFile stream, so there is no risk of the "There is no file with ID X" error.
                coverArtMemoryStream = new MemoryStream();
                await using (var coverArtStream = coverArtFile.OpenReadStream(MaxOcrImageSizeBytes))
                {
                    await coverArtStream.CopyToAsync(coverArtMemoryStream, bufferSize, _uploadCts.Token);
                }
                coverArtMemoryStream.Position = 0;

                // Determine content type from the file extension when not already set
                if (string.IsNullOrEmpty(coverArtContentType))
                {
                    var ext = Path.GetExtension(coverArtFile.Name).ToLowerInvariant();
                    coverArtContentType = ext == ".png" ? "image/png" : "image/jpeg";
                }
            }

            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 25;
            await InvokeAsync(StateHasChanged);

            // Delegate to the service with buffered streams - use normalized name for both files
            // so they are stored under the same clean base name regardless of original filenames
            var audioExtension = Path.GetExtension(audioFile.Name).ToLowerInvariant();
            var coverArtExtension = coverArtContentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                _ => Path.GetExtension(uploadItem.CoverArtFileName).ToLowerInvariant() is { Length: > 0 } ext ? ext : ".jpg"
            };
            var audioFileNameForStorage = normalizedName + audioExtension;
            var coverArtFileNameForStorage = normalizedName + coverArtExtension;
            var folderPath = await MusicUploadService.UploadMusicWithAlbumArtAsync(
                audioMemoryStream,
                audioFileNameForStorage,
                coverArtMemoryStream,
                coverArtFileNameForStorage,
                null, // No album name
                _currentCreatorId,
                _uploadCts.Token);

            // Track the uploaded blob paths for cleanup if user leaves
            var mp3Path = $"{normalizedName}/{normalizedName}.mp3";
            var imagePath = $"{normalizedName}/{normalizedName}{coverArtExtension}";
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

    /// <summary>
    /// Processes ALL image files in chunks of <see cref="ImageOcrChunkSize"/> (4).
    /// For each chunk: (a) filename-only matching — no bytes read; then (b) for images still
    /// unmatched in that chunk, buffer their bytes and attempt OCR matching.
    /// At most <see cref="ImageOcrChunkSize"/> images are in memory at any one time.
    /// Bytes for unmatched images in each chunk become GC-eligible immediately after the chunk.
    /// Bytes for OCR-matched images are retained and returned so they can be used during upload
    /// (their IBrowserFile streams have been consumed and cannot be re-read).
    /// </summary>
    private async Task<(FileMatchingResult Result, Dictionary<string, (byte[] Data, string ContentType)> BufferedImages)>
        PerformChunkedMatchingAsync(
            List<string> audioFileNames,
            List<string> allImageFileNames,
            Dictionary<string, IBrowserFile> coverArtFilesByName)
    {
        var allPairs = new List<FilePair>();
        var unmatchedImages = new List<string>();
        var bufferedImages = new Dictionary<string, (byte[] Data, string ContentType)>(StringComparer.OrdinalIgnoreCase);
        var remainingAudioNames = audioFileNames.ToList();

        for (int i = 0; i < allImageFileNames.Count; i += ImageOcrChunkSize)
        {
            if (!remainingAudioNames.Any())
            {
                // All audio already matched — remaining images are unmatched
                unmatchedImages.AddRange(allImageFileNames.Skip(i));
                break;
            }

            var imageChunk = allImageFileNames.Skip(i).Take(ImageOcrChunkSize).ToList();

            // Step A: filename-only match for this chunk (no bytes read)
            FileMatchingResult chunkResult;
            try
            {
                chunkResult = await FileMatchingService.MatchFilesAsync(remainingAudioNames, imageChunk);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "UploadFiles: Filename matching failed for image chunk {ChunkIndex}.", i / ImageOcrChunkSize);
                chunkResult = new FileMatchingResult
                {
                    Pairs = remainingAudioNames.Select(a => new FilePair
                    {
                        AudioFileName = a,
                        ImageFileName = null,
                        NormalizedName = MusicUploadService.GetNormalizedBaseName(a)
                    }).ToList(),
                    UnmatchedImageFiles = imageChunk
                };
            }

            // Collect filename-matched pairs; remove matched audio from the remaining pool
            foreach (var pair in chunkResult.Pairs.Where(p => p.ImageFileName != null))
            {
                allPairs.Add(pair);
                remainingAudioNames.Remove(pair.AudioFileName);
            }

            // Step B: OCR for images still unmatched in this chunk (up to ImageOcrChunkSize bytes at once)
            var stillUnmatched = chunkResult.UnmatchedImageFiles.ToList();
            if (stillUnmatched.Any() && remainingAudioNames.Any())
            {
                var chunkFiles = stillUnmatched
                    .Where(coverArtFilesByName.ContainsKey)
                    .ToDictionary(f => f, f => coverArtFilesByName[f]);

                if (chunkFiles.Any())
                {
                    var chunkData = await BufferCoverArtChunkAsync(chunkFiles);
                    try
                    {
                        var ocrResult = await FileMatchingService.MatchFilesAsync(
                            remainingAudioNames, stillUnmatched, chunkData);

                        foreach (var pair in ocrResult.Pairs.Where(p => p.ImageFileName != null))
                        {
                            allPairs.Add(pair);
                            remainingAudioNames.Remove(pair.AudioFileName);
                            // Retain bytes — IBrowserFile stream already consumed by OCR
                            if (chunkData.TryGetValue(pair.ImageFileName, out var data))
                                bufferedImages[pair.ImageFileName] = data;
                        }
                        unmatchedImages.AddRange(ocrResult.UnmatchedImageFiles);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "UploadFiles: OCR matching failed for image chunk {ChunkIndex}; images will remain unmatched.", i / ImageOcrChunkSize);
                        unmatchedImages.AddRange(stillUnmatched);
                    }
                    // Bytes for unmatched images in chunkData go out of scope → GC-eligible
                }
            }
            else
            {
                unmatchedImages.AddRange(stillUnmatched);
            }
        }

        // Audio files with no image match → audio-only pairs
        foreach (var audio in remainingAudioNames)
        {
            allPairs.Add(new FilePair
            {
                AudioFileName = audio,
                ImageFileName = null,
                NormalizedName = MusicUploadService.GetNormalizedBaseName(audio)
            });
        }

        return (new FileMatchingResult { Pairs = allPairs, UnmatchedImageFiles = unmatchedImages }, bufferedImages);
    }

    /// <summary>
    /// Buffers a subset of cover art files into memory.
    /// Called once per OCR chunk — only <see cref="ImageOcrChunkSize"/> images at a time.
    /// </summary>
    private async Task<Dictionary<string, (byte[] Data, string ContentType)>> BufferCoverArtChunkAsync(
        Dictionary<string, IBrowserFile> coverArtChunk)
    {
        const int bufferSize = 81920;
        var result = new Dictionary<string, (byte[] Data, string ContentType)>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in coverArtChunk)
        {
            try
            {
                using var ms = new MemoryStream();
                await using var stream = kvp.Value.OpenReadStream(MaxOcrImageSizeBytes);
                await stream.CopyToAsync(ms, bufferSize, _uploadCts.Token);
                var extension = Path.GetExtension(kvp.Key).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".png" => "image/png",
                    _ => "image/jpeg"
                };
                result[kvp.Key] = (ms.ToArray(), contentType);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "UploadFiles: Failed to buffer image '{FileName}' for OCR; it will be unmatched.", kvp.Key);
            }
        }

        return result;
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

    protected async Task TriggerFileDialog()
    {
        await JS.InvokeVoidAsync("triggerClick", FileInput.Element);
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

    /// <summary>
    /// Sends an email to admin when a creator upload fails due to null CreatorId.
    /// </summary>
    private async Task SendCreatorIdFailureEmailAsync(string fileNames)
    {
        try
        {
            var adminEmail = Configuration["EmailSettings:AdminEmail"] ?? "admin@streamtunes.net";
            var logoHtml = EmailService.GetEmailLogoHtml();
            var subject = "Upload Failure: Creator ID Not Found";
            var body = $@"
                {logoHtml}
                <h2 style='color: #dc3545;'>Upload Failure: Creator ID Not Found</h2>
                <p>A creator attempted to upload files but their Creator ID could not be determined.</p>
                <table style='border-collapse: collapse; width: 100%; margin: 15px 0;'>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Creator Email</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>{System.Net.WebUtility.HtmlEncode(_currentUserEmail ?? "Unknown")}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Files</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>{System.Net.WebUtility.HtmlEncode(fileNames)}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Error</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>CreatorId is null - the user may not have an active Creator record in the database.</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Timestamp (UTC)</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</td>
                    </tr>
                </table>
                <p>Please investigate why this creator does not have a Creator record associated with their account.</p>";

            await EmailService.SendEmailAsync(adminEmail, subject, body);
            Logger.LogInformation("UploadFiles: Sent admin notification email about null CreatorId for user {Email}", _currentUserEmail);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to send admin notification email about null CreatorId for user {Email}", _currentUserEmail);
        }
    }
}
