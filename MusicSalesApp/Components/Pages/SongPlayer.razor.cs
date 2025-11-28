using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages;

public partial class SongPlayerModel : BlazorBase, IAsyncDisposable
{
    [Inject]
    protected IJSRuntime JS { get; set; }

    [Parameter]
    public string SongTitle { get; set; }

    protected bool _loading = true;
    protected string _error;
    protected StorageFileInfo _songInfo;
    protected string _albumArtUrl;
    protected string _streamUrl;
    protected bool _isPlaying;
    protected double _currentTime;
    protected double _duration;
    protected ElementReference _audioElement;
    protected bool _shuffleEnabled;
    private IJSObjectReference _jsModule;
    private DotNetObjectReference<SongPlayerModel> _dotNetRef;
    private bool invokedJs = false;

    protected override async Task OnParametersSetAsync()
    {
        await LoadSongInfo();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!invokedJs && !_loading && _songInfo != null)
        {
            invokedJs = true;
            _dotNetRef = DotNetObjectReference.Create(this);
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/SongPlayer.razor.js");
            await _jsModule.InvokeVoidAsync("initAudioPlayer", _audioElement, _dotNetRef);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_jsModule != null)
        {
            await _jsModule.DisposeAsync();
        }
        _dotNetRef?.Dispose();
    }

    private async Task LoadSongInfo()
    {
        _loading = true;
        _error = null;

        if (string.IsNullOrWhiteSpace(SongTitle))
        {
            _error = "No song title provided.";
            _loading = false;
            return;
        }

        try
        {
            // URL decode the song title
            var decodedTitle = Uri.UnescapeDataString(SongTitle);

            // Get list of files from blob storage and find the matching song (audio files only)
            var files = await Http.GetFromJsonAsync<IEnumerable<StorageFileInfo>>("api/music");
            _songInfo = files?.FirstOrDefault(f =>
                IsAudioFile(f.Name) &&
                (Path.GetFileNameWithoutExtension(f.Name).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals(decodedTitle, StringComparison.OrdinalIgnoreCase)));

            if (_songInfo == null)
            {
                _error = $"Song '{decodedTitle}' not found.";
                _loading = false;
                return;
            }

            // Set the streaming URL for the audio
            _streamUrl = $"api/music/{Uri.EscapeDataString(_songInfo.Name)}";

            // Try to find album art (look for image files with matching name)
            var songBaseName = Path.GetFileNameWithoutExtension(_songInfo.Name);
            var artFile = files?.FirstOrDefault(f =>
                IsImageFile(f.Name) &&
                Path.GetFileNameWithoutExtension(f.Name).Equals(songBaseName, StringComparison.OrdinalIgnoreCase));

            if (artFile != null)
            {
                _albumArtUrl = $"api/music/{Uri.EscapeDataString(artFile.Name)}";
            }
            else
            {
                // Default placeholder if no album art found
                _albumArtUrl = null;
            }
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

    private bool IsImageFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".webp";
    }

    private bool IsAudioFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".mp3" || ext == ".wav" || ext == ".flac" || ext == ".ogg" || ext == ".m4a" || ext == ".aac" || ext == ".wma";
    }

    protected string GetDisplayTitle()
    {
        if (_songInfo == null) return SongTitle ?? "Unknown Song";
        return Path.GetFileNameWithoutExtension(_songInfo.Name);
    }

    protected string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "0:00";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    protected async Task TogglePlay()
    {
        _isPlaying = !_isPlaying;
        if (_jsModule != null)
        {
            if (_isPlaying)
            {
                await _jsModule.InvokeVoidAsync("play", _audioElement);
            }
            else
            {
                await _jsModule.InvokeVoidAsync("pause", _audioElement);
            }
        }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public void UpdateTime(double currentTime)
    {
        _currentTime = currentTime;
        InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public void UpdateDuration(double duration)
    {
        _duration = duration;
        InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public void AudioEnded()
    {
        _isPlaying = false;
        InvokeAsync(StateHasChanged);
    }

    protected async Task SeekTo(double percentage)
    {
        if (_jsModule != null && _duration > 0)
        {
            var newTime = _duration * (percentage / 100);
            await _jsModule.InvokeVoidAsync("seekTo", _audioElement, newTime);
        }
    }

    protected void ToggleShuffle()
    {
        _shuffleEnabled = !_shuffleEnabled;
    }
}
