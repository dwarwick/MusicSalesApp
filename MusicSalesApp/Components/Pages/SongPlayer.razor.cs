using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Services;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages;

public partial class SongPlayerModel : BlazorBase, IAsyncDisposable
{
    private const double PREVIEW_DURATION_SECONDS = 60.0;

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
    protected ElementReference _progressBarContainer;
    protected ElementReference _volumeBarContainer;
    protected bool _shuffleEnabled;
    protected double _volume = 1.0;
    protected double _previousVolume = 1.0;
    protected bool _isMuted;
    protected bool _isAuthenticated;
    protected bool _ownsSong;
    protected bool _inCart;
    protected bool _cartAnimating;
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
            await _jsModule.InvokeVoidAsync("initAudioPlayer", _audioElement, _dotNetRef, !_ownsSong, PREVIEW_DURATION_SECONDS);
            await _jsModule.InvokeVoidAsync("setupProgressBarDrag", _progressBarContainer, _audioElement, _dotNetRef, !_ownsSong, PREVIEW_DURATION_SECONDS);
            await _jsModule.InvokeVoidAsync("setupVolumeBarDrag", _volumeBarContainer, _audioElement, _dotNetRef);
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
            // Check authentication status
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;
            
            // URL decode the song title
            var decodedTitle = Uri.UnescapeDataString(SongTitle);

            // Get list of files from blob storage and find the matching song (audio files only)
            var files = await Http.GetFromJsonAsync<IEnumerable<StorageFileInfo>>("api/music");
            
            // Match by file name (not folder path) - extract just the filename from the full path
            _songInfo = files?.FirstOrDefault(f =>
                IsAudioFile(f.Name) &&
                (Path.GetFileNameWithoutExtension(Path.GetFileName(f.Name)).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f.Name).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase)));

            if (_songInfo == null)
            {
                _error = $"Song '{decodedTitle}' not found.";
                _loading = false;
                return;
            }

            // Get SAS URL for direct streaming from blob storage
            // Non-owners and unauthenticated users get short-lived URLs (preview only)
            // Owners get longer-lived URLs for full access
            await LoadStreamUrl();

            // Try to find album art (look for image files with matching name in the same folder)
            var songBaseName = Path.GetFileNameWithoutExtension(Path.GetFileName(_songInfo.Name));
            var songFolder = Path.GetDirectoryName(_songInfo.Name)?.Replace("\\", "/") ?? "";
            
            var artFile = files?.FirstOrDefault(f =>
                IsImageFile(f.Name) &&
                Path.GetFileNameWithoutExtension(Path.GetFileName(f.Name)).Equals(songBaseName, StringComparison.OrdinalIgnoreCase) &&
                (Path.GetDirectoryName(f.Name)?.Replace("\\", "/") ?? "").Equals(songFolder, StringComparison.OrdinalIgnoreCase));

            if (artFile != null)
            {
                _albumArtUrl = $"api/music/{SafeEncodePath(artFile.Name)}";
            }
            else
            {
                // Default placeholder if no album art found
                _albumArtUrl = null;
            }

            // Check ownership and cart status if authenticated
            if (_isAuthenticated)
            {
                await LoadSongStatus();
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

    private async Task LoadSongStatus()
    {
        if (_songInfo == null) return;
        
        try
        {
            var response = await Http.GetFromJsonAsync<SongStatusResponse>($"api/cart/status/{SafeEncodePath(_songInfo.Name)}");
            if (response != null)
            {
                _ownsSong = response.Owns;
                _inCart = response.InCart;
            }
        }
        catch (HttpRequestException)
        {
            // Not authenticated or error, ignore
        }
    }

    private async Task LoadStreamUrl()
    {
        if (_songInfo == null) return;
        
        try
        {
            // Request SAS URL from the server
            var response = await Http.GetFromJsonAsync<SasUrlResponse>($"api/music/url/{SafeEncodePath(_songInfo.Name)}");
            if (response != null && !string.IsNullOrEmpty(response.Url))
            {
                _streamUrl = response.Url;
            }
            else
            {
                // Fallback to server proxy if SAS URL generation fails
                _streamUrl = $"api/music/{SafeEncodePath(_songInfo.Name)}";
            }
        }
        catch (Exception)
        {
            // Fallback to server proxy on error
            _streamUrl = $"api/music/{SafeEncodePath(_songInfo.Name)}";
        }
    }

    protected async Task ToggleCart()
    {
        if (_songInfo == null) return;

        try
        {
            var response = await Http.PostAsJsonAsync("api/cart/toggle", new { SongFileName = _songInfo.Name, Price = 0.99m });
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CartToggleResponseDto>();
                if (result != null)
                {
                    _inCart = result.InCart;
                    
                    if (_inCart)
                    {
                        // Trigger animation
                        _cartAnimating = true;
                        await InvokeAsync(StateHasChanged);
                        
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(800);
                            _cartAnimating = false;
                            await InvokeAsync(StateHasChanged);
                        });
                    }

                    CartService.NotifyCartUpdated();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error toggling cart: {ex.Message}");
        }
    }

    protected bool IsProgressBarRestricted()
    {
        // Restrict for non-authenticated users OR authenticated users who don't own the song
        return !_isAuthenticated || !_ownsSong;
    }

    protected double GetProgressBarWidth()
    {
        if (_duration <= 0) return 0;
        
        if (IsProgressBarRestricted())
        {
            var maxTime = Math.Min(_duration, PREVIEW_DURATION_SECONDS);
            return (_currentTime / maxTime) * GetPreviewLimitPercentage();
        }
        
        return (_currentTime / _duration) * 100;
    }

    protected double GetPreviewLimitPercentage()
    {
        if (_duration <= 0) return 100;
        return Math.Min(100, (PREVIEW_DURATION_SECONDS / _duration) * 100);
    }

    protected double GetDisplayDuration()
    {
        if (IsProgressBarRestricted())
        {
            return Math.Min(_duration, PREVIEW_DURATION_SECONDS);
        }
        return _duration;
    }

    private class SongStatusResponse
    {
        public bool Owns { get; set; }
        public bool InCart { get; set; }
    }

    private class CartToggleResponseDto
    {
        public bool InCart { get; set; }
        public int Count { get; set; }
    }

    private class SasUrlResponse
    {
        public string Url { get; set; } = string.Empty;
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

    protected string GetDisplayTitle()
    {
        if (_songInfo == null) return SongTitle ?? "Unknown Song";
        // Get just the file name without folder path, then remove extension
        return Path.GetFileNameWithoutExtension(Path.GetFileName(_songInfo.Name));
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

    protected async Task OnProgressBarClick(Microsoft.AspNetCore.Components.Web.MouseEventArgs e)
    {
        if (_jsModule != null && _duration > 0)
        {
            // Get the width of the progress bar container
            var width = await _jsModule.InvokeAsync<double>("getElementWidth", _progressBarContainer);
            if (width > 0)
            {
                await _jsModule.InvokeVoidAsync("seekToPosition", _audioElement, e.OffsetX, width);
            }
        }
    }

    [JSInvokable]
    public void UpdateVolume(double volume, bool isMuted)
    {
        _volume = volume;
        _isMuted = isMuted;
        if (!isMuted && volume > 0)
        {
            _previousVolume = volume;
        }
        InvokeAsync(StateHasChanged);
    }

    protected async Task ToggleMute()
    {
        if (_jsModule != null)
        {
            if (_isMuted)
            {
                // Unmute - restore previous volume
                _isMuted = false;
                _volume = _previousVolume;
                await _jsModule.InvokeVoidAsync("setMuted", _audioElement, false);
                await _jsModule.InvokeVoidAsync("setVolume", _audioElement, _previousVolume);
            }
            else
            {
                // Mute - save current volume
                _previousVolume = _volume > 0 ? _volume : _previousVolume;
                _isMuted = true;
                await _jsModule.InvokeVoidAsync("setMuted", _audioElement, true);
            }
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task OnVolumeBarClick(Microsoft.AspNetCore.Components.Web.MouseEventArgs e)
    {
        if (_jsModule != null)
        {
            var width = await _jsModule.InvokeAsync<double>("getElementWidth", _volumeBarContainer);
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
                await _jsModule.InvokeVoidAsync("setVolume", _audioElement, newVolume);
                await _jsModule.InvokeVoidAsync("setMuted", _audioElement, false);
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected double GetDisplayVolume()
    {
        return _isMuted ? 0 : _volume;
    }
}
