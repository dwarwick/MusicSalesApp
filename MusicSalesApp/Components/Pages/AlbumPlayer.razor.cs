using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages
{
    public partial class AlbumPlayerModel : BlazorBase, IAsyncDisposable
    {
        private const double PREVIEW_DURATION_SECONDS = 60.0;
        private const string UNKNOWN_DURATION_PLACEHOLDER = "--:--";

        [Parameter]
        public string AlbumName { get; set; }

        [Parameter]
        public int? PlaylistId { get; set; }

        protected bool _loading = true;
        protected string _error;
        protected AlbumInfo _albumInfo;
        protected string _playlistName;
        protected bool _isPlaylistMode;
        protected string _streamUrl;
        protected bool _isPlaying;
        protected double _currentTime;
        protected double _duration;
        protected ElementReference _audioElement;
        protected ElementReference _progressBarContainer;
        protected ElementReference _volumeBarContainer;
        protected bool _shuffleEnabled;
        protected bool _repeatEnabled;
        private List<int> _shuffledTrackOrder = new List<int>();
        private int _currentShufflePosition = 0;
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
        private Dictionary<int, string> _trackImageUrls = new Dictionary<int, string>();
        private Dictionary<string, Models.SongMetadata> _metadataLookup = new Dictionary<string, Models.SongMetadata>();
        private IJSObjectReference _jsModule;
        private DotNetObjectReference<AlbumPlayerModel> _dotNetRef;
        private bool invokedJs = false;

        private bool isLoading;
        protected bool _hasActiveSubscription;

        protected override async Task OnParametersSetAsync()
        {
            _isPlaylistMode = PlaylistId.HasValue;

            if (!isLoading)
            {
                isLoading = true;

                if (_isPlaylistMode)
                {
                    await LoadPlaylistInfo();
                }
                else
                {
                    await LoadAlbumInfo();
                }
            }            
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!invokedJs && !_loading && _albumInfo != null && _albumInfo.Tracks.Any())
            {
                invokedJs = true;
                _dotNetRef = DotNetObjectReference.Create(this);
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/AlbumPlayer.razor.js");

                await _jsModule.InvokeVoidAsync("initAudioPlayer", _audioElement, _dotNetRef, IsCurrentTrackRestricted(), PREVIEW_DURATION_SECONDS);
                await _jsModule.InvokeVoidAsync("setupProgressBarDrag", _progressBarContainer, _audioElement, _dotNetRef);
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
            catch (JSDisconnectedException ex)
            {
                Logger.LogDebug(ex, "Album player JS runtime disconnected during disposal");
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

                // Get metadata from database for this album
                var albumMetadata = await SongMetadataService.GetByAlbumNameAsync(decodedAlbumName);
                
                if (albumMetadata == null || !albumMetadata.Any())
                {
                    _error = $"Album '{decodedAlbumName}' not found.";
                    _loading = false;
                    return;
                }

                // Find album cover metadata
                var albumCoverMeta = albumMetadata.FirstOrDefault(m => m.IsAlbumCover);
                
                if (albumCoverMeta == null)
                {
                    _error = $"Album cover not found for '{decodedAlbumName}'.";
                    _loading = false;
                    return;
                }

                // Find track metadata - use SQL metadata directly, no need for controller call
                var trackMetadata = albumMetadata
                    .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath) || m.FileExtension == ".mp3")
                    .OrderBy(m => m.TrackNumber ?? 9999) // Sort by track number
                    .ThenBy(m => Path.GetFileName(m.Mp3BlobPath ?? m.BlobPath))
                    .ToList();

                // Convert metadata to StorageFileInfo for compatibility with existing code
                var tracks = trackMetadata.Select(trackMeta =>
                {
                    var mp3Path = trackMeta.Mp3BlobPath ?? trackMeta.BlobPath;
                    return new StorageFileInfo
                    {
                        Name = mp3Path,
                        Length = 0, // Not needed for playback
                        ContentType = "audio/mpeg",
                        LastModified = trackMeta.UpdatedAt,
                        Tags = new Dictionary<string, string>() // No longer using tags
                    };
                }).ToList();

                if (!tracks.Any())
                {
                    _error = $"No tracks found for album '{decodedAlbumName}'.";
                    _loading = false;
                    return;
                }

                // Get album price from database metadata
                decimal albumPrice = albumCoverMeta.AlbumPrice ?? PriceDefaults.DefaultAlbumPrice;

                // Build metadata lookup for track info (use Mp3BlobPath if available)
                _metadataLookup = albumMetadata.ToDictionary(m => m.Mp3BlobPath ?? m.BlobPath, m => m);

                var coverImagePath = albumCoverMeta.ImageBlobPath ?? albumCoverMeta.BlobPath;
                _albumInfo = new AlbumInfo
                {
                    AlbumName = decodedAlbumName,
                    CoverArtUrl = $"api/music/{SafeEncodePath(coverImagePath)}", // Cover art from metadata
                    CoverArtFileName = coverImagePath,
                    Tracks = tracks,
                    Price = albumPrice
                };

                // Pre-fetch all track SAS URLs in parallel for better performance
                var trackUrlTasks = tracks.Select(t => GetTrackStreamUrlAsync(t.Name));
                _trackStreamUrls = (await Task.WhenAll(trackUrlTasks)).ToList();

                // Find and store track images from metadata (JPEGs with same base name as MP3, not album covers)
                var trackImageMetadata = albumMetadata
                    .Where(m => !m.IsAlbumCover && !string.IsNullOrEmpty(m.ImageBlobPath))
                    .ToList();
                
                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    var trackBaseName = Path.GetFileNameWithoutExtension(Path.GetFileName(track.Name));
                    var trackFolder = Path.GetDirectoryName(track.Name)?.Replace("\\", "/") ?? "";
                    
                    // Look for image metadata with same base name in same folder
                    var trackImageMeta = trackImageMetadata.FirstOrDefault(imgMeta =>
                    {
                        var imgPath = imgMeta.ImageBlobPath;
                        var imgBaseName = Path.GetFileNameWithoutExtension(Path.GetFileName(imgPath));
                        var imgFolder = Path.GetDirectoryName(imgPath)?.Replace("\\", "/") ?? "";
                        
                        return string.Equals(imgBaseName, trackBaseName, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(imgFolder, trackFolder, StringComparison.OrdinalIgnoreCase);
                    });
                    
                    if (trackImageMeta != null)
                    {
                        _trackImageUrls[i] = $"api/music/{SafeEncodePath(trackImageMeta.ImageBlobPath)}";
                    }
                }

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
                Logger.LogError(ex, "Error loading album info for {AlbumName}", AlbumName);
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
                // Check subscription status
                var subscriptionResponse = await Http.GetFromJsonAsync<SubscriptionStatusDto>("api/subscription/status");
                _hasActiveSubscription = subscriptionResponse?.HasSubscription ?? false;

                // Check if user owns tracks in the album
                var ownedResponse = await Http.GetFromJsonAsync<IEnumerable<string>>("api/cart/owned");
                _ownedSongs = new HashSet<string>(ownedResponse ?? Enumerable.Empty<string>());
                _ownsAlbum = _albumInfo.Tracks.All(t => _ownedSongs.Contains(t.Name));

                // Check if album is in cart (albums are stored as individual tracks)
                var cartResponse = await Http.GetFromJsonAsync<CartResponseDto>("api/cart");
                if (cartResponse?.Items != null)
                {
                    var cartSongs = new HashSet<string>(cartResponse.Items.Select(i => i.SongFileName));
                    // An album is in cart if all of its tracks are in cart
                    _inCart = _albumInfo.Tracks.All(t => cartSongs.Contains(t.Name));
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogDebug(ex, "Unable to load album status; user may not be authenticated");
            }
        }

        private async Task LoadPlaylistInfo()
        {
            _loading = true;
            _error = null;

            if (!PlaylistId.HasValue)
            {
                _error = "No playlist ID provided.";
                _loading = false;
                return;
            }

            try
            {
                // Check authentication status
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

                if (!_isAuthenticated)
                {
                    _error = "You must be logged in to view playlists.";
                    _loading = false;
                    return;
                }

                // Get the current user
                var user = authState.User;
                var appUser = await UserManager.GetUserAsync(user);
                if (appUser == null)
                {
                    _error = "User not found.";
                    _loading = false;
                    return;
                }

                // Get playlist from service
                var playlist = await PlaylistService.GetPlaylistByIdAsync(PlaylistId.Value);
                if (playlist == null)
                {
                    _error = "Playlist not found.";
                    _loading = false;
                    return;
                }

                // Verify user owns the playlist
                if (playlist.UserId != appUser.Id)
                {
                    _error = "You do not have access to this playlist.";
                    _loading = false;
                    return;
                }

                _playlistName = playlist.PlaylistName;

                // Get playlist songs
                var playlistSongs = await PlaylistService.GetPlaylistSongsAsync(PlaylistId.Value);
                if (playlistSongs == null || !playlistSongs.Any())
                {
                    _error = "This playlist is empty.";
                    _loading = false;
                    return;
                }

                // Build list of tracks from playlist songs
                var tracks = new List<StorageFileInfo>();
                var allMetadata = new List<Models.SongMetadata>();
                
                foreach (var userPlaylist in playlistSongs)
                {
                    var songMetadata = userPlaylist.OwnedSong?.SongMetadata;
                    if (songMetadata != null && !string.IsNullOrEmpty(songMetadata.Mp3BlobPath))
                    {
                        tracks.Add(new StorageFileInfo
                        {
                            Name = songMetadata.Mp3BlobPath,
                            Length = 0,
                            ContentType = "audio/mpeg",
                            LastModified = songMetadata.UpdatedAt,
                            Tags = new Dictionary<string, string>()
                        });
                        allMetadata.Add(songMetadata);
                    }
                }

                if (!tracks.Any())
                {
                    _error = "No playable tracks found in this playlist.";
                    _loading = false;
                    return;
                }

                // Build metadata lookup for track info (handle potential duplicates by using first occurrence)
                _metadataLookup = allMetadata
                    .GroupBy(m => m.Mp3BlobPath)
                    .ToDictionary(g => g.Key, g => g.First());

                // For playlist mode, we use the first track's image as the "cover"
                if (allMetadata.Count == 0)
                {
                    _error = "No playable tracks found in this playlist.";
                    _loading = false;
                    return;
                }
                
                var firstTrackMeta = allMetadata.First();
                var coverImagePath = firstTrackMeta.ImageBlobPath ?? "";
                var coverImageUrl = !string.IsNullOrEmpty(coverImagePath) 
                    ? $"api/music/{SafeEncodePath(coverImagePath)}" 
                    : "";

                _albumInfo = new AlbumInfo
                {
                    AlbumName = _playlistName,
                    CoverArtUrl = coverImageUrl,
                    CoverArtFileName = coverImagePath,
                    Tracks = tracks,
                    Price = 0 // Playlists don't have a price
                };

                // Pre-fetch all track SAS URLs in parallel for better performance
                var trackUrlTasks = tracks.Select(t => GetTrackStreamUrlAsync(t.Name));
                _trackStreamUrls = (await Task.WhenAll(trackUrlTasks)).ToList();

                // Store track images from metadata
                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                    {
                        if (!string.IsNullOrEmpty(metadata.ImageBlobPath))
                        {
                            _trackImageUrls[i] = $"api/music/{SafeEncodePath(metadata.ImageBlobPath)}";
                        }
                    }
                }

                // Set up the first track
                _currentTrackIndex = 0;
                _streamUrl = _trackStreamUrls.Count > 0 ? _trackStreamUrls[0] : string.Empty;

                // Since user owns all songs in playlist, mark as owned
                _ownsAlbum = true;
                _ownedSongs = new HashSet<string>(tracks.Select(t => t.Name));
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Logger.LogError(ex, "Error loading playlist info for playlist {PlaylistId}", PlaylistId);
            }
            finally
            {
                _loading = false;
            }
        }

        protected async Task ToggleCart()
        {
            if (_albumInfo == null) return;

            try
            {
                var trackFileNames = _albumInfo.Tracks.Select(t => t.Name).ToList();
                
                // Build dictionary of track filenames to metadata IDs
                var trackMetadataIds = new Dictionary<string, int>();
                foreach (var track in _albumInfo.Tracks)
                {
                    if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                    {
                        trackMetadataIds[track.Name] = metadata.Id;
                    }
                }

                var response = await Http.PostAsJsonAsync("api/cart/toggle-album", new
                {
                    AlbumName = _albumInfo.AlbumName,
                    TrackFileNames = trackFileNames,
                    Price = _albumInfo.Price,
                    TrackMetadataIds = trackMetadataIds
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

                        CartService.NotifyCartUpdated();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error toggling album cart for {AlbumName}", _albumInfo?.AlbumName);
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
        /// Restricted for non-authenticated users OR authenticated users who don't own the current track and don't have an active subscription.
        /// </summary>
        protected bool IsCurrentTrackRestricted()
        {
            // If user has an active subscription, they can listen to everything
            if (_hasActiveSubscription)
                return false;

            return !_isAuthenticated || !OwnsTrack(_currentTrackIndex);
        }

        protected bool IsProgressBarRestricted()
        {
            // If user has an active subscription, they can listen to everything
            if (_hasActiveSubscription)
                return false;

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
            if (_isPlaylistMode)
            {
                return _playlistName ?? "Unknown Playlist";
            }
            return _albumInfo?.AlbumName ?? AlbumName ?? "Unknown Album";
        }

        protected string GetTrackTitle(int index)
        {
            if (_albumInfo == null || index >= _albumInfo.Tracks.Count) return "";
            return Path.GetFileNameWithoutExtension(Path.GetFileName(_albumInfo.Tracks[index].Name));
        }

        protected string GetTrackNumber(int index)
        {
            if (_albumInfo == null || index >= _albumInfo.Tracks.Count) return (index + 1).ToString();
            
            var track = _albumInfo.Tracks[index];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata) && metadata.TrackNumber.HasValue)
            {
                return metadata.TrackNumber.Value.ToString();
            }
            
            return (index + 1).ToString(); // Fallback to 1-based index
        }

        protected string GetTrackImageUrl(int index)
        {
            return _trackImageUrls.TryGetValue(index, out var url) ? url : null;
        }

        protected double? GetTrackLengthSeconds(int index)
        {
            if (_albumInfo == null || index >= _albumInfo.Tracks.Count) return null;
            
            var track = _albumInfo.Tracks[index];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata) && metadata.TrackLength.HasValue)
            {
                return metadata.TrackLength.Value;
            }
            
            return null;
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
                Logger.LogWarning(ex, "Failed to get SAS URL for track {FileName}; using fallback", fileName);
            }

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
            
            // Update shuffle position if shuffle is enabled
            UpdateShufflePosition(index);
            
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
            // First check if we have a stored duration from playback
            if (_trackDurations.TryGetValue(index, out var duration) && duration > 0)
            {
                return FormatTime(duration);
            }
            
            // Otherwise, check if we have track length from index tags
            var trackLength = GetTrackLengthSeconds(index);
            if (trackLength.HasValue && trackLength.Value > 0)
            {
                return FormatTime(trackLength.Value);
            }
            
            return UNKNOWN_DURATION_PLACEHOLDER;
        }

        [JSInvokable]
        public async Task AudioEnded()
        {
            // Get next track index based on shuffle state
            var nextIndex = GetNextTrackIndex();
            
            if (nextIndex.HasValue)
            {
                _currentTrackIndex = nextIndex.Value;
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
            
            if (_shuffleEnabled)
            {
                // Generate a shuffled order when enabled
                GenerateShuffleOrder();
            }
            else
            {
                // Clear shuffle order when disabled
                _shuffledTrackOrder.Clear();
                _currentShufflePosition = 0;
            }
        }

        protected void ToggleRepeat()
        {
            _repeatEnabled = !_repeatEnabled;
        }

        /// <summary>
        /// Generates a shuffled order of track indices, ensuring the current track is first.
        /// </summary>
        private void GenerateShuffleOrder()
        {
            if (_albumInfo == null || _albumInfo.Tracks.Count == 0)
                return;

            // Create a list of all track indices except the current one
            var remainingIndices = Enumerable.Range(0, _albumInfo.Tracks.Count)
                .Where(i => i != _currentTrackIndex)
                .ToList();

            // Shuffle the remaining indices using Fisher-Yates algorithm
            var random = new Random();
            for (int i = remainingIndices.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var temp = remainingIndices[i];
                remainingIndices[i] = remainingIndices[j];
                remainingIndices[j] = temp;
            }

            // Build the shuffled order with current track first
            _shuffledTrackOrder = new List<int> { _currentTrackIndex };
            _shuffledTrackOrder.AddRange(remainingIndices);
            _currentShufflePosition = 0;
        }

        /// <summary>
        /// Gets the next track index based on shuffle state.
        /// </summary>
        private int? GetNextTrackIndex()
        {
            if (_albumInfo == null || _albumInfo.Tracks.Count == 0)
                return null;

            if (_shuffleEnabled)
            {
                // If shuffle order hasn't been generated yet, generate it
                if (_shuffledTrackOrder.Count == 0)
                {
                    GenerateShuffleOrder();
                }

                // Move to next position in shuffle order
                if (_currentShufflePosition < _shuffledTrackOrder.Count - 1)
                {
                    _currentShufflePosition++;
                    return _shuffledTrackOrder[_currentShufflePosition];
                }
                
                // End of shuffled playlist
                if (_repeatEnabled)
                {
                    // Loop back to the beginning and regenerate shuffle order
                    GenerateShuffleOrder();
                    return _shuffledTrackOrder[0];
                }
                
                return null;
            }
            else
            {
                // Normal sequential playback
                if (_currentTrackIndex < _albumInfo.Tracks.Count - 1)
                {
                    return _currentTrackIndex + 1;
                }
                
                // End of playlist
                if (_repeatEnabled)
                {
                    // Loop back to the first track
                    return 0;
                }
                
                return null;
            }
        }

        /// <summary>
        /// Gets the previous track index based on shuffle state.
        /// </summary>
        private int? GetPreviousTrackIndex()
        {
            if (_albumInfo == null || _albumInfo.Tracks.Count == 0)
                return null;

            if (_shuffleEnabled)
            {
                // If shuffle order hasn't been generated yet, can't go back
                if (_shuffledTrackOrder.Count == 0 || _currentShufflePosition == 0)
                    return null;

                _currentShufflePosition--;
                return _shuffledTrackOrder[_currentShufflePosition];
            }
            else
            {
                // Normal sequential playback
                if (_currentTrackIndex > 0)
                {
                    return _currentTrackIndex - 1;
                }
                
                return null; // Beginning of playlist
            }
        }

        /// <summary>
        /// Updates the current shuffle position when a track is played directly (e.g., by clicking on it).
        /// </summary>
        private void UpdateShufflePosition(int trackIndex)
        {
            if (!_shuffleEnabled || _shuffledTrackOrder.Count == 0)
                return;

            // Find this track in the shuffle order
            var position = _shuffledTrackOrder.IndexOf(trackIndex);
            if (position >= 0)
            {
                _currentShufflePosition = position;
            }
            else
            {
                // Track not in current shuffle order, regenerate with this track
                _currentTrackIndex = trackIndex;
                GenerateShuffleOrder();
            }
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
            var previousIndex = GetPreviousTrackIndex();
            if (previousIndex.HasValue)
            {
                await PlayTrack(previousIndex.Value);
            }
        }

        protected async Task PlayNextTrack()
        {
            var nextIndex = GetNextTrackIndex();
            if (nextIndex.HasValue)
            {
                await PlayTrack(nextIndex.Value);
            }
        }
    }
}