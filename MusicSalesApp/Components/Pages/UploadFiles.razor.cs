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

    protected string _destinationFolder = string.Empty;
    protected List<UploadItem> _uploadItems = new List<UploadItem>();    

    protected async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        var files = e.GetMultipleFiles(20);

        foreach (var file in files)
        {
            var uploadItem = new UploadItem
            {
                FileName = file.Name,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            _uploadItems.Add(uploadItem);

            // Fire-and-forget each upload
            _ = UploadFileAsync(file, uploadItem);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task UploadFileAsync(IBrowserFile file, UploadItem uploadItem)
    {
        const long maxFileSize = 100 * 1024 * 1024; // 100 MB

        try
        {
            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Validating...";
            uploadItem.Progress = 5;
            await InvokeAsync(StateHasChanged);

            await using var stream = file.OpenReadStream(maxFileSize);

            // Delegate to the service (no HttpClient, no antiforgery)
            var fullPath = await MusicUploadService.UploadAudioAsync(
                stream,
                file.Name,
                _destinationFolder,
                _cancellationToken);

            uploadItem.Progress = 100;
            uploadItem.Status = UploadStatus.Completed;
            uploadItem.StatusMessage = $"Uploaded as {Path.GetFileName(fullPath)}";
            uploadItem.UploadedFileName = fullPath;
            uploadItem.ErrorMessage = null;
        }
        catch (InvalidDataException ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid audio file";
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

    protected class UploadItem
    {
        public string FileName { get; set; } = string.Empty;
        public string UploadedFileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
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
