using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MusicSalesApp.Components.Pages;

public class UploadFilesModel : BlazorBase
{
    protected string _destinationFolder = string.Empty;
    protected List<UploadItem> _uploadItems = new List<UploadItem>();

    protected async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        await ProcessFiles(e.GetMultipleFiles(20)); // Limit to 20 files at once
    }

    protected async Task ProcessFiles(IReadOnlyList<IBrowserFile> files)
    {
        foreach (var file in files)
        {
            var uploadItem = new UploadItem
            {
                FileName = file.Name,
                FileSize = file.Size,
                Status = UploadStatus.Pending,
                StatusMessage = "Pending",
                Progress = 0
            };

            _uploadItems.Add(uploadItem);
        }

        await InvokeAsync(StateHasChanged);

        // Process uploads sequentially to avoid overwhelming the server
        foreach (var file in files)
        {
            var uploadItem = _uploadItems.First(x => x.FileName == file.Name && x.Status == UploadStatus.Pending);
            await UploadFile(file, uploadItem);
        }
    }

    protected async Task UploadFile(IBrowserFile file, UploadItem uploadItem)
    {
        try
        {
            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Uploading...";
            uploadItem.Progress = 10;
            await InvokeAsync(StateHasChanged);

            // Create multipart form content
            using var content = new MultipartFormDataContent();
            
            var maxFileSize = 100 * 1024 * 1024; // 100 MB
            var fileStream = file.OpenReadStream(maxFileSize);
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            
            content.Add(streamContent, "file", file.Name);
            content.Add(new StringContent(_destinationFolder ?? string.Empty), "destinationFolder");

            uploadItem.Progress = 30;
            uploadItem.StatusMessage = "Converting...";
            uploadItem.Status = UploadStatus.Converting;
            await InvokeAsync(StateHasChanged);

            var response = await Http.PostAsync("api/music/upload", content);

            uploadItem.Progress = 90;
            await InvokeAsync(StateHasChanged);

            if (response.IsSuccessStatusCode)
            {
                uploadItem.Status = UploadStatus.Completed;
                uploadItem.StatusMessage = "Completed";
                uploadItem.Progress = 100;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.StatusMessage = "Failed";
                uploadItem.ErrorMessage = $"Upload failed: {errorContent}";
                uploadItem.Progress = 0;
            }
        }
        catch (Exception ex)
        {
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.StatusMessage = "Failed";
            uploadItem.ErrorMessage = ex.Message;
            uploadItem.Progress = 0;
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
