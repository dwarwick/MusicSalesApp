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
            _files = result != null ? new List<StorageFileInfo>(result) : new List<StorageFileInfo>();
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

    // Ensure audio element reloads when clicking play repeatedly.
    protected async Task PlayAsync(string fileName)
    {
        _currentFile = fileName;
        // Force re-render by clearing then setting source.
        _streamUrl = null;
        await InvokeAsync(StateHasChanged);
        _streamUrl = $"api/music/{Uri.EscapeDataString(fileName)}";
        await InvokeAsync(StateHasChanged);
    }
}
