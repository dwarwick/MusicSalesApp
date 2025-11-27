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

    private static readonly string[] ValidAudioExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
    private static readonly string[] ValidAlbumArtExtensions = { ".jpeg", ".jpg" };

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
            _validationErrorMessage = "Some files do not have matching pairs. Each MP3 file must have a corresponding album art file with the same base name.";
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
                    Mp3FileName = audioFile.Name,
                    Mp3FileSize = audioFile.Size,
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

        try
        {
            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Validating...";
            uploadItem.Progress = 5;
            await InvokeAsync(StateHasChanged);

            await using var audioStream = audioFile.OpenReadStream(maxFileSize);
            await using var albumArtStream = albumArtFile.OpenReadStream(maxFileSize);

            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 20;
            await InvokeAsync(StateHasChanged);

            // Delegate to the service
            var folderPath = await MusicUploadService.UploadMusicWithAlbumArtAsync(
                audioStream,
                audioFile.Name,
                albumArtStream,
                albumArtFile.Name,
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
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void ClearValidationError()
    {
        _validationErrorMessage = string.Empty;
        _unmatchedMp3Files.Clear();
        _unmatchedAlbumArtFiles.Clear();
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
        public string Mp3FileName { get; set; } = string.Empty;
        public long Mp3FileSize { get; set; }
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
