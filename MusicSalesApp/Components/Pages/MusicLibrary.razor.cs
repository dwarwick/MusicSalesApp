using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MusicSalesApp.Services;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public class MusicLibraryModel : BlazorBase, IAsyncDisposable
{
    [Inject]
    protected IJSRuntime JS { get; set; }

    protected bool _loading = true;
    protected string _error;
    protected List<StorageFileInfo> _files = new List<StorageFileInfo>();

    // Track which card is currently playing
    private string _playingCardId;
    private bool _isActuallyPlaying;
    
    // Card player state for the currently active card
    private double _currentTime;
    private double _duration;
    private double _volume = 1.0;
    private bool _isMuted;
    private double _previousVolume = 1.0;

    // Single set of element references for the active card
    protected ElementReference _activeAudioElement;
    protected ElementReference _activeProgressBarElement;
    protected ElementReference _activeVolumeBarElement;

    // Map file names to album art URLs
    private Dictionary<string, string> _albumArtUrls = new Dictionary<string, string>();

    private IJSObjectReference _jsModule;
    private DotNetObjectReference<MusicLibraryModel> _dotNetRef;
    private bool _needsJsInit;

    protected override async Task OnInitializedAsync()
    {
        await LoadFiles();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_needsJsInit && !string.IsNullOrEmpty(_playingCardId))
        {
            _needsJsInit = false;
            
            if (_jsModule == null)
            {
                _dotNetRef = DotNetObjectReference.Create(this);
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/MusicLibrary.razor.js");
            }

            await _jsModule.InvokeVoidAsync("initCardAudioPlayer", _activeAudioElement, _playingCardId, _dotNetRef);
            await _jsModule.InvokeVoidAsync("setupCardProgressBarDrag", _activeProgressBarElement, _activeAudioElement, _playingCardId, _dotNetRef);
            await _jsModule.InvokeVoidAsync("setupCardVolumeBarDrag", _activeVolumeBarElement, _activeAudioElement, _playingCardId, _dotNetRef);

            // Auto-play when card is initialized
            await _jsModule.InvokeVoidAsync("playCard", _activeAudioElement);
            _isActuallyPlaying = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_jsModule != null)
            {
                await _jsModule.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit is already disconnected, safe to ignore
        }
        _dotNetRef?.Dispose();
    }

    private async Task LoadFiles()
    {
        _loading = true; _error = null;
        try
        {
            var result = await Http.GetFromJsonAsync<IEnumerable<StorageFileInfo>>("api/music");
            var allFiles = result?.ToList() ?? new List<StorageFileInfo>();
            
            // Filter audio files
            _files = allFiles.Where(f => IsAudioFile(f.Name)).ToList();

            // Build album art URL map
            foreach (var audioFile in _files)
            {
                var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(audioFile.Name));
                var folder = Path.GetDirectoryName(audioFile.Name)?.Replace("\\", "/") ?? "";
                
                var artFile = allFiles.FirstOrDefault(f =>
                    IsImageFile(f.Name) &&
                    Path.GetFileNameWithoutExtension(Path.GetFileName(f.Name)).Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetDirectoryName(f.Name)?.Replace("\\", "/") ?? "").Equals(folder, StringComparison.OrdinalIgnoreCase));

                if (artFile != null)
                {
                    _albumArtUrls[audioFile.Name] = $"api/music/{SafeEncodePath(artFile.Name)}";
                }
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

    private bool IsAudioFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".mp3" || ext == ".wav" || ext == ".flac" || ext == ".ogg" || ext == ".m4a" || ext == ".aac" || ext == ".wma";
    }

    private bool IsImageFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".webp";
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

    protected string GetCardId(string fileName)
    {
        // Create a stable card ID from the filename
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(fileName)).Replace("+", "-").Replace("/", "_");
    }

    protected string GetDisplayTitle(string fileName)
    {
        return Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
    }

    protected string GetStreamUrl(string fileName)
    {
        return $"api/music/{SafeEncodePath(fileName)}";
    }

    protected string GetAlbumArtUrl(string fileName)
    {
        return _albumArtUrls.TryGetValue(fileName, out var url) ? url : null;
    }

    protected string GetSongPlayerUrl(string fileName)
    {
        var songTitle = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
        return $"/song/{Uri.EscapeDataString(songTitle)}";
    }

    protected bool IsCardPlaying(string cardId)
    {
        return _playingCardId == cardId;
    }

    protected bool IsCardCurrentlyPlaying(string cardId)
    {
        return _playingCardId == cardId && _isActuallyPlaying;
    }

    protected async Task PlayCard(string fileName)
    {
        var cardId = GetCardId(fileName);
        _playingCardId = cardId;
        _isActuallyPlaying = false;
        _needsJsInit = true;

        // Reset state for new card
        _volume = 1.0;
        _previousVolume = 1.0;
        _isMuted = false;
        _currentTime = 0;
        _duration = 0;

        await InvokeAsync(StateHasChanged);
    }

    protected async Task ToggleCardPlay(string cardId)
    {
        if (_jsModule != null && _playingCardId == cardId)
        {
            if (_isActuallyPlaying)
            {
                await _jsModule.InvokeVoidAsync("pauseCard", _activeAudioElement);
                _isActuallyPlaying = false;
            }
            else
            {
                await _jsModule.InvokeVoidAsync("playCard", _activeAudioElement);
                _isActuallyPlaying = true;
            }
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task StopCard(string cardId)
    {
        if (_jsModule != null && _playingCardId == cardId)
        {
            await _jsModule.InvokeVoidAsync("stopCard", _activeAudioElement);
        }

        _playingCardId = null;
        _isActuallyPlaying = false;
        _currentTime = 0;
        _duration = 0;

        await InvokeAsync(StateHasChanged);
    }

    protected double GetCardCurrentTime(string cardId)
    {
        return _playingCardId == cardId ? _currentTime : 0;
    }

    protected double GetCardDuration(string cardId)
    {
        return _playingCardId == cardId ? _duration : 0;
    }

    protected double GetCardProgressPercentage(string cardId)
    {
        if (_playingCardId != cardId) return 0;
        return _duration > 0 ? (_currentTime / _duration * 100) : 0;
    }

    protected double GetCardVolume(string cardId)
    {
        if (_playingCardId != cardId) return 1.0;
        return _isMuted ? 0 : _volume;
    }

    protected bool IsCardMuted(string cardId)
    {
        return _playingCardId == cardId && _isMuted;
    }

    protected async Task ToggleCardMute(string cardId)
    {
        if (_jsModule != null && _playingCardId == cardId)
        {
            if (_isMuted)
            {
                // Unmute
                _isMuted = false;
                _volume = _previousVolume;
                await _jsModule.InvokeVoidAsync("setCardMuted", _activeAudioElement, false);
                await _jsModule.InvokeVoidAsync("setCardVolume", _activeAudioElement, _previousVolume);
            }
            else
            {
                // Mute
                if (_volume > 0)
                {
                    _previousVolume = _volume;
                }
                _isMuted = true;
                await _jsModule.InvokeVoidAsync("setCardMuted", _activeAudioElement, true);
            }
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task OnCardVolumeBarClick(MouseEventArgs e, string cardId)
    {
        if (_jsModule != null && _playingCardId == cardId)
        {
            var width = await _jsModule.InvokeAsync<double>("getElementWidth", _activeVolumeBarElement);
            if (width > 0)
            {
                var newVolume = e.OffsetX / width;
                newVolume = Math.Max(0, Math.Min(1, newVolume));
                _volume = newVolume;
                _isMuted = false;
                if (newVolume > 0)
                {
                    _previousVolume = newVolume;
                }
                await _jsModule.InvokeVoidAsync("setCardVolume", _activeAudioElement, newVolume);
                await _jsModule.InvokeVoidAsync("setCardMuted", _activeAudioElement, false);
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task OnCardProgressBarClick(MouseEventArgs e, string cardId)
    {
        if (_jsModule != null && _playingCardId == cardId)
        {
            var width = await _jsModule.InvokeAsync<double>("getElementWidth", _activeProgressBarElement);
            if (width > 0)
            {
                await _jsModule.InvokeVoidAsync("seekCardToPosition", _activeAudioElement, e.OffsetX, width);
            }
        }
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

    // JSInvokable methods for JavaScript callbacks
    [JSInvokable]
    public void UpdateCardTime(string cardId, double currentTime)
    {
        if (_playingCardId == cardId)
        {
            _currentTime = currentTime;
            InvokeAsync(StateHasChanged);
        }
    }

    [JSInvokable]
    public void UpdateCardDuration(string cardId, double duration)
    {
        if (_playingCardId == cardId)
        {
            _duration = duration;
            InvokeAsync(StateHasChanged);
        }
    }

    [JSInvokable]
    public void UpdateCardVolume(string cardId, double volume, bool isMuted)
    {
        if (_playingCardId == cardId)
        {
            _volume = volume;
            _isMuted = isMuted;
            if (!isMuted && volume > 0)
            {
                _previousVolume = volume;
            }
            InvokeAsync(StateHasChanged);
        }
    }

    [JSInvokable]
    public void CardAudioEnded(string cardId)
    {
        if (_playingCardId == cardId)
        {
            _isActuallyPlaying = false;
            InvokeAsync(StateHasChanged);
        }
    }
}
