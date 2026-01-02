using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Components.Pages;



public class UploadFilesModel : BlazorBase
{
    private sealed class AntiforgeryTokenResponse
    {
        public string Token { get; set; }
        public string FieldName { get; set; }
    }

    private readonly CancellationToken _cancellationToken = CancellationToken.None;

    protected List<UploadPairItem> _uploadItems = new List<UploadPairItem>();
    protected string _validationErrorMessage = string.Empty;
    protected List<string> _unmatchedMp3Files = new List<string>();
    protected List<string> _unmatchedAlbumArtFiles = new List<string>();
    protected string _albumName = string.Empty;
    protected bool _isAlbumCoverUploadMode = false;
    protected bool _showAlbumCoverPrompt = false;
    protected string _pendingAlbumName = string.Empty;
    
    // Seller ID - will be populated if the current user is a seller
    private int? _currentSellerId = null;
    private bool _hasLoadedSellerId = false;

    private static readonly string[] ValidAudioExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
    private static readonly string[] ValidAlbumArtExtensions = { ".jpeg", ".jpg", ".png" };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedSellerId)
        {
            _hasLoadedSellerId = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    var appUser = await UserManager.GetUserAsync(user);
                    if (appUser != null)
                    {
                        // Check if the user is a seller and get their seller ID
                        _currentSellerId = await SellerService.GetSellerIdForUserAsync(appUser.Id);
                    }
                }
            }
            catch (Exception)
            {
                // If we can't determine seller status, uploads will be assigned to admin
                _currentSellerId = null;
            }
        }
    }

    protected async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        // Clear previous validation errors
        ClearValidationError();
        _uploadItems.Clear();

        var files = e.GetMultipleFiles(40); // Allow up to 40 files (20 pairs)

        // Separate files into audio and album art
        var audioFiles = new Dictionary<string, IBrowserFile>();
        var albumArtFiles = new Dictionary<string, IBrowserFile>();

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            var baseName = MusicUploadService.GetNormalizedBaseName(file.Name).ToLowerInvariant();

            if (ValidAudioExtensions.Contains(extension))
            {
                audioFiles[baseName] = file;
            }
            else if (ValidAlbumArtExtensions.Contains(extension))
            {
                albumArtFiles[baseName] = file;
            }
        }

        // Validate all file pairings
        var fileNames = files.Select(f => f.Name).ToList();
        var validationResult = MusicUploadService.ValidateAllFilePairings(fileNames);

        if (!validationResult.IsValid)
        {
            _validationErrorMessage = "Some files do not have matching pairs. Each audio file must have a corresponding album art file with the same base name.";
            _unmatchedMp3Files = validationResult.UnmatchedMp3Files;
            _unmatchedAlbumArtFiles = validationResult.UnmatchedAlbumArtFiles;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Create upload pairs for matched files
        foreach (var audioEntry in audioFiles)
        {
            if (albumArtFiles.TryGetValue(audioEntry.Key, out var albumArtFile))
            {
                var audioFile = audioEntry.Value;
                var baseName = MusicUploadService.GetNormalizedBaseName(audioFile.Name);

                var uploadItem = new UploadPairItem
                {
                    BaseName = baseName,
                    AudioFileName = audioFile.Name,
                    AudioFileSize = audioFile.Size,
                    AlbumArtFileName = albumArtFile.Name,
                    AlbumArtFileSize = albumArtFile.Size,
                    Status = UploadStatus.Pending,
                    Progress = 0,
                    StatusMessage = "Pending"
                };

                _uploadItems.Add(uploadItem);

                // Fire-and-forget each upload pair
                _ = UploadFilePairAsync(audioFile, albumArtFile, uploadItem);
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task UploadFilePairAsync(IBrowserFile audioFile, IBrowserFile albumArtFile, UploadPairItem uploadItem)
    {
        const long maxFileSize = 100 * 1024 * 1024; // 100 MB
        const int bufferSize = 81920; // 80 KB buffer for better performance with large files

        MemoryStream audioMemoryStream = null;
        MemoryStream albumArtMemoryStream = null;

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
                await audioStream.CopyToAsync(audioMemoryStream, bufferSize, _cancellationToken);
            }
            audioMemoryStream.Position = 0;

            uploadItem.StatusMessage = "Reading album art...";
            uploadItem.Progress = 15;
            await InvokeAsync(StateHasChanged);

            // Now buffer the album art file
            albumArtMemoryStream = new MemoryStream();
            await using (var albumArtStream = albumArtFile.OpenReadStream(maxFileSize))
            {
                await albumArtStream.CopyToAsync(albumArtMemoryStream, bufferSize, _cancellationToken);
            }
            albumArtMemoryStream.Position = 0;

            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 25;
            await InvokeAsync(StateHasChanged);

            // Delegate to the service with buffered streams
            var folderPath = await MusicUploadService.UploadMusicWithAlbumArtAsync(
                audioMemoryStream,
                audioFile.Name,
                albumArtMemoryStream,
                albumArtFile.Name,
                _albumName,
                _currentSellerId,
                _cancellationToken);

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
            albumArtMemoryStream?.Dispose();
            await InvokeAsync(StateHasChanged);

            // Check if all uploads are complete and show album cover prompt if album name was provided
            await CheckAndShowAlbumCoverPromptAsync();
        }
    }

    private async Task CheckAndShowAlbumCoverPromptAsync()
    {
        // Only show prompt if an album name was provided and all uploads are done
        if (string.IsNullOrWhiteSpace(_albumName))
            return;

        // Check if all uploads are complete (either succeeded or failed)
        var allComplete = _uploadItems.All(item => 
            item.Status == UploadStatus.Completed || item.Status == UploadStatus.Failed);

        // Check if at least one upload succeeded
        var anySucceeded = _uploadItems.Any(item => item.Status == UploadStatus.Completed);

        if (allComplete && anySucceeded && !_showAlbumCoverPrompt)
        {
            _showAlbumCoverPrompt = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void ClearValidationError()
    {
        _validationErrorMessage = string.Empty;
        _unmatchedMp3Files.Clear();
        _unmatchedAlbumArtFiles.Clear();
    }

    protected void StartAlbumCoverUpload()
    {
        _isAlbumCoverUploadMode = true;
        _showAlbumCoverPrompt = false;
        _pendingAlbumName = _albumName;
        _uploadItems.Clear();
        StateHasChanged();
    }

    protected void SkipAlbumCoverUpload()
    {
        _showAlbumCoverPrompt = false;
        _albumName = string.Empty;
        StateHasChanged();
    }

    protected async Task HandleAlbumCoverFileSelected(InputFileChangeEventArgs e)
    {
        ClearValidationError();
        _uploadItems.Clear();

        var files = e.GetMultipleFiles(10); // Allow up to 10 files for album cover

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            if (!ValidAlbumArtExtensions.Contains(extension))
            {
                continue; // Skip non-album art files
            }

            var baseName = MusicUploadService.GetNormalizedBaseName(file.Name);

            var uploadItem = new UploadPairItem
            {
                BaseName = baseName,
                AudioFileName = string.Empty,
                AudioFileSize = 0,
                AlbumArtFileName = file.Name,
                AlbumArtFileSize = file.Size,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            _uploadItems.Add(uploadItem);

            // Fire-and-forget the album cover upload
            _ = UploadAlbumCoverAsync(file, uploadItem);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task UploadAlbumCoverAsync(IBrowserFile albumArtFile, UploadPairItem uploadItem)
    {
        const long maxFileSize = 100 * 1024 * 1024; // 100 MB
        const int bufferSize = 81920; // 80 KB buffer

        MemoryStream albumArtMemoryStream = null;

        try
        {
            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Reading album cover...";
            uploadItem.Progress = 20;
            await InvokeAsync(StateHasChanged);

            // Buffer the album art file
            albumArtMemoryStream = new MemoryStream();
            await using (var albumArtStream = albumArtFile.OpenReadStream(maxFileSize))
            {
                await albumArtStream.CopyToAsync(albumArtMemoryStream, bufferSize, _cancellationToken);
            }
            albumArtMemoryStream.Position = 0;

            uploadItem.StatusMessage = "Uploading album cover...";
            uploadItem.Progress = 50;
            await InvokeAsync(StateHasChanged);

            // Upload using the album cover service
            var coverPath = await MusicUploadService.UploadAlbumCoverAsync(
                albumArtMemoryStream,
                albumArtFile.Name,
                _pendingAlbumName,
                _currentSellerId,
                _cancellationToken);

            uploadItem.Progress = 100;
            uploadItem.Status = UploadStatus.Completed;
            uploadItem.StatusMessage = $"Uploaded to {coverPath}";
            uploadItem.ErrorMessage = null;
        }
        catch (InvalidDataException ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
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
            albumArtMemoryStream?.Dispose();
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void FinishAlbumCoverUpload()
    {
        _isAlbumCoverUploadMode = false;
        _pendingAlbumName = string.Empty;
        _albumName = string.Empty;
        _uploadItems.Clear();
        StateHasChanged();
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
        public string AlbumArtFileName { get; set; } = string.Empty;
        public long AlbumArtFileSize { get; set; }
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
}
