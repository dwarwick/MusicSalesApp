using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
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
    protected bool _repeatEnabled;
    protected double _volume = 1.0;
    protected double _previousVolume = 1.0;
    protected bool _isMuted;
    protected bool _isAuthenticated;
    protected int _streamCount;
    private Models.SongMetadata _songMetadata;
    private int _defaultStreamQualifyingSeconds = 30;
    private IJSObjectReference _jsModule;
    private DotNetObjectReference<SongPlayerModel> _dotNetRef;
    private bool invokedJs = false;
    protected bool _hasActiveSubscription;
    protected bool _isAdmin;
    protected bool _isCreatorOfSong;
    private int? _currentUserId;
    private Action<int, int> _streamCountUpdatedHandler;
    private Action<int, int> _hubStreamCountHandler;

    protected override async Task OnInitializedAsync()
    {
        // Subscribe to stream count updates (local in-process events)
        _streamCountUpdatedHandler = OnStreamCountUpdated;
        StreamCountService.OnStreamCountUpdated += _streamCountUpdatedHandler;

        // Subscribe to SignalR hub for cross-tab updates
        _hubStreamCountHandler = OnStreamCountUpdated;
        StreamCountHubClient.OnStreamCountReceived += _hubStreamCountHandler;
        await StreamCountHubClient.StartAsync();
    }

    private void OnStreamCountUpdated(int songMetadataId, int newCount)
    {
        if (_songMetadata != null && _songMetadata.Id == songMetadataId)
        {
            _streamCount = newCount;
            InvokeAsync(StateHasChanged);
        }
    }

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
            await _jsModule.InvokeVoidAsync("initAudioPlayer", _audioElement, _dotNetRef, IsProgressBarRestricted(), PREVIEW_DURATION_SECONDS, GetSongMetadataId(), GetStreamQualifyingSeconds());
            await _jsModule.InvokeVoidAsync("setupProgressBarDrag", _progressBarContainer, _audioElement, _dotNetRef, IsProgressBarRestricted(), PREVIEW_DURATION_SECONDS);
            await _jsModule.InvokeVoidAsync("setupVolumeBarDrag", _volumeBarContainer, _audioElement, _dotNetRef);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Unsubscribe from stream count updates (local)
        if (_streamCountUpdatedHandler != null)
        {
            StreamCountService.OnStreamCountUpdated -= _streamCountUpdatedHandler;
        }

        // Unsubscribe from SignalR hub updates
        if (_hubStreamCountHandler != null)
        {
            StreamCountHubClient.OnStreamCountReceived -= _hubStreamCountHandler;
        }

        try
        {
            if (_jsModule != null)
            {
                await _jsModule.DisposeAsync();
            }
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Song player JS runtime disconnected during disposal");
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
            // Load default stream qualifying seconds for songs without a creator
            _defaultStreamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();

            // Check authentication status
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;
            
            // URL decode the song title
            var decodedTitle = Uri.UnescapeDataString(SongTitle);

            // Get all metadata from database - SQL is the source of truth
            var allMetadata = await SongMetadataService.GetAllAsync();
            
            // Find matching song metadata by filename or stored title
            _songMetadata = allMetadata.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.Mp3BlobPath) &&
                MusicFileExtensions.IsAudioFile(m.Mp3BlobPath) &&
                (Path.GetFileNameWithoutExtension(Path.GetFileName(m.Mp3BlobPath)).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(m.Mp3BlobPath).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(m.SongTitle) && m.SongTitle.Equals(decodedTitle, StringComparison.OrdinalIgnoreCase))));

            if (_songMetadata == null)
            {
                _error = $"Song '{decodedTitle}' not found.";
                _loading = false;
                return;
            }

            // Initialize stream count from metadata
            _streamCount = _songMetadata.NumberOfStreams;

            // Build StorageFileInfo from metadata (no longer calling api/music List endpoint)
            _songInfo = new StorageFileInfo
            {
                Name = _songMetadata.Mp3BlobPath ?? _songMetadata.BlobPath,
                Length = 0, // Not needed for playback
                ContentType = "audio/mpeg",
                LastModified = _songMetadata.UpdatedAt,
                Tags = new Dictionary<string, string>() // No longer using tags
            };

            // Get SAS URL for direct streaming from blob storage
            await LoadStreamUrl();

            // Try to find album art from metadata (look for image with matching name in the same folder)
            var songBaseName = Path.GetFileNameWithoutExtension(Path.GetFileName(_songInfo.Name));
            var songFolder = Path.GetDirectoryName(_songInfo.Name)?.Replace("\\", "/") ?? "";
            
            var artMetadata = allMetadata.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.ImageBlobPath) &&
                IsImageFile(m.ImageBlobPath) &&
                Path.GetFileNameWithoutExtension(Path.GetFileName(m.ImageBlobPath)).Equals(songBaseName, StringComparison.OrdinalIgnoreCase) &&
                (Path.GetDirectoryName(m.ImageBlobPath)?.Replace("\\", "/") ?? "").Equals(songFolder, StringComparison.OrdinalIgnoreCase));

            if (artMetadata != null)
            {
                // Only show image if it's square (or dimensions are unknown)
                var isSquare = artMetadata.IsImageSquare ?? true;
                _albumArtUrl = isSquare ? $"api/music/{SafeEncodePath(artMetadata.ImageBlobPath)}" : null;
            }
            else
            {
                // Default placeholder if no album art found
                _albumArtUrl = null;
            }

            // Check subscription status if authenticated
            if (_isAuthenticated)
            {
                await LoadSubscriptionStatus();

                // Check if user is admin
                _isAdmin = authState.User.IsInRole(Common.Helpers.Roles.Admin);

                // Load current user ID for stream recording
                var user = await UserManager.GetUserAsync(authState.User);
                _currentUserId = user?.Id;

                // Check if user is the creator of this song
                if (_songMetadata.Creator != null && user != null)
                {
                    _isCreatorOfSong = _songMetadata.Creator.UserId == user.Id;
                }

                Logger.LogInformation("SongPlayer: Auth context loaded - _isAuthenticated={IsAuthenticated}, _isAdmin={IsAdmin}, _currentUserId={CurrentUserId}, _isCreatorOfSong={IsCreatorOfSong}", 
                    _isAuthenticated, _isAdmin, _currentUserId, _isCreatorOfSong);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Logger.LogError(ex, "Error loading song info for {SongTitle}", SongTitle);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadSubscriptionStatus()
    {
        try
        {
            // Check subscription status
            var subscriptionResponse = await Http.GetFromJsonAsync<SubscriptionStatusDto>("api/subscription/status");
            _hasActiveSubscription = subscriptionResponse?.HasSubscription ?? false;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogDebug(ex, "Unable to load subscription status; user may be unauthenticated");
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
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to retrieve SAS URL for {SongFile}", _songInfo?.Name);
            _streamUrl = $"api/music/{SafeEncodePath(_songInfo.Name)}";
        }
    }

    protected bool IsProgressBarRestricted()
    {
        // If user has an active subscription, they can listen to everything
        if (_hasActiveSubscription)
            return false;

        // Admins can fully stream all songs without a subscription
        if (_isAdmin)
            return false;

        // Creators can fully stream their own songs
        if (_isCreatorOfSong)
            return false;

        // Non-authenticated users or users without subscription are restricted to preview
        return true;
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
        return _duration;
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
        // Prefer stored SongTitle from metadata if available
        if (_songMetadata != null && !string.IsNullOrEmpty(_songMetadata.SongTitle))
        {
            return _songMetadata.SongTitle;
        }
        
        if (_songInfo == null) return SongTitle ?? "Unknown Song";
        // Fall back to extracting from file name
        return Path.GetFileNameWithoutExtension(Path.GetFileName(_songInfo.Name));
    }

    /// <summary>
    /// Gets the artist display name using the priority:
    /// 1. SongMetadata.ArtistName
    /// 2. Creator.DisplayName
    /// 3. Creator.User.Email (part before @)
    /// </summary>
    protected string GetArtistDisplayName()
    {
        if (_songMetadata == null)
            return null;

        // Priority 1: ArtistName from SongMetadata
        if (!string.IsNullOrWhiteSpace(_songMetadata.ArtistName))
        {
            // Strip email domain if it contains @ to avoid exposing email addresses
            return _songMetadata.ArtistName.Contains('@') ? _songMetadata.ArtistName.Split('@')[0] : _songMetadata.ArtistName;
        }

        // Priority 2: DisplayName from Creator
        if (_songMetadata.Creator != null && !string.IsNullOrWhiteSpace(_songMetadata.Creator.DisplayName))
        {
            return _songMetadata.Creator.DisplayName;
        }

        // Priority 3: Email from Creator's User - use part before @ symbol
        if (_songMetadata.Creator?.User?.Email != null)
        {
            return _songMetadata.Creator.User.Email.Split('@')[0];
        }

        return null;
    }

    protected double? GetTrackLengthSeconds()
    {
        if (_songMetadata != null && _songMetadata.TrackLength.HasValue)
        {
            return _songMetadata.TrackLength.Value;
        }
        return null;
    }

    protected int GetSongMetadataId()
    {
        return _songMetadata?.Id ?? 0;
    }

    protected int GetStreamQualifyingSeconds()
    {
        return _songMetadata?.Creator?.StreamQualifyingSeconds ?? _defaultStreamQualifyingSeconds;
    }

    /// <summary>
    /// Gets the genre for the current song. Returns "Unknown Genre" if genre is null or whitespace.
    /// </summary>
    protected string GetGenre()
    {
        if (_songMetadata != null && !string.IsNullOrWhiteSpace(_songMetadata.Genre))
        {
            return _songMetadata.Genre;
        }
        return "Unknown Genre";
    }

    /// <summary>
    /// Gets the URL for the genre playlist page.
    /// </summary>
    protected string GetArtistUrl()
    {
        return $"/artist/{Uri.EscapeDataString(GetArtistDisplayName())}";
    }

    protected string GetGenreUrl()
    {
        return $"/genre/{Uri.EscapeDataString(GetGenre())}";
    }

    protected string GetShareUrl()
    {
        var baseUrl = NavigationManager.BaseUri.TrimEnd('/');
        return $"{baseUrl}/song/{Uri.EscapeDataString(SongTitle)}";
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
    public async Task AudioEnded()
    {
        if (_repeatEnabled && _jsModule != null)
        {
            // Restart the song from the beginning
            await _jsModule.InvokeVoidAsync("seekTo", _audioElement, 0);
            await _jsModule.InvokeVoidAsync("play", _audioElement);
            _isPlaying = true;
        }
        else
        {
            _isPlaying = false;
        }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task RecordStream(int songMetadataId)
    {
        Logger.LogInformation("SongPlayer.RecordStream called: songMetadataId={SongMetadataId}, _currentUserId={CurrentUserId}, _isAdmin={IsAdmin}, _isCreatorOfSong={IsCreatorOfSong}", 
            songMetadataId, _currentUserId, _isAdmin, _isCreatorOfSong);

        try
        {
            // Call StreamCountService directly (bypasses HTTP which loses auth context in Blazor Server)
            var newCount = await StreamCountService.IncrementStreamCountAsync(songMetadataId, _currentUserId, _isAdmin);
            _streamCount = newCount;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SongPlayer: Failed to record stream for song {SongMetadataId}", songMetadataId);
        }
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

    protected void ToggleRepeat()
    {
        _repeatEnabled = !_repeatEnabled;
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
