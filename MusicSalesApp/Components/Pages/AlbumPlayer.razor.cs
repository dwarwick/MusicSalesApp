using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages
{
    public partial class AlbumPlayerModel : BlazorBase, IAsyncDisposable
    {
        private const double PREVIEW_DURATION_SECONDS = 60.0;
        private const string UNKNOWN_DURATION_PLACEHOLDER = "--:--";

        [Parameter]
        public string AlbumName { get; set; }

        protected bool _loading = true;
        protected string _error;
        protected AlbumInfo _albumInfo;
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
        protected bool _ownsAlbum;
        protected HashSet<string> _ownedSongs = new HashSet<string>();
        protected bool _inCart;
        protected bool _cartAnimating;
        protected int _currentTrackIndex;
        protected Dictionary<int, double> _trackDurations = new Dictionary<int, double>();
        private List<string> _trackStreamUrls = new List<string>();
        private IJSObjectReference _jsModule;
        private DotNetObjectReference<AlbumPlayerModel> _dotNetRef;
        private bool invokedJs = false;

        protected override async Task OnParametersSetAsync()
        {
            await LoadAlbumInfo();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!invokedJs && !_loading && _albumInfo != null && _albumInfo.Tracks.Any())
            {
                invokedJs = true;
                _dotNetRef = DotNetObjectReference.Create(this);
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/AlbumPlayer.razor.js");

                await _jsModule.InvokeVoidAsync("initAudioPlayer", _audioElement, _dotNetRef, IsCurrentTrackRestricted(), PREVIEW_DURATION_SECONDS);
                await _jsModule.InvokeVoidAsync("setupProgressBarDrag", _progressBarContainer, _audioElement, _dotNetRef, IsCurrentTrackRestricted(), PREVIEW_DURATION_SECONDS);
                await _jsModule.InvokeVoidAsync("setupVolumeBarDrag", _volumeBarContainer, _audioElement, _dotNetRef);

                // Ensure an initial track source is set for the audio element
                if (!string.IsNullOrWhiteSpace(_streamUrl))
                {
                    await _jsModule.InvokeVoidAsync("setTrackSource", _audioElement, _streamUrl);
                }
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

        private async Task LoadAlbumInfo()
        {
            _loading = true;
            _error = null;

            if (string.IsNullOrWhiteSpace(AlbumName))
            {
                _error = "No album name provided.";
                _loading = false;
                return;
            }

            try
            {
                // Check authentication status
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

                // URL decode the album name
                var decodedAlbumName = Uri.UnescapeDataString(AlbumName);

                // Get list of files for this specific album from blob storage
                var files = await Http.GetFromJsonAsync<IEnumerable<StorageFileInfo>>(
                    $"api/music?albumName={Uri.EscapeDataString(decodedAlbumName)}");

                var allFiles = files?.ToList() ?? new List<StorageFileInfo>();

                // Find album cover (image with IsAlbumCover=true and matching AlbumName)
                var albumCover = allFiles.FirstOrDefault(f =>
                    IsImageFile(f.Name) &&
                    f.Tags != null &&
                    f.Tags.TryGetValue(IndexTagNames.IsAlbumCover, out var isAlbumCover) &&
                    string.Equals(isAlbumCover, "true", StringComparison.OrdinalIgnoreCase) &&
                    f.Tags.TryGetValue(IndexTagNames.AlbumName, out var albumName) &&
                    string.Equals(albumName, decodedAlbumName, StringComparison.OrdinalIgnoreCase));

                if (albumCover == null)
                {
                    _error = $"Album '{decodedAlbumName}' not found.";
                    _loading = false;
                    return;
                }

                // Find all tracks with the same album name
                var tracks = allFiles
                    .Where(f => IsAudioFile(f.Name) &&
                                f.Tags != null &&
                                f.Tags.TryGetValue(IndexTagNames.AlbumName, out var trackAlbum) &&
                                string.Equals(trackAlbum, decodedAlbumName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f.Name))
                    .ToList();

                if (!tracks.Any())
                {
                    _error = $"No tracks found for album '{decodedAlbumName}'.";
                    _loading = false;
                    return;
                }

                _albumInfo = new AlbumInfo
                {
                    AlbumName = decodedAlbumName,
                    CoverArtUrl = $"api/music/{SafeEncodePath(albumCover.Name)}", // Cover art can still go through the controller
                    CoverArtFileName = albumCover.Name,
                    Tracks = tracks,
                    Price = AlbumInfo.DEFAULT_ALBUM_PRICE
                };

                // Pre-fetch all track SAS URLs in parallel for better performance
                var trackUrlTasks = tracks.Select(t => GetTrackStreamUrlAsync(t.Name));
                _trackStreamUrls = (await Task.WhenAll(trackUrlTasks)).ToList();

                // Set up the first track
                _currentTrackIndex = 0;
                _streamUrl = _trackStreamUrls.Count > 0 ? _trackStreamUrls[0] : string.Empty;

                // Check ownership and cart status if authenticated
                if (_isAuthenticated)
                {
                    await LoadAlbumStatus();
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

        private async Task LoadAlbumStatus()
        {
            if (_albumInfo == null) return;

            try
            {
                // Check if user owns tracks in the album
                var ownedResponse = await Http.GetFromJsonAsync<IEnumerable<string>>("api/cart/owned");
                _ownedSongs = new HashSet<string>(ownedResponse ?? Enumerable.Empty<string>());
                _ownsAlbum = _albumInfo.Tracks.All(t => _ownedSongs.Contains(t.Name));

                // Check if album is in cart
                var cartResponse = await Http.GetFromJsonAsync<CartResponseDto>("api/cart");
                if (cartResponse?.Albums != null)
                {
                    _inCart = cartResponse.Albums.Any(a =>
                        string.Equals(a.AlbumName, _albumInfo.AlbumName, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (HttpRequestException)
            {
                // Not authenticated or error, ignore
            }
        }

        protected async Task ToggleCart()
        {
            if (_albumInfo == null) return;

            try
            {
                var trackFileNames = _albumInfo.Tracks.Select(t => t.Name).ToList();

                var response = await Http.PostAsJsonAsync("api/cart/toggle-album", new
                {
                    AlbumName = _albumInfo.AlbumName,
                    TrackFileNames = trackFileNames,
                    Price = _albumInfo.Price
                });

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

                        NavMenuModel.NotifyCartUpdated();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error toggling cart: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the user owns the specified track.
        /// </summary>
        protected bool OwnsTrack(int trackIndex)
        {
            if (!_isAuthenticated || _albumInfo == null || trackIndex < 0 || trackIndex >= _albumInfo.Tracks.Count)
                return false;
            return _ownedSongs.Contains(_albumInfo.Tracks[trackIndex].Name);
        }

        /// <summary>
        /// Checks if the current track should be restricted (60 second preview).
        /// Restricted for non-authenticated users OR authenticated users who don't own the current track.
        /// </summary>
        protected bool IsCurrentTrackRestricted()
        {
            return !_isAuthenticated || !OwnsTrack(_currentTrackIndex);
        }

        protected bool IsProgressBarRestricted()
        {
            // Restrict preview for non-authenticated users OR authenticated users who don't own the current track
            return IsCurrentTrackRestricted();
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

        private class CartResponseDto
        {
            public IEnumerable<CartItemDto> Items { get; set; }
            public IEnumerable<CartAlbumDto> Albums { get; set; }
            public decimal Total { get; set; }
        }

        private class CartItemDto
        {
            public string SongFileName { get; set; }
            public string SongTitle { get; set; }
            public decimal Price { get; set; }
        }

        private class CartAlbumDto
        {
            public string AlbumName { get; set; }
            public decimal Price { get; set; }
            public IEnumerable<string> TrackFileNames { get; set; }
        }

        private class CartToggleResponseDto
        {
            public bool InCart { get; set; }
            public int Count { get; set; }
        }

        private class StreamUrlResponseDto
        {
            public string Url { get; set; }
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

        private string SafeEncodePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return string.Empty;

            if (filePath.Contains("..") || filePath.Contains("~"))
                return string.Empty;

            var segments = filePath.Split('/');
            var encodedSegments = segments.Select(s => Uri.EscapeDataString(s));
            return string.Join("/", encodedSegments);
        }

        protected string GetDisplayTitle()
        {
            return _albumInfo?.AlbumName ?? AlbumName ?? "Unknown Album";
        }

        protected string GetTrackTitle(int index)
        {
            if (_albumInfo == null || index >= _albumInfo.Tracks.Count) return "";
            return Path.GetFileNameWithoutExtension(Path.GetFileName(_albumInfo.Tracks[index].Name));
        }

        /// <summary>
        /// Gets a track stream URL by index. Uses pre-fetched URLs if available.
        /// </summary>
        private async Task<string> GetTrackStreamUrlAsync(int index)
        {
            // Use pre-fetched URL if available
            if (index >= 0 && _trackStreamUrls.Count > index && !string.IsNullOrWhiteSpace(_trackStreamUrls[index]))
            {
                return _trackStreamUrls[index];
            }

            // Fall back to fetching on-demand
            if (_albumInfo == null || index < 0 || index >= _albumInfo.Tracks.Count) return string.Empty;
            return await GetTrackStreamUrlAsync(_albumInfo.Tracks[index].Name);
        }

        /// <summary>
        /// Gets a direct SAS URL for streaming a track from blob storage.
        /// Falls back to the controller streaming endpoint if SAS URL is unavailable.
        /// </summary>
        private async Task<string> GetTrackStreamUrlAsync(string fileName)
        {
            var safePath = SafeEncodePath(fileName);

            // Preferred: direct SAS URL from Blob Storage via API
            try
            {
                var result = await Http.GetFromJsonAsync<StreamUrlResponseDto>($"api/music/url/{safePath}");
                if (!string.IsNullOrWhiteSpace(result?.Url))
                {
                    return result.Url;
                }
            }
            catch (Exception ex)
            {
                // Log and fall back to server streaming if SAS is unavailable
                Console.WriteLine($"Failed to get SAS URL for {fileName}: {ex.Message}");
            }

            // Fallback: stream through the MusicController
            return $"api/music/{safePath}";
        }

        protected string GetTrackAlbumArtUrl(int index)
        {
            if (_albumInfo == null || index >= _albumInfo.Tracks.Count) return "";

            // For now, all tracks share the album cover art
            return _albumInfo.CoverArtUrl;
        }

        protected bool IsCurrentTrack(int index)
        {
            return _currentTrackIndex == index;
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

        protected async Task PlayTrack(int index)
        {
            if (_albumInfo == null || index >= _albumInfo.Tracks.Count) return;

            _currentTrackIndex = index;
            _streamUrl = await GetTrackStreamUrlAsync(index);
            _currentTime = 0;
            _duration = 0;

            if (_jsModule != null && !string.IsNullOrWhiteSpace(_streamUrl))
            {
                await _jsModule.InvokeVoidAsync("changeTrack", _audioElement, _streamUrl, IsCurrentTrackRestricted());
                _isPlaying = true;
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
            // Store the duration for the current track
            if (_currentTrackIndex >= 0)
            {
                _trackDurations[_currentTrackIndex] = duration;
            }
            InvokeAsync(StateHasChanged);
        }

        protected string GetTrackDuration(int index)
        {
            if (_trackDurations.TryGetValue(index, out var duration) && duration > 0)
            {
                return FormatTime(duration);
            }
            return UNKNOWN_DURATION_PLACEHOLDER;
        }

        [JSInvokable]
        public async Task AudioEnded()
        {
            // Move to next track if available
            if (_albumInfo != null && _currentTrackIndex < _albumInfo.Tracks.Count - 1)
            {
                _currentTrackIndex++;
                _streamUrl = await GetTrackStreamUrlAsync(_currentTrackIndex);
                _currentTime = 0;
                _duration = 0;

                if (_jsModule != null && !string.IsNullOrWhiteSpace(_streamUrl))
                {
                    await _jsModule.InvokeVoidAsync("changeTrack", _audioElement, _streamUrl, IsCurrentTrackRestricted());
                }
                await InvokeAsync(StateHasChanged);
            }
            else
            {
                _isPlaying = false;
                await InvokeAsync(StateHasChanged);
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

        protected async Task OnProgressBarClick(Microsoft.AspNetCore.Components.Web.MouseEventArgs e)
        {
            if (_jsModule != null && _duration > 0)
            {
                var width = await _jsModule.InvokeAsync<double>("getElementWidth", _progressBarContainer);
                if (width > 0)
                {
                    await _jsModule.InvokeVoidAsync("seekToPosition", _audioElement, e.OffsetX, width, IsProgressBarRestricted(), PREVIEW_DURATION_SECONDS);
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
                    _isMuted = false;
                    _volume = _previousVolume;
                    await _jsModule.InvokeVoidAsync("setMuted", _audioElement, false);
                    await _jsModule.InvokeVoidAsync("setVolume", _audioElement, _previousVolume);
                }
                else
                {
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

        protected async Task PlayPreviousTrack()
        {
            if (_currentTrackIndex > 0)
            {
                await PlayTrack(_currentTrackIndex - 1);
            }
        }

        protected async Task PlayNextTrack()
        {
            if (_albumInfo != null && _currentTrackIndex < _albumInfo.Tracks.Count - 1)
            {
                await PlayTrack(_currentTrackIndex + 1);
            }
        }
    }
}