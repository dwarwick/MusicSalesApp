using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Components.Pages.Creator;



public class UploadFilesModel : BlazorBase, IAsyncDisposable
{
    private sealed class AntiforgeryTokenResponse
    {
        public string Token { get; set; }
        public string FieldName { get; set; }
    }

    protected sealed class InitialUploadProgressItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public long Size { get; set; }
        public string FormattedSize { get; set; } = string.Empty;
        public int Progress { get; set; }
        public string StatusMessage { get; set; } = "Waiting...";
    }

    private sealed class InitialUploadProgressState
    {
        public long TotalBytes { get; set; }
        public long BytesReceived { get; set; }
        public DateTime LastReportedAtUtc { get; set; } = DateTime.MinValue;
    }

    private CancellationTokenSource _uploadCts = new CancellationTokenSource();

    protected List<UploadPairItem> _uploadItems = new List<UploadPairItem>();
    protected List<InitialUploadProgressItem> _initialUploadItems = new List<InitialUploadProgressItem>();
    protected string _initialUploadStatusMessage = string.Empty;
    protected int _initialUploadBatchProgress = 0;
    protected string _validationErrorMessage = string.Empty;
    protected List<string> _unmatchedMp3Files = new List<string>();
    protected List<string> _unmatchedCoverArtFiles = new List<string>();
    protected List<string> _duplicateSongFiles = new List<string>();

    /// <summary>Selected files whose extension is not a supported audio or cover-art type.</summary>
    protected List<string> _skippedFiles = new List<string>();

    // Titles are no longer derived from filenames, so the batch pauses after matching to let the
    // creator review and edit each title. The buffered temp files have to outlive that pause,
    // which is why they are held here rather than in a local `finally`.
    protected bool _awaitingTitleConfirmation = false;
    private readonly List<PendingUpload> _pendingUploads = new();
    private readonly List<string> _pendingTempFiles = new();

    private sealed record PendingUpload(
        UploadPairItem Item,
        string AudioTempPath,
        string CoverArtTempPath,
        string CoverArtContentType,
        string PlaybackTempPath,
        double ValidatedDuration);


    // Creator ID - will be populated if the current user is a creator
    private int? _currentCreatorId = null;
    private string _currentUserEmail = null;
    private bool _hasLoadedCreatorId = false;

    // Track upload state for navigation/close warnings and cleanup
    protected bool _isUploading = false;
    protected bool _isProcessingFiles = false;
    private readonly List<string> _uploadedBlobPaths = new();
    private readonly object _blobPathsLock = new();
    private bool _disposed = false;

    // Configuration for chunked uploads
    private const int MaxFilesAllowed = 50;
    private const int ChunkSize = 8;
    private const int ImageOcrChunkSize = 4; // Max images buffered at once during the OCR matching phase
    private const string UploadFailedUserMessage = "There was an issue uploading your files. It is being investigated. Please try again later.";
    private static readonly TimeSpan InitialUploadProgressUpdateInterval = TimeSpan.FromSeconds(1);

    private long _maxAudioFileSize = 100 * 1024 * 1024; // default 100 MB, loaded from settings
    protected int _maxAudioUploadSizeMBDisplay = 100; // MB value for display in UI
    private long _maxImageFileSize = 20 * 1024 * 1024; // default 20 MB, loaded from settings
    protected int _maxImageUploadSizeMBDisplay = 20; // MB value for display in UI

    private static readonly string[] ValidAudioExtensions = MusicFileExtensions.ValidAudioExtensions;
    private static readonly string[] ValidCoverArtExtensions = MusicFileExtensions.ValidCoverArtExtensions;
    
    protected InputFile FileInput { get; set;}

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedCreatorId)
        {
            _hasLoadedCreatorId = true;
            await LoadCreatorIdAsync();
            await LoadMaxAudioFileSizeAsync();
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

    /// <summary>
    /// Loads the maximum audio and image file upload sizes from the application settings.
    /// </summary>
    private async Task LoadMaxAudioFileSizeAsync()
    {
        try
        {
            var audioSizeMB = await AppSettingsService.GetMaxAudioUploadSizeMBAsync();
            _maxAudioFileSize = (long)audioSizeMB * 1024 * 1024;
            _maxAudioUploadSizeMBDisplay = audioSizeMB;

            var imageSizeMB = await AppSettingsService.GetMaxImageUploadSizeMBAsync();
            _maxImageFileSize = (long)imageSizeMB * 1024 * 1024;
            _maxImageUploadSizeMBDisplay = imageSizeMB;

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to load max upload size settings. Using defaults.");
        }
    }

    protected async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        // Guard against concurrent invocations — in .NET 9+ Blazor, a second change event
        // on InputFile invalidates file references from the first event. Drop events can
        // trigger multiple change events, so we must process only the first one.
        if (_isProcessingFiles)
            return;
        _isProcessingFiles = true;

        // Track temp files for centralized cleanup — declared outside try so
        // the finally block can always reach them.
        var audioTempPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var coverArtTempPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The MP3s the preflight transcodes, kept so the upload does not transcode again.
        // Only populated for non-MP3 sources.
        var playbackTempPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var validatedDurations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var beforeUnloadEnabled = false;

        try
        {
        // A batch still awaiting title review is abandoned by this new selection, so release the
        // files it was holding rather than leaking them on the server.
        _awaitingTitleConfirmation = false;
        CleanupPendingTempFiles();

        // Clear previous validation errors
        ClearValidationError();
        _uploadItems.Clear();
        _initialUploadItems.Clear();
        _initialUploadStatusMessage = string.Empty;
        _initialUploadBatchProgress = 0;
        _unmatchedCoverArtFiles.Clear();
        _duplicateSongFiles.Clear();
        _skippedFiles.Clear();

        // Ensure CreatorId is loaded
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

        try
        {
            await EnableBeforeUnloadAsync();
            beforeUnloadEnabled = true;
        }
        catch (JSDisconnectedException) { }

        // Capture file references BEFORE showing the spinner / calling StateHasChanged.
        // In .NET 9+ Blazor Server, calling StateHasChanged before GetMultipleFiles can
        // invalidate IBrowserFile references, causing "There is no file with ID X" errors
        // when OpenReadStream is later called on an audio-only upload.
        var files = e.GetMultipleFiles(MaxFilesAllowed); // Allow up to 50 files

        // Filenames themselves are unconstrained now that storage paths come from a GUID, so a file
        // with an unsupported extension is simply skipped rather than failing the whole batch.
        _skippedFiles.AddRange(files
            .Where(file => !MusicFileExtensions.IsAudioFile(file.Name)
                && !MusicFileExtensions.IsCoverArtFile(file.Name))
            .Select(file => file.Name));

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

        _initialUploadItems = BuildInitialUploadProgressFiles(audioFilesByName.Values, coverArtFilesByName.Values);
        var initialUploadItemsByName = _initialUploadItems.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var initialUploadProgress = new InitialUploadProgressState
        {
            TotalBytes = _initialUploadItems.Sum(f => f.Size)
        };

        if (_initialUploadItems.Any())
        {
            _isUploading = true;
            _initialUploadStatusMessage = _initialUploadItems.Count == 1
                ? "Receiving 1 file..."
                : $"Receiving {_initialUploadItems.Count} files...";
            await InvokeAsync(StateHasChanged);
        }

        // Buffer ALL IBrowserFile streams to temp files BEFORE calling StateHasChanged.
        // In .NET 9+ Blazor Server, re-rendering the InputFile component clears the
        // JS-side file list, invalidating all IBrowserFile references from the current
        // selection. After buffering to disk, re-renders are safe.

        try
        {
            foreach (var kvp in audioFilesByName)
            {
                var tempPath = Path.GetTempFileName();
                audioTempPaths[kvp.Key] = tempPath;
                initialUploadItemsByName.TryGetValue(kvp.Key, out var initialUploadItem);
                await BufferBrowserFileToTempFileAsync(kvp.Value, tempPath, _maxAudioFileSize, initialUploadItem, initialUploadProgress);
            }

            foreach (var kvp in coverArtFilesByName)
            {
                var tempPath = Path.GetTempFileName();
                coverArtTempPaths[kvp.Key] = tempPath;
                initialUploadItemsByName.TryGetValue(kvp.Key, out var initialUploadItem);
                await BufferBrowserFileToTempFileAsync(kvp.Value, tempPath, _maxImageFileSize, initialUploadItem, initialUploadProgress);
            }
        }
        catch (InvalidDataException ex)
        {
            Logger.LogWarning(ex, "UploadFiles: File transfer was incomplete.");
            _validationErrorMessage = "No files were uploaded. The upload was interrupted before it finished — please try again.";
            _isUploading = false;
            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
            return;
        }


        // Preflight the complete batch before any upload task is started. Every audio file is
        // fully decoded here, which for a long track is the slowest part of the whole flow, so
        // report progress rather than appearing to hang.
        var invalidContentFiles = new List<string>();
        var decoderInfrastructureFailures = new List<string>();
        var validatedCount = 0;
        foreach (var file in audioFilesByName)
        {
            validatedCount++;
            _initialUploadStatusMessage =
                $"Checking that your audio plays ({validatedCount} of {audioFilesByName.Count}): {file.Key}";
            _initialUploadBatchProgress = (int)(100.0 * (validatedCount - 1) / audioFilesByName.Count);
            await InvokeAsync(StateHasChanged);

            await using var stream = File.OpenRead(audioTempPaths[file.Key]);
            if (!MediaFileContentValidator.AudioContentMatchesExtension(stream, file.Key, out _))
            {
                invalidContentFiles.Add(file.Key);
                continue;
            }

            stream.Position = 0;
            var decode = await MusicService.ValidateAudioDecodeAsync(
                stream,
                file.Key,
                _uploadCts.Token);
            if (decode.Status == AudioDecodeStatus.Inconclusive)
            {
                decoderInfrastructureFailures.Add($"{file.Key} ({decode.FailureCode})");
                continue;
            }
            if (decode.Status != AudioDecodeStatus.Playable)
            {
                invalidContentFiles.Add(file.Key);
                continue;
            }

            if (MusicService.IsMp3File(file.Key))
            {
                // The upload can stream this straight through; reuse the duration just measured.
                validatedDurations[file.Key] = decode.Duration ?? 0;
                continue;
            }

            // Prove that every non-MP3 source can be converted before any task is
            // allowed to write this batch to Azure, and keep the result: converting again
            // during the upload would double the transcoding work for the whole batch.
            try
            {
                stream.Position = 0;
                await using var converted = await MusicService.ConvertToMp3Async(stream, file.Key);
                if (!MediaFileContentValidator.AudioContentMatchesExtension(converted, "preflight.mp3", out _))
                {
                    invalidContentFiles.Add(file.Key);
                    continue;
                }

                converted.Position = 0;
                var convertedDecode = await MusicService.ValidateAudioDecodeAsync(
                    converted,
                    "preflight.mp3",
                    _uploadCts.Token);
                if (convertedDecode.Status == AudioDecodeStatus.Inconclusive)
                {
                    decoderInfrastructureFailures.Add($"{file.Key} ({convertedDecode.FailureCode})");
                    continue;
                }
                if (convertedDecode.Status != AudioDecodeStatus.Playable)
                {
                    invalidContentFiles.Add(file.Key);
                    continue;
                }

                var playbackTempPath = Path.GetTempFileName();
                converted.Position = 0;
                await using (var playbackFile = File.Create(playbackTempPath))
                {
                    await converted.CopyToAsync(playbackFile, _uploadCts.Token);
                }

                playbackTempPaths[file.Key] = playbackTempPath;
                validatedDurations[file.Key] = convertedDecode.Duration ?? 0;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Upload preflight conversion failed for {FileName}", file.Key);
                invalidContentFiles.Add(file.Key);
            }
        }
        foreach (var file in coverArtFilesByName)
        {
            await using var stream = File.OpenRead(coverArtTempPaths[file.Key]);
            if (!MediaFileContentValidator.ImageContentMatchesExtension(stream, file.Key, out _))
            {
                invalidContentFiles.Add(file.Key);
            }
        }
        if (decoderInfrastructureFailures.Count > 0)
        {
            _validationErrorMessage =
                "No files were uploaded because the media decoder could not complete validation. "
                + "Please retry after the server issue is resolved: "
                + string.Join(", ", decoderInfrastructureFailures);
            _isUploading = false;
            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (invalidContentFiles.Count > 0)
        {
            _validationErrorMessage =
                "No files were uploaded. These files are corrupt or do not match their extensions: "
                + string.Join(", ", invalidContentFiles);
            _isUploading = false;
            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        _initialUploadStatusMessage = "Files received. Matching cover art...";
        _initialUploadBatchProgress = 100;
        await InvokeAsync(StateHasChanged);

        // Process all image files in chunks of ImageOcrChunkSize (4).
        // Each chunk: filename-only match first (no bytes), then OCR for any still-unmatched
        // images in that chunk. At most ImageOcrChunkSize image bytes are in memory at once.
        var matchingResult = await PerformChunkedMatchingAsync(
            audioFilesByName.Keys.ToList(),
            coverArtFilesByName.Keys.ToList(),
            coverArtTempPaths);

        // Track image files that could not be matched (shown below progress table, no error)
        _unmatchedCoverArtFiles = matchingResult.UnmatchedImageFiles;

        // Build the upload item list. All files are already on disk as temp files,
        // so we just pass temp paths through to the upload methods.
        var uploadItemsWithFiles = new List<PendingUpload>();

        foreach (var pair in matchingResult.Pairs)
        {
            if (!audioTempPaths.TryGetValue(pair.AudioFileName, out var audioTempPath))
                continue;

            audioFilesByName.TryGetValue(pair.AudioFileName, out var audioFileMeta);
            if (audioFileMeta == null)
                continue;

            string coverArtTempPath = null;
            string coverArtContentType = null;
            string coverArtFileName = "(No cover art)";
            long coverArtFileSize = 0;

            if (!string.IsNullOrEmpty(pair.ImageFileName))
            {
                coverArtTempPaths.TryGetValue(pair.ImageFileName, out coverArtTempPath);

                // Determine content type from file extension
                var ext = Path.GetExtension(pair.ImageFileName).ToLowerInvariant();
                coverArtContentType = ext == ".png" ? "image/png" : "image/jpeg";

                // Metadata for display in the progress table
                if (coverArtFilesByName.TryGetValue(pair.ImageFileName, out var metaFile))
                {
                    coverArtFileName = metaFile.Name;
                    coverArtFileSize = metaFile.Size;
                }
            }

            // The filename no longer determines storage. It only seeds the title, which the
            // creator can edit in the review step before anything is uploaded.
            var uploadItem = new UploadPairItem
            {
                SongTitle = SongTitleHelper.FromFileName(pair.AudioFileName),
                AudioFileName = audioFileMeta.Name,
                AudioFileSize = audioFileMeta.Size,
                CoverArtFileName = coverArtFileName,
                CoverArtFileSize = coverArtFileSize,
                HasCoverArt = coverArtTempPath != null,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            _uploadItems.Add(uploadItem);
            playbackTempPaths.TryGetValue(pair.AudioFileName, out var playbackTempPath);
            validatedDurations.TryGetValue(pair.AudioFileName, out var validatedDuration);
            uploadItemsWithFiles.Add(new PendingUpload(
                uploadItem,
                audioTempPath,
                coverArtTempPath,
                coverArtContentType,
                playbackTempPath,
                validatedDuration));
        }

        _initialUploadItems.Clear();
        _initialUploadStatusMessage = string.Empty;
        _initialUploadBatchProgress = 0;

        if (!uploadItemsWithFiles.Any())
        {
            _isUploading = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Ownership of the buffered files transfers to the fields below, so this method's
        // `finally` must no longer delete them.
        _pendingUploads.AddRange(uploadItemsWithFiles);
        _pendingTempFiles.AddRange(audioTempPaths.Values);
        _pendingTempFiles.AddRange(coverArtTempPaths.Values);
        _pendingTempFiles.AddRange(playbackTempPaths.Values);
        audioTempPaths.Clear();
        coverArtTempPaths.Clear();
        playbackTempPaths.Clear();

        // Upload without asking when the titles taken from the filenames are all usable. The
        // creator only has to intervene when one is blank, too long, or collides with another
        // song - the cases where uploading first and fixing later would mean a failed upload.
        if (await PendingTitlesNeedAttentionAsync())
        {
            _awaitingTitleConfirmation = true;
            _isUploading = false;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await InvokeAsync(StateHasChanged);
        await RunPendingUploadsAsync();
        beforeUnloadEnabled = false;
        }
        catch (JSException ex) when (
            ex.Message.Contains("There is no file with ID", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("file list may have changed", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning(ex, "UploadFiles: Browser file list changed before selected files could be buffered.");
            _isUploading = false;
            _validationErrorMessage = "The browser changed the selected file list before StreamTunes could read it. Please drop the files again.";

            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            _isProcessingFiles = false;
            // If an error occurred after showing the spinner via JS but before
            // Blazor took over (_isUploading was set), sync the render state so
            // the spinner is hidden and the upload box is re-enabled.
            if (!_isUploading)
            {
                try { await InvokeAsync(StateHasChanged); }
                catch { }
            }

            if (beforeUnloadEnabled)
            {
                await DisableBeforeUnloadAsync();
            }

            if (!_isUploading)
            {
                _initialUploadItems.Clear();
                _initialUploadStatusMessage = string.Empty;
                _initialUploadBatchProgress = 0;
            }

            // Clean up ALL temp files (safe to call even if already deleted).
            // On the success path these dictionaries were emptied when ownership of the buffered
            // files transferred to the review step, so nothing is deleted out from under it.
            foreach (var path in audioTempPaths.Values)
                TempFileHelper.TryDelete(path, Logger);
            foreach (var path in coverArtTempPaths.Values)
                TempFileHelper.TryDelete(path, Logger);
            foreach (var path in playbackTempPaths.Values)
                TempFileHelper.TryDelete(path, Logger);
        }
    }

    /// <summary>
    /// Checks the titles taken from the filenames and marks any that the creator has to fix.
    /// Returns true when the batch cannot upload as-is.
    /// </summary>
    private async Task<bool> PendingTitlesNeedAttentionAsync()
    {
        foreach (var pending in _pendingUploads)
        {
            pending.Item.SongTitle = (pending.Item.SongTitle ?? string.Empty).Trim();
            pending.Item.TitleError = null;
        }

        foreach (var pending in _pendingUploads)
        {
            var error = SongTitleHelper.GetTitleValidationErrors(pending.Item.SongTitle).FirstOrDefault();
            if (error != null)
            {
                pending.Item.TitleError = error;
            }
        }

        foreach (var collision in _pendingUploads
            .Where(pending => pending.Item.TitleError == null)
            .GroupBy(pending => pending.Item.SongTitle, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group))
        {
            collision.Item.TitleError = "Another file in this batch has the same title.";
        }

        try
        {
            var existingTitles = await SongMetadataService.FindExistingSongTitlesAsync(
                _pendingUploads
                    .Where(pending => pending.Item.TitleError == null)
                    .Select(pending => pending.Item.SongTitle)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

            foreach (var duplicate in _pendingUploads
                .Where(pending => pending.Item.TitleError == null
                    && existingTitles.Contains(pending.Item.SongTitle)))
            {
                duplicate.Item.TitleError = "You already have a song with this title.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to check for duplicate song titles.");
            _validationErrorMessage =
                "No files were uploaded because existing song titles could not be checked. Please try again.";
            return true;
        }

        var problems = _pendingUploads.Count(pending => pending.Item.TitleError != null);
        if (problems == 0)
            return false;

        _validationErrorMessage = problems == 1
            ? "One song needs a different title before this batch can upload. It is highlighted below."
            : $"{problems} songs need different titles before this batch can upload. They are highlighted below.";
        return true;
    }

    /// <summary>
    /// Uploads a batch the creator had to correct first. Only reachable from the review step.
    /// </summary>
    protected async Task StartUploadAsync()
    {
        if (!_awaitingTitleConfirmation || _isUploading)
            return;

        ClearValidationError();

        if (await PendingTitlesNeedAttentionAsync())
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        _awaitingTitleConfirmation = false;
        await RunPendingUploadsAsync();
    }

    /// <summary>
    /// Runs the buffered batch. Titles have already been checked by this point.
    /// </summary>
    private async Task RunPendingUploadsAsync()
    {
        _isUploading = true;
        _uploadCts = new CancellationTokenSource();
        lock (_blobPathsLock)
        {
            _uploadedBlobPaths.Clear();
        }

        var beforeUnloadEnabled = false;
        try
        {
            try
            {
                await EnableBeforeUnloadAsync();
                beforeUnloadEnabled = true;
            }
            catch (JSDisconnectedException) { }

            var uploads = _pendingUploads.ToList();
            if (!uploads.Any())
                return;

            await ProcessUploadsInChunksAsync(uploads);

            // Send batch upload notification after all uploads complete
            try
            {
                // Use the actual MP3 blob paths tracked during upload for reliable matching
                List<string> uploadedMp3Paths;
                lock (_blobPathsLock)
                {
                    uploadedMp3Paths = _uploadedBlobPaths
                        .Where(p => p.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                if (uploadedMp3Paths.Any() && !string.IsNullOrEmpty(_currentUserEmail) && _currentCreatorId.HasValue)
                {
                    await AdminNotificationService.NotifyUploadBatchCompletedAsync(
                        _currentUserEmail, _currentCreatorId.Value, uploadedMp3Paths);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to send batch upload notification");
            }
        }
        finally
        {
            _isUploading = false;
            CleanupPendingTempFiles();

            if (beforeUnloadEnabled)
            {
                await DisableBeforeUnloadAsync();
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Discards a reviewed-but-not-yet-uploaded batch.</summary>
    protected async Task CancelPendingBatchAsync()
    {
        if (!_awaitingTitleConfirmation)
            return;

        _awaitingTitleConfirmation = false;
        CleanupPendingTempFiles();
        _uploadItems.Clear();
        _unmatchedCoverArtFiles.Clear();
        _skippedFiles.Clear();
        ClearValidationError();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Deletes the buffered files held for the review step. Safe to call repeatedly, and reached
    /// from every exit: upload completion, cancel, navigation away, and disposal.
    /// </summary>
    private void CleanupPendingTempFiles()
    {
        foreach (var path in _pendingTempFiles)
            TempFileHelper.TryDelete(path, Logger);

        _pendingTempFiles.Clear();
        _pendingUploads.Clear();
    }

    private List<InitialUploadProgressItem> BuildInitialUploadProgressFiles(
        IEnumerable<IBrowserFile> audioFiles,
        IEnumerable<IBrowserFile> coverArtFiles)
    {
        var files = new List<InitialUploadProgressItem>();
        var index = 0;

        foreach (var file in audioFiles)
        {
            files.Add(new InitialUploadProgressItem
            {
                Index = index++,
                Name = file.Name,
                FileType = "Audio",
                Size = file.Size,
                FormattedSize = FormatFileSize(file.Size),
                Progress = 0,
                StatusMessage = "Waiting..."
            });
        }

        foreach (var file in coverArtFiles)
        {
            files.Add(new InitialUploadProgressItem
            {
                Index = index++,
                Name = file.Name,
                FileType = "Cover Art",
                Size = file.Size,
                FormattedSize = FormatFileSize(file.Size),
                Progress = 0,
                StatusMessage = "Waiting..."
            });
        }

        return files;
    }

    private async Task BufferBrowserFileToTempFileAsync(
        IBrowserFile browserFile,
        string tempPath,
        long maxAllowedSize,
        InitialUploadProgressItem progressItem,
        InitialUploadProgressState progressState)
    {
        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        long fileBytesReceived = 0;

        await ReportInitialUploadProgressAsync(progressItem, fileBytesReceived, browserFile.Size, progressState, "Receiving...", force: true);

        await using var browserStream = browserFile.OpenReadStream(maxAllowedSize);
        await using var tempFileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        while (true)
        {
            var bytesRead = await browserStream.ReadAsync(buffer.AsMemory(0, buffer.Length), _uploadCts.Token);
            if (bytesRead == 0)
            {
                break;
            }

            await tempFileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _uploadCts.Token);
            fileBytesReceived += bytesRead;
            progressState.BytesReceived += bytesRead;

            await ReportInitialUploadProgressAsync(progressItem, fileBytesReceived, browserFile.Size, progressState, "Receiving...");
        }

        await ReportInitialUploadProgressAsync(progressItem, fileBytesReceived, browserFile.Size, progressState, "Received", force: true);
        MediaTransferValidator.RequireComplete(browserFile.Name, browserFile.Size, fileBytesReceived);
    }

    private async Task ReportInitialUploadProgressAsync(
        InitialUploadProgressItem progressItem,
        long fileBytesReceived,
        long fileSize,
        InitialUploadProgressState progressState,
        string statusText,
        bool force = false)
    {
        if (progressItem == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - progressState.LastReportedAtUtc < InitialUploadProgressUpdateInterval)
        {
            return;
        }

        progressState.LastReportedAtUtc = now;
        var filePercent = CalculatePercent(fileBytesReceived, fileSize);
        var batchPercent = CalculatePercent(progressState.BytesReceived, progressState.TotalBytes);

        progressItem.Progress = filePercent;
        progressItem.StatusMessage = statusText;
        _initialUploadBatchProgress = batchPercent;
        _initialUploadStatusMessage = statusText == "Received"
            ? $"Received {progressItem.Name}"
            : $"Receiving {progressItem.Name}...";

        await InvokeAsync(StateHasChanged);
    }

    private static int CalculatePercent(long current, long total)
    {
        if (total <= 0)
        {
            return 100;
        }

        return (int)Math.Clamp(Math.Round(current * 100d / total), 0, 100);
    }

    private async Task EnableBeforeUnloadAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("uploadFilesHelper.enableBeforeUnload");
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private async Task DisableBeforeUnloadAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("uploadFilesHelper.disableBeforeUnload");
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Processes upload items in chunks to avoid overwhelming the system.
    /// Audio/cover art pairs are kept together within their chunks.
    /// ChunkSize represents the maximum number of files (not pairs) to process concurrently.
    /// </summary>
    private async Task ProcessUploadsInChunksAsync(IEnumerable<PendingUpload> uploadItemsWithFiles)
    {
        var itemsList = uploadItemsWithFiles.ToList();
        var currentIndex = 0;

        while (currentIndex < itemsList.Count)
        {
            // Build a chunk that respects the max file count
            var chunk = new List<PendingUpload>();
            var currentFileCount = 0;

            while (currentIndex < itemsList.Count && currentFileCount < ChunkSize)
            {
                var item = itemsList[currentIndex];
                // Count files: 2 for pair (audio + cover art), 1 for audio only
                var fileCount = item.CoverArtTempPath != null ? 2 : 1;

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
            foreach (var pending in chunk)
            {
                chunkTasks.Add(pending.CoverArtTempPath != null
                    ? UploadFilePairAsync(pending)
                    : UploadAudioOnlyAsync(pending));
            }

            // Wait for all uploads in this chunk to complete before starting the next chunk
            await Task.WhenAll(chunkTasks);
        }
    }

    /// <summary>
    /// Opens the MP3 the preflight already produced for a non-MP3 source, or null when the source
    /// was already an MP3 and the upload can stream it straight through.
    /// </summary>
    private static FileStream OpenValidatedPlayback(string playbackTempPath, int bufferSize)
        => string.IsNullOrEmpty(playbackTempPath)
            ? null
            : new FileStream(playbackTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);

    private async Task UploadAudioOnlyAsync(PendingUpload pending)
    {
        const int bufferSize = 81920;
        var (uploadItem, audioTempPath, _, _, playbackTempPath, validatedDuration) = pending;

        try
        {
            // Validate CreatorId before uploading - songs without a creator cannot be tracked or paid
            if (_currentCreatorId == null)
            {
                Logger.LogError("UploadFiles: CreatorId is null for user {Email}. Cannot upload {FileName} without a creator association.", _currentUserEmail, uploadItem.AudioFileName);
                await SendCreatorIdFailureEmailAsync(uploadItem.AudioFileName);
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.Progress = 0;
                uploadItem.StatusMessage = "Upload failed";
                uploadItem.ErrorMessage = UploadFailedUserMessage;
                await InvokeAsync(StateHasChanged);
                return;
            }

            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 25;
            await InvokeAsync(StateHasChanged);

            await using var audioFileStream = new FileStream(audioTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            await using var playbackStream = OpenValidatedPlayback(playbackTempPath, bufferSize);
            var uploadResult = await MusicUploadService.UploadMusicWithoutAlbumArtAsync(
                audioFileStream,
                uploadItem.AudioFileName,
                uploadItem.SongTitle,
                null, // No album name
                _currentCreatorId,
                playbackStream,
                validatedDuration,
                _uploadCts.Token);

            // Track the uploaded blob path for cleanup if user leaves
            lock (_blobPathsLock)
            {
                _uploadedBlobPaths.Add(uploadResult.Mp3BlobPath);
                _uploadedBlobPaths.Add(uploadResult.OriginalAudioBlobPath);
            }

            uploadItem.Progress = 100;
            uploadItem.Status = UploadStatus.Completed;
            uploadItem.StatusMessage = $"Uploaded '{uploadResult.SongTitle}'";
            uploadItem.ErrorMessage = null;
        }
        catch (InvalidDataException ex)
        {
            Logger.LogWarning(ex, "Upload validation rejected audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(ex, "Upload validation failed for audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Validation failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Upload failed for audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = FileSizeHelper.FormatFileSizeExceptionMessage(ex.Message);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task UploadFilePairAsync(PendingUpload pending)
    {
        const int bufferSize = 81920;
        var (uploadItem, audioTempPath, coverArtTempPath, _, playbackTempPath, validatedDuration) = pending;

        try
        {
            // Validate CreatorId before uploading - songs without a creator cannot be tracked or paid
            if (_currentCreatorId == null)
            {
                Logger.LogError("UploadFiles: CreatorId is null for user {Email}. Cannot upload audio '{AudioFile}' + cover art '{CoverArtFile}' without a creator association.", _currentUserEmail, uploadItem.AudioFileName, uploadItem.CoverArtFileName);
                await SendCreatorIdFailureEmailAsync($"{uploadItem.AudioFileName}, {uploadItem.CoverArtFileName}");
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.Progress = 0;
                uploadItem.StatusMessage = "Upload failed";
                uploadItem.ErrorMessage = UploadFailedUserMessage;
                await InvokeAsync(StateHasChanged);
                return;
            }

            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 25;
            await InvokeAsync(StateHasChanged);

            await using var audioFileStream = new FileStream(audioTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            await using var coverArtFileStream = new FileStream(coverArtTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            await using var playbackStream = OpenValidatedPlayback(playbackTempPath, bufferSize);
            var uploadResult = await MusicUploadService.UploadMusicWithAlbumArtAsync(
                audioFileStream,
                uploadItem.AudioFileName,
                coverArtFileStream,
                uploadItem.CoverArtFileName,
                uploadItem.SongTitle,
                null, // No album name
                _currentCreatorId,
                playbackStream,
                validatedDuration,
                _uploadCts.Token);

            // Track the uploaded blob paths for cleanup if user leaves
            lock (_blobPathsLock)
            {
                _uploadedBlobPaths.Add(uploadResult.Mp3BlobPath);
                _uploadedBlobPaths.Add(uploadResult.OriginalAudioBlobPath);
                _uploadedBlobPaths.Add(uploadResult.ImageBlobPath);
                _uploadedBlobPaths.Add(uploadResult.OriginalCoverArtBlobPath);
                _uploadedBlobPaths.Add(SongMediaPaths.FacebookImageFor(uploadResult.ImageBlobPath));

                // The pre-resized renditions are generated after the metadata commit, so an
                // abandoned batch would otherwise strand them in storage with no row referencing
                // them. Sweeping the whole ladder is fine here: cleanup deletes best-effort, and a
                // rung the source was too small to fill simply does not exist.
                foreach (var variantPath in ImageVariantPaths.VariantsFor(
                             uploadResult.ImageBlobPath, ImageVariantSizes.CoverArt))
                {
                    _uploadedBlobPaths.Add(variantPath);
                }
            }

            uploadItem.Progress = 100;
            uploadItem.Status = UploadStatus.Completed;
            uploadItem.StatusMessage = $"Uploaded '{uploadResult.SongTitle}'";
            uploadItem.ErrorMessage = null;
        }
        catch (InvalidDataException ex)
        {
            Logger.LogWarning(
                ex,
                "Upload validation rejected audio file {AudioFileName} with cover art {CoverArtFileName}",
                uploadItem.AudioFileName,
                uploadItem.CoverArtFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(
                ex,
                "Upload validation failed for audio file {AudioFileName} with cover art {CoverArtFileName}",
                uploadItem.AudioFileName,
                uploadItem.CoverArtFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Validation failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Upload failed for audio file {AudioFileName} with cover art {CoverArtFileName}",
                uploadItem.AudioFileName,
                uploadItem.CoverArtFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = FileSizeHelper.FormatFileSizeExceptionMessage(ex.Message);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Processes ALL image files in chunks of <see cref="ImageOcrChunkSize"/> (4).
    /// For each chunk: (a) filename-only matching — no bytes read; then (b) for images still
    /// unmatched in that chunk, read their bytes from temp files and attempt OCR matching.
    /// At most <see cref="ImageOcrChunkSize"/> image byte arrays are in memory at any one time.
    /// Bytes for unmatched images in each chunk become GC-eligible immediately after the chunk.
    /// </summary>
    private async Task<FileMatchingResult> PerformChunkedMatchingAsync(
            List<string> audioFileNames,
            List<string> allImageFileNames,
            Dictionary<string, string> coverArtTempPaths)
    {
        var allPairs = new List<FilePair>();
        var unmatchedImages = new List<string>();
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
                var chunkTempPaths = stillUnmatched
                    .Where(coverArtTempPaths.ContainsKey)
                    .ToDictionary(f => f, f => coverArtTempPaths[f]);

                if (chunkTempPaths.Any())
                {
                    var chunkData = await BufferCoverArtChunkAsync(chunkTempPaths);
                    try
                    {
                        var ocrResult = await FileMatchingService.MatchFilesAsync(
                            remainingAudioNames, stillUnmatched, chunkData);

                        foreach (var pair in ocrResult.Pairs.Where(p => p.ImageFileName != null))
                        {
                            allPairs.Add(pair);
                            remainingAudioNames.Remove(pair.AudioFileName);
                        }
                        unmatchedImages.AddRange(ocrResult.UnmatchedImageFiles);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "UploadFiles: OCR matching failed for image chunk {ChunkIndex}; images will remain unmatched.", i / ImageOcrChunkSize);
                        unmatchedImages.AddRange(stillUnmatched);
                    }
                    // Bytes for images in chunkData go out of scope → GC-eligible
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

        return new FileMatchingResult { Pairs = allPairs, UnmatchedImageFiles = unmatchedImages };
    }

    /// <summary>
    /// Reads a subset of cover art temp files into memory for OCR matching.
    /// Called once per OCR chunk — only <see cref="ImageOcrChunkSize"/> images at a time.
    /// </summary>
    private async Task<Dictionary<string, (byte[] Data, string ContentType)>> BufferCoverArtChunkAsync(
        Dictionary<string, string> coverArtTempPaths)
    {
        var result = new Dictionary<string, (byte[] Data, string ContentType)>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in coverArtTempPaths)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(kvp.Value, _uploadCts.Token);
                var extension = Path.GetExtension(kvp.Key).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".png" => "image/png",
                    _ => "image/jpeg"
                };
                result[kvp.Key] = (bytes, contentType);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "UploadFiles: Failed to read temp file for image '{FileName}' for OCR; it will be unmatched.", kvp.Key);
            }
        }

        return result;
    }

    protected void ClearValidationError()
    {
        _validationErrorMessage = string.Empty;
        _unmatchedMp3Files.Clear();
        _unmatchedCoverArtFiles.Clear();
        _duplicateSongFiles.Clear();
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
        /// <summary>
        /// The title to store. Seeded from the filename and editable in the review step before
        /// anything is uploaded.
        /// </summary>
        public string SongTitle { get; set; } = string.Empty;

        /// <summary>
        /// Why this title cannot be uploaded as-is, or null when it is fine. Set only for the
        /// rows the creator has to correct.
        /// </summary>
        public string TitleError { get; set; }

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
        if (!_isUploading && !_isProcessingFiles && !_awaitingTitleConfirmation)
            return;

        var isConfirmed = await JS.InvokeAsync<bool>("confirm",
            _awaitingTitleConfirmation && !_isUploading
                ? "Your files are ready to upload but have not been uploaded yet. If you leave now they will be discarded. Are you sure you want to leave?"
                : "Uploads are in progress. If you leave now, all uploads will be cancelled and any files already uploaded in this session will be removed. Are you sure you want to leave?");

        if (isConfirmed)
        {
            // User chose to leave — cancel pending uploads and clean up
            _uploadCts.Cancel();
            await CleanupUploadedFilesAsync();
            _awaitingTitleConfirmation = false;
            CleanupPendingTempFiles();
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
            pathsToCleanup = _uploadedBlobPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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

        // Backstop for a batch buffered for review that was never uploaded or cancelled,
        // e.g. the circuit dropped while the creator was still editing titles.
        CleanupPendingTempFiles();

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
