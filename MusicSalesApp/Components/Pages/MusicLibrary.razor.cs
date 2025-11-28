using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Services;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public class MusicLibraryModel : BlazorBase
{
    protected bool _loading = true;
    protected string _error;
    protected List<StorageFileInfo> _files = new List<StorageFileInfo>();
    protected string _currentFile;
    protected string _streamUrl; // audio source URL
    protected ElementReference _audio;

    protected override async Task OnInitializedAsync()
    {
        await LoadFiles();
    }

    private async Task LoadFiles()
    {
        _loading = true; _error = null;
        try
        {
            var result = await Http.GetFromJsonAsync<IEnumerable<StorageFileInfo>>("api/music");
            // Filter out image files - only show audio files in the library
            _files = result != null 
                ? new List<StorageFileInfo>(result.Where(f => IsAudioFile(f.Name))) 
                : new List<StorageFileInfo>();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private bool IsAudioFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".mp3" || ext == ".wav" || ext == ".flac" || ext == ".ogg" || ext == ".m4a" || ext == ".aac" || ext == ".wma";
    }

    /// <summary>
    /// Safely encodes a file path for use in URLs, preserving forward slashes but encoding other special characters.
    /// Also validates against path traversal attacks.
    /// </summary>
    private string SafeEncodePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        // Check for path traversal attempts
        if (filePath.Contains("..") || filePath.Contains("~"))
            return string.Empty;

        // Split by forward slash, encode each segment, then rejoin
        var segments = filePath.Split('/');
        var encodedSegments = segments.Select(s => Uri.EscapeDataString(s));
        return string.Join("/", encodedSegments);
    }

    // Ensure audio element reloads when clicking play repeatedly.
    protected async Task PlayAsync(string fileName)
    {
        _currentFile = Path.GetFileName(fileName); // Display just the file name
        // Force re-render by clearing then setting source.
        _streamUrl = null;
        await InvokeAsync(StateHasChanged);
        // Safely encode the path while preserving forward slashes
        _streamUrl = $"api/music/{SafeEncodePath(fileName)}";
        await InvokeAsync(StateHasChanged);
    }

    protected string GetSongPlayerUrl(string fileName)
    {
        // Use just the file name without extension as the song title (not the folder path)
        var songTitle = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
        return $"/song/{Uri.EscapeDataString(songTitle)}";
    }
}
