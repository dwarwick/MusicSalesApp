using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Notifications;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Players
{
    public partial class PlaylistPlayerInteractiveModel : BlazorBase, IAsyncDisposable
    {
        private const double PREVIEW_DURATION_SECONDS = 60.0;
        private const string UNKNOWN_DURATION_PLACEHOLDER = "--:--";

        [Parameter]
        public int? PlaylistId { get; set; }

        [Parameter]
        public int? RecommendedUserId { get; set; }

        [Parameter]
        public string ArtistName { get; set; }

        [Parameter]
        public int? CreatorId { get; set; }

        [Parameter]
        public string GenreName { get; set; }

        protected bool _loading = true;
        protected string _error;
        protected PlaylistInfo _playlistInfo;
        protected string _playlistName;
        protected bool _isRecommendedMode;
        protected bool _isArtistMode;
        protected bool _isCreatorMode;
        protected bool _isGenreMode;
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
        protected double _volume = 0.4;
        protected double _previousVolume = 0.4;
        protected bool _isMuted;
        protected bool _isAuthenticated;
        protected int _currentTrackIndex;
        protected Dictionary<int, double> _trackDurations = new Dictionary<int, double>();
        protected Dictionary<int, int> _trackStreamCounts = new Dictionary<int, int>();
        private List<string> _trackStreamUrls = new List<string>();
        private Dictionary<int, CoverArtSource> _trackArtSources = new Dictionary<int, CoverArtSource>();
        private Dictionary<string, Models.SongMetadata> _metadataLookup = new Dictionary<string, Models.SongMetadata>();

        protected Models.SongLyrics _currentTrackLyrics;
        protected MusicSalesApp.Common.Contracts.LyricsTimingsDocument _currentTrackTimings;

        /// <summary>
        /// Which song <see cref="_currentTrackLyrics"/> belongs to, so the lookup happens once per
        /// track rather than once per render.
        /// </summary>
        private int _lyricsLoadedForSongId = -1;
        private IJSObjectReference _jsModule;
        private DotNetObjectReference<PlaylistPlayerInteractiveModel> _dotNetRef;
        private bool invokedJs = false;

        private bool _hasLoadedData = false;
        protected bool _tipReturnHandled;
        protected bool IsProcessingTipReturn => !string.IsNullOrEmpty(TipStatus) && !_tipReturnHandled;
        private int? _lastLoadedPlaylistId;
        private int? _lastLoadedRecommendedUserId;
        private string _lastLoadedArtistName;
        private int? _lastLoadedCreatorId;
        private string _lastLoadedGenreName;
        protected bool _hasActiveSubscription;
        protected bool _isAdmin;
        private int? _currentUserId;
        private int? _currentUserCreatorId;
        private int _defaultStreamQualifyingSeconds = 30;
        private Action<int, int> _streamCountUpdatedHandler;
        private Action<int, int> _hubStreamCountHandler;
        protected SubscribeCtaDialogModel _subscribeCtaDialog;
        protected TipDialogModel _tipDialog;

        [Parameter]
        public string TipStatus { get; set; }

        [Parameter]
        public string TipPayPalToken { get; set; }

        protected SfToast _toastRef;
        protected int _tipCreatorId;
        protected int? _tipSongMetadataId;

        protected int GetCurrentTrackCreatorId()
        {
            if (_playlistInfo == null || _currentTrackIndex >= _playlistInfo.Tracks.Count) return 0;
            var track = _playlistInfo.Tracks[_currentTrackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                return metadata.CreatorId ?? 0;
            }
            return 0;
        }

        protected bool CanShowTipButton()
        {
            if (!_isAuthenticated) return false;
            if (_playlistInfo == null || _currentTrackIndex >= _playlistInfo.Tracks.Count) return false;
            var track = _playlistInfo.Tracks[_currentTrackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                if (metadata.CreatorId == null || metadata.CreatorId <= 0) return false;
                // Check if current user is the creator (via nav prop or creator ID lookup)
                if (metadata.Creator != null && _currentUserId.HasValue && metadata.Creator.UserId == _currentUserId.Value) return false;
                if (_currentUserCreatorId.HasValue && metadata.CreatorId == _currentUserCreatorId.Value) return false;
                return true;
            }
            return false;
        }

        protected async Task ShowTipDialog()
        {
            _tipCreatorId = GetCurrentTrackCreatorId();
            _tipSongMetadataId = GetCurrentTrackMetadataId();
            if (_tipDialog != null)
            {
                await _tipDialog.ShowAsync();
            }
        }

        protected bool CanShowTipButtonForTrack(int trackIndex)
        {
            if (!_isAuthenticated) return false;
            if (_playlistInfo == null || trackIndex >= _playlistInfo.Tracks.Count) return false;
            var track = _playlistInfo.Tracks[trackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                if (metadata.CreatorId == null || metadata.CreatorId <= 0) return false;
                if (metadata.Creator != null && _currentUserId.HasValue && metadata.Creator.UserId == _currentUserId.Value) return false;
                if (_currentUserCreatorId.HasValue && metadata.CreatorId == _currentUserCreatorId.Value) return false;
                return true;
            }
            return false;
        }

        protected async Task ShowTipDialogForTrack(int trackIndex)
        {
            if (_playlistInfo == null || trackIndex >= _playlistInfo.Tracks.Count) return;
            var track = _playlistInfo.Tracks[trackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                _tipCreatorId = metadata.CreatorId ?? 0;
                _tipSongMetadataId = metadata.Id;
                if (_tipDialog != null)
                {
                    await _tipDialog.ShowAsync();
                }
            }
        }

        private async Task HandleTipReturnAsync()
        {
            var (tipStatus, tipToken) = Helpers.TipUrlHelper.GetLastTipParams(NavigationManager.Uri);

            if (tipStatus == "approved" && !string.IsNullOrEmpty(tipToken))
            {
                try
                {
                    var (success, errorMessage, tipAmount) = await TipService.CaptureTipAsync(tipToken);
                    if (success)
                    {
                        await ShowTipToastAsync($"Your ${tipAmount:F2} tip was sent successfully! Thank you for supporting this creator.", true);
                    }
                    else
                    {
                        await ShowTipToastAsync(errorMessage ?? "Failed to process your tip. Please try again.", false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error capturing tip on return from PayPal");
                    await ShowTipToastAsync("An error occurred processing your tip.", false);
                }
            }
            else if (tipStatus == "cancelled")
            {
                if (!string.IsNullOrEmpty(tipToken))
                {
                    await TipService.CancelTipAsync(tipToken);
                }
                await ShowTipToastAsync("Tip payment was cancelled.", false);
            }

            // Clear tip-related query parameters from the browser URL without triggering
            // Blazor's navigation lifecycle (avoids re-running OnParametersSet / data reload)
            var uri = NavigationManager.Uri;
            var baseUri = uri.Split('?')[0];
            await JS.InvokeVoidAsync("history.replaceState", null, "", baseUri);
        }

        private async Task ShowTipToastAsync(string message, bool isSuccess)
        {
            if (_toastRef != null)
            {
                await _toastRef.ShowAsync(new ToastModel
                {
                    Title = isSuccess ? "Tip Sent!" : "Tip Error",
                    Content = message,
                    CssClass = isSuccess ? "e-toast-success" : "e-toast-danger",
                    Icon = isSuccess ? "e-success" : "e-error"
                });
            }
        }

        protected override async Task OnInitializedAsync()
        {
            // Set mode flags before any await so they are available when
            // OnAfterRenderAsync fires (which happens before OnParametersSet
            // when OnInitializedAsync is truly async).
            SetModeFlags();

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
            // Update local tracking if this song is in our list
            if (_trackStreamCounts.ContainsKey(songMetadataId))
            {
                _trackStreamCounts[songMetadataId] = newCount;
                InvokeAsync(StateHasChanged);
            }
        }

        private void SetModeFlags()
        {
            _isRecommendedMode = RecommendedUserId.HasValue;
            _isArtistMode = !string.IsNullOrEmpty(ArtistName);
            _isCreatorMode = CreatorId.HasValue;
            _isGenreMode = !string.IsNullOrEmpty(GenreName);
        }

        protected override void OnParametersSet()
        {
            // Set the mode flags based on which parameter is provided
            SetModeFlags();
            
            // Check if parameters have changed and reset the flag if needed
            bool parametersChanged;
            if (_isGenreMode)
            {
                parametersChanged = GenreName != _lastLoadedGenreName;
            }
            else if (_isArtistMode)
            {
                parametersChanged = ArtistName != _lastLoadedArtistName;
            }
            else if (_isCreatorMode)
            {
                parametersChanged = CreatorId != _lastLoadedCreatorId;
            }
            else if (_isRecommendedMode)
            {
                parametersChanged = RecommendedUserId != _lastLoadedRecommendedUserId;
            }
            else
            {
                parametersChanged = PlaylistId != _lastLoadedPlaylistId;
            }
            
            if (parametersChanged)
            {
                _hasLoadedData = false;
                invokedJs = false;
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            // Load data only on first render or when parameters change to avoid DbContext threading issues
            if (!_hasLoadedData)
            {
                _hasLoadedData = true;
                
                try
                {
                    // Load default stream qualifying seconds for songs without a creator
                    _defaultStreamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();

                    if (_isGenreMode)
                    {
                        _lastLoadedGenreName = GenreName;
                        await LoadGenrePlaylistInfo();
                    }
                    else if (_isArtistMode)
                    {
                        _lastLoadedArtistName = ArtistName;
                        await LoadArtistPlaylistInfo();
                    }
                    else if (_isCreatorMode)
                    {
                        _lastLoadedCreatorId = CreatorId;
                        await LoadCreatorPlaylistInfo();
                    }
                    else if (_isRecommendedMode)
                    {
                        _lastLoadedRecommendedUserId = RecommendedUserId;
                        await LoadRecommendedPlaylistInfo();
                    }
                    else
                    {
                        _lastLoadedPlaylistId = PlaylistId;
                        await LoadPlaylistInfo();
                    }
                }
                finally
                {
                    await InvokeAsync(StateHasChanged);
                }

                return; // Force a new render cycle so the DOM is updated before tip handling or JS init
            }

            // Handle return from PayPal tip approval before JS init.
            // IsProcessingTipReturn keeps the page in loading state (audio element not rendered),
            // so we must process the tip first, then re-render to get the audio element in the DOM.
            if (!_tipReturnHandled && !_loading && _playlistInfo != null
                && !string.IsNullOrEmpty(TipStatus)
                && (!string.IsNullOrEmpty(TipPayPalToken) || TipStatus == "cancelled"))
            {
                _tipReturnHandled = true;
                await HandleTipReturnAsync();
                await InvokeAsync(StateHasChanged);
                return; // Re-render will now show content with audio element; JS init happens on next cycle
            }

            // Initialize JS after data is loaded and the content (including audio element) is rendered
            if (!invokedJs && !_loading && !IsProcessingTipReturn && _playlistInfo != null && _playlistInfo.Tracks.Any())
            {
                invokedJs = true;
                _dotNetRef = DotNetObjectReference.Create(this);
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Players/PlaylistPlayerInteractive.razor.js");

                await _jsModule.InvokeVoidAsync("initAudioPlayer", _audioElement, _dotNetRef, IsCurrentTrackRestricted(), PREVIEW_DURATION_SECONDS, GetCurrentTrackMetadataId(), GetCurrentTrackStreamQualifyingSeconds());
                await _jsModule.InvokeVoidAsync("setupProgressBarDrag", _progressBarContainer, _audioElement, _dotNetRef);
                await _jsModule.InvokeVoidAsync("setupVolumeBarDrag", _volumeBarContainer, _audioElement, _dotNetRef);

                // Load saved volume from localStorage
                var savedVolume = await _jsModule.InvokeAsync<double>("getSavedVolume");
                _volume = savedVolume;
                _previousVolume = savedVolume;

                // Ensure an initial track source is set for the audio element
                if (!string.IsNullOrWhiteSpace(_streamUrl))
                {
                    await _jsModule.InvokeVoidAsync("setTrackSource", _audioElement, _streamUrl);
                }

                await InvokeAsync(StateHasChanged);
            }

            await RefreshCurrentTrackLyricsAsync();
        }

        /// <summary>
        /// Load the current track's lyrics when the track changes.
        ///
        /// <para>
        /// Hooked here rather than in <c>PlayTrack</c> because the index is assigned from eight
        /// different places - initial load for each of the five playlist kinds, auto-advance, shuffle,
        /// and the tracklist. Keying off the id that ends up rendered catches all of them, including
        /// any added later, and costs one comparison per render.
        /// </para>
        /// </summary>
        private async Task RefreshCurrentTrackLyricsAsync()
        {
            var songId = GetCurrentTrackMetadataId();

            if (songId == _lyricsLoadedForSongId)
            {
                return;
            }

            _lyricsLoadedForSongId = songId;
            _currentTrackLyrics = songId > 0 ? await LyricsService.GetForSongAsync(songId) : null;

            // The document as well as the row: the scroller draws its words from this, so a player
            // without it shows an empty panel however good the timings are.
            _currentTrackTimings = songId > 0
                ? await LyricsService.GetPublishedTimingsAsync(songId)
                : null;

            // The toggle appears or disappears with the track, so the page has to be told.
            await InvokeAsync(StateHasChanged);
        }

        /// <summary>Whether the track now playing has lyrics a listener may see.</summary>
        protected bool HasPublishedLyrics() =>
            _currentTrackLyrics is { Status: Models.SongLyricsStatus.Published }
            && !string.IsNullOrWhiteSpace(_currentTrackLyrics.TimingsBlobPath);

        /// <summary>
        /// The timings URL for the current track, carrying the version as a cache-buster.
        /// </summary>
        /// <remarks>
        /// Changing between tracks is what makes the scroller re-fetch: it compares what it was last
        /// given, so a new URL is a new document and the same URL is a no-op.
        /// </remarks>
        protected string GetLyricsTimingsUrl() =>
            HasPublishedLyrics()
                ? $"/api/music/{_currentTrackLyrics.TimingsBlobPath}?v={_currentTrackLyrics.Version}"
                : null;

        protected void ToggleLyrics() => _showLyrics = !_showLyrics;

        protected bool _showLyrics;

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
                Logger.LogDebug(ex, "Playlist player JS runtime disconnected during disposal");
            }

            _dotNetRef?.Dispose();
        }

        private async Task LoadSubscriptionStatus()
        {
            try
            {
                // Check subscription status directly via service (Blazor Server runs on the server)
                if (_currentUserId.HasValue)
                {
                    _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(_currentUserId.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Unable to load subscription status");
            }
        }

        private async Task LoadUserContext(System.Security.Claims.ClaimsPrincipal claimsPrincipal)
        {
            _isAdmin = claimsPrincipal.IsInRole(Common.Helpers.Roles.Admin);
            _currentUserId = GetUserId(claimsPrincipal);

            // Look up the current user's creator ID for self-tip prevention
            if (_currentUserId.HasValue)
            {
                var creator = await CreatorService.GetCreatorByUserIdAsync(_currentUserId.Value);
                _currentUserCreatorId = creator?.Id;
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
                await LoadUserContext(authState.User);
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
                    var songMetadata = userPlaylist.SongMetadata;
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

                // Initialize stream counts from metadata
                _trackStreamCounts.Clear();
                foreach (var meta in allMetadata)
                {
                    _trackStreamCounts[meta.Id] = meta.NumberOfStreams;
                }

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

                _playlistInfo = new PlaylistInfo
                {
                    PlaylistName = _playlistName,
                    CoverArtUrl = coverImageUrl,
                    CoverArtFileName = coverImagePath,
                    Tracks = tracks,
                    MetadataId = firstTrackMeta.Id // Use first track's metadata ID for playlists
                };

                // Check subscription status before generating SAS URLs (affects URL lifetime)
                _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(appUser.Id);

                // Pre-fetch all track SAS URLs in a single batch request
                _trackStreamUrls = GetTrackStreamUrlsBatch(tracks);

                // Store track images from metadata
                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                    {
                        if (!string.IsNullOrEmpty(metadata.ImageBlobPath))
                        {
                            _trackArtSources[i] = BuildTrackArtSource(metadata);
                        }
                    }
                }

                // Set up the first track
                _currentTrackIndex = 0;
                _streamUrl = _trackStreamUrls.Count > 0 ? _trackStreamUrls[0] : string.Empty;
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

        private async Task LoadRecommendedPlaylistInfo()
        {
            _loading = true;
            _error = null;

            if (!RecommendedUserId.HasValue)
            {
                _error = "No user ID provided.";
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
                    _error = "You must be logged in to view recommended playlists.";
                    _loading = false;
                    return;
                }

                // Get the current user
                var user = authState.User;
                var appUser = await UserManager.GetUserAsync(user);
                await LoadUserContext(authState.User);
                if (appUser == null)
                {
                    _error = "User not found.";
                    _loading = false;
                    return;
                }

                // Verify the recommended playlist is for the current user
                if (RecommendedUserId.Value != appUser.Id)
                {
                    _error = "You can only view your own recommended playlist.";
                    _loading = false;
                    return;
                }

                _playlistName = "Recommended For You";

                // Get recommended playlist from service
                var recommendedSongs = await RecommendationService.GetRecommendedPlaylistAsync(appUser.Id);
                if (recommendedSongs == null || !recommendedSongs.Any())
                {
                    _error = "No recommendations available yet. Like some songs to get personalized recommendations!";
                    _loading = false;
                    return;
                }

                // Build list of tracks from recommended songs
                var tracks = new List<StorageFileInfo>();
                var allMetadata = new List<Models.SongMetadata>();
                
                foreach (var recommendation in recommendedSongs.OrderBy(r => r.DisplayOrder))
                {
                    var songMetadata = recommendation.SongMetadata;
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
                    _error = "No playable tracks found in recommendations.";
                    _loading = false;
                    return;
                }

                // Build metadata lookup for track info (handle potential duplicates by using first occurrence)
                _metadataLookup = allMetadata
                    .GroupBy(m => m.Mp3BlobPath)
                    .ToDictionary(g => g.Key, g => g.First());

                // Initialize stream counts from metadata
                _trackStreamCounts.Clear();
                foreach (var meta in allMetadata)
                {
                    _trackStreamCounts[meta.Id] = meta.NumberOfStreams;
                }

                var firstTrackMeta = allMetadata.First();
                var coverImagePath = firstTrackMeta.ImageBlobPath ?? "";
                var coverImageUrl = !string.IsNullOrEmpty(coverImagePath) 
                    ? $"api/music/{SafeEncodePath(coverImagePath)}" 
                    : "";

                _playlistInfo = new PlaylistInfo
                {
                    PlaylistName = _playlistName,
                    CoverArtUrl = coverImageUrl,
                    CoverArtFileName = coverImagePath,
                    Tracks = tracks,
                    MetadataId = firstTrackMeta.Id
                };

                // Check subscription status before generating SAS URLs (affects URL lifetime)
                _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(appUser.Id);

                // Pre-fetch all track SAS URLs in a single batch request
                _trackStreamUrls = GetTrackStreamUrlsBatch(tracks);

                // Store track images from metadata
                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                    {
                        if (!string.IsNullOrEmpty(metadata.ImageBlobPath))
                        {
                            _trackArtSources[i] = BuildTrackArtSource(metadata);
                        }
                    }
                }

                // Set up the first track
                _currentTrackIndex = 0;
                _streamUrl = _trackStreamUrls.Count > 0 ? _trackStreamUrls[0] : string.Empty;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Logger.LogError(ex, "Error loading recommended playlist for user {UserId}", RecommendedUserId);
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task LoadArtistPlaylistInfo()
        {
            _loading = true;
            _error = null;

            if (string.IsNullOrEmpty(ArtistName))
            {
                _error = "No artist name provided.";
                _loading = false;
                return;
            }

            try
            {
                // Check authentication status
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

                if (_isAuthenticated)
                {
                    await LoadUserContext(authState.User);
                }

                // URL decode the artist name
                var decodedArtistName = Uri.UnescapeDataString(ArtistName);
                _playlistName = decodedArtistName;

                // Get all songs with this artist name
                var artistSongs = await SongMetadataService.GetByArtistNameAsync(decodedArtistName);
                if (artistSongs == null || !artistSongs.Any())
                {
                    _error = $"No songs found for artist '{decodedArtistName}'.";
                    _loading = false;
                    return;
                }

                // Build list of tracks from artist songs
                var tracks = new List<StorageFileInfo>();
                var allMetadata = new List<Models.SongMetadata>();
                
                foreach (var songMetadata in artistSongs.Where(s => !string.IsNullOrEmpty(s.Mp3BlobPath)))
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

                if (!tracks.Any())
                {
                    _error = $"No playable tracks found for artist '{decodedArtistName}'.";
                    _loading = false;
                    return;
                }

                // Build metadata lookup for track info (handle potential duplicates by using first occurrence)
                _metadataLookup = allMetadata
                    .GroupBy(m => m.Mp3BlobPath)
                    .ToDictionary(g => g.Key, g => g.First());

                // Initialize stream counts from metadata
                _trackStreamCounts.Clear();
                foreach (var meta in allMetadata)
                {
                    _trackStreamCounts[meta.Id] = meta.NumberOfStreams;
                }

                var firstTrackMeta = allMetadata.First();
                var coverImagePath = firstTrackMeta.ImageBlobPath ?? "";
                var coverImageUrl = !string.IsNullOrEmpty(coverImagePath) 
                    ? $"api/music/{SafeEncodePath(coverImagePath)}" 
                    : "";

                _playlistInfo = new PlaylistInfo
                {
                    PlaylistName = _playlistName,
                    CoverArtUrl = coverImageUrl,
                    CoverArtFileName = coverImagePath,
                    Tracks = tracks,
                    MetadataId = firstTrackMeta.Id
                };

                // Check subscription status before generating SAS URLs (affects URL lifetime)
                if (_isAuthenticated && _currentUserId.HasValue)
                {
                    _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(_currentUserId.Value);
                }

                // Pre-fetch all track SAS URLs in a single batch request
                _trackStreamUrls = GetTrackStreamUrlsBatch(tracks);

                // Store track images from metadata
                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                    {
                        if (!string.IsNullOrEmpty(metadata.ImageBlobPath))
                        {
                            _trackArtSources[i] = BuildTrackArtSource(metadata);
                        }
                    }
                }

                // Set up the first track
                _currentTrackIndex = 0;
                _streamUrl = _trackStreamUrls.Count > 0 ? _trackStreamUrls[0] : string.Empty;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Logger.LogError(ex, "Error loading artist playlist for artist {ArtistName}", ArtistName);
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task LoadCreatorPlaylistInfo()
        {
            _loading = true;
            _error = null;

            if (!CreatorId.HasValue)
            {
                _error = "No creator ID provided.";
                _loading = false;
                return;
            }

            try
            {
                // Check authentication status
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

                if (_isAuthenticated)
                {
                    await LoadUserContext(authState.User);
                }

                // Get all songs from this creator
                var creatorSongs = await SongMetadataService.GetByCreatorIdAsync(CreatorId.Value);
                if (creatorSongs == null || !creatorSongs.Any())
                {
                    _error = "No songs found for this creator.";
                    _loading = false;
                    return;
                }

                // Get the creator's display name for the playlist title
                var firstSong = creatorSongs.First();
                var creatorDisplayName = GetArtistDisplayName(firstSong);
                _playlistName = creatorDisplayName;

                // Build list of tracks from creator songs
                var tracks = new List<StorageFileInfo>();
                var allMetadata = new List<Models.SongMetadata>();
                
                foreach (var songMetadata in creatorSongs.Where(s => !string.IsNullOrEmpty(s.Mp3BlobPath)))
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

                if (!tracks.Any())
                {
                    _error = "No playable tracks found for this creator.";
                    _loading = false;
                    return;
                }

                // Build metadata lookup for track info (handle potential duplicates by using first occurrence)
                _metadataLookup = allMetadata
                    .GroupBy(m => m.Mp3BlobPath)
                    .ToDictionary(g => g.Key, g => g.First());

                // Initialize stream counts from metadata
                _trackStreamCounts.Clear();
                foreach (var meta in allMetadata)
                {
                    _trackStreamCounts[meta.Id] = meta.NumberOfStreams;
                }

                var firstTrackMeta = allMetadata.First();
                var coverImagePath = firstTrackMeta.ImageBlobPath ?? "";
                var coverImageUrl = !string.IsNullOrEmpty(coverImagePath) 
                    ? $"api/music/{SafeEncodePath(coverImagePath)}" 
                    : "";

                _playlistInfo = new PlaylistInfo
                {
                    PlaylistName = _playlistName,
                    CoverArtUrl = coverImageUrl,
                    CoverArtFileName = coverImagePath,
                    Tracks = tracks,
                    MetadataId = firstTrackMeta.Id
                };

                // Check subscription status before generating SAS URLs (affects URL lifetime)
                if (_isAuthenticated && _currentUserId.HasValue)
                {
                    _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(_currentUserId.Value);
                }

                // Pre-fetch all track SAS URLs in a single batch request
                _trackStreamUrls = GetTrackStreamUrlsBatch(tracks);

                // Store track images from metadata
                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                    {
                        if (!string.IsNullOrEmpty(metadata.ImageBlobPath))
                        {
                            _trackArtSources[i] = BuildTrackArtSource(metadata);
                        }
                    }
                }

                // Set up the first track
                _currentTrackIndex = 0;
                _streamUrl = _trackStreamUrls.Count > 0 ? _trackStreamUrls[0] : string.Empty;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Logger.LogError(ex, "Error loading creator playlist for creator {CreatorId}", CreatorId);
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task LoadGenrePlaylistInfo()
        {
            _loading = true;
            _error = null;

            if (string.IsNullOrEmpty(GenreName))
            {
                _error = "No genre name provided.";
                _loading = false;
                return;
            }

            try
            {
                // Check authentication status
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

                if (_isAuthenticated)
                {
                    await LoadUserContext(authState.User);
                }

                // URL decode the genre name
                var decodedGenreName = Uri.UnescapeDataString(GenreName);
                _playlistName = decodedGenreName;

                // Get all songs with this genre
                var genreSongs = await SongMetadataService.GetByGenreAsync(decodedGenreName);
                if (genreSongs == null || !genreSongs.Any())
                {
                    _error = $"No songs found for genre '{decodedGenreName}'.";
                    _loading = false;
                    return;
                }

                // Build list of tracks from genre songs
                var tracks = new List<StorageFileInfo>();
                var allMetadata = new List<Models.SongMetadata>();
                
                foreach (var songMetadata in genreSongs.Where(s => !string.IsNullOrEmpty(s.Mp3BlobPath)))
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

                if (!tracks.Any())
                {
                    _error = $"No playable tracks found for genre '{decodedGenreName}'.";
                    _loading = false;
                    return;
                }

                // Build metadata lookup for track info (handle potential duplicates by using first occurrence)
                _metadataLookup = allMetadata
                    .GroupBy(m => m.Mp3BlobPath)
                    .ToDictionary(g => g.Key, g => g.First());

                // Initialize stream counts from metadata
                _trackStreamCounts.Clear();
                foreach (var meta in allMetadata)
                {
                    _trackStreamCounts[meta.Id] = meta.NumberOfStreams;
                }

                var firstTrackMeta = allMetadata.First();
                var coverImagePath = firstTrackMeta.ImageBlobPath ?? "";
                var coverImageUrl = !string.IsNullOrEmpty(coverImagePath) 
                    ? $"api/music/{SafeEncodePath(coverImagePath)}" 
                    : "";

                _playlistInfo = new PlaylistInfo
                {
                    PlaylistName = _playlistName,
                    CoverArtUrl = coverImageUrl,
                    CoverArtFileName = coverImagePath,
                    Tracks = tracks,
                    MetadataId = firstTrackMeta.Id
                };

                // Check subscription status before generating SAS URLs (affects URL lifetime)
                if (_isAuthenticated && _currentUserId.HasValue)
                {
                    _hasActiveSubscription = await SubscriptionService.HasActiveSubscriptionAsync(_currentUserId.Value);
                }

                // Pre-fetch all track SAS URLs in a single batch request
                _trackStreamUrls = GetTrackStreamUrlsBatch(tracks);

                // Store track images from metadata
                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                    {
                        if (!string.IsNullOrEmpty(metadata.ImageBlobPath))
                        {
                            _trackArtSources[i] = BuildTrackArtSource(metadata);
                        }
                    }
                }

                // Set up the first track
                _currentTrackIndex = 0;
                _streamUrl = _trackStreamUrls.Count > 0 ? _trackStreamUrls[0] : string.Empty;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Logger.LogError(ex, "Error loading genre playlist for genre {GenreName}", GenreName);
            }
            finally
            {
                _loading = false;
            }
        }

                /// <summary>
        /// Gets the artist display name for a song metadata entry, using persona name priority.
        /// Priority: 1. Persona.Name (if a persona is linked to the song)
        /// 2. SongMetadata.ArtistName
        /// 3. Creator.DisplayName
        /// 4. Creator.User.Email
        /// </summary>
        protected string GetArtistDisplayName(Models.SongMetadata metadata)
        {
            if (metadata == null) return ArtistDisplayNames.UnknownArtist;
            return metadata.GetEffectiveArtistName() is var name && !string.IsNullOrWhiteSpace(name)
                ? name
                : ArtistDisplayNames.UnknownArtist;
        }

        /// <summary>
        /// Gets the persona for the currently playing track (if any).
        /// </summary>
        protected Models.CreatorPersona GetCurrentTrackPersona()
        {
            if (_currentTrackIndex < 0 || _playlistInfo?.Tracks == null ||
                _currentTrackIndex >= _playlistInfo.Tracks.Count)
                return null;

            var track = _playlistInfo.Tracks[_currentTrackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                var persona = metadata.Persona;
                return (persona != null && persona.IsEnabled) ? persona : null;
            }

            return null;
        }

        /// <summary>
        /// Gets the Creator bio for the currently playing track.
        /// Returns null when an active persona is already displayed or no bio is set.
        /// </summary>
        protected string GetCurrentTrackCreatorBio()
        {
            if (GetCurrentTrackPersona() != null)
                return null;

            if (_currentTrackIndex < 0 || _playlistInfo?.Tracks == null ||
                _currentTrackIndex >= _playlistInfo.Tracks.Count)
                return null;

            var track = _playlistInfo.Tracks[_currentTrackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
                return metadata.Creator?.Bio;

            return null;
        }

        /// <summary>
        /// Gets the SongMetadata for the currently playing track, or null if unavailable.
        /// </summary>
        protected Models.SongMetadata GetCurrentTrackMetadata()
        {
            if (_currentTrackIndex < 0 || _playlistInfo?.Tracks == null ||
                _currentTrackIndex >= _playlistInfo.Tracks.Count)
                return null;

            var track = _playlistInfo.Tracks[_currentTrackIndex];
            _metadataLookup.TryGetValue(track.Name, out var metadata);
            return metadata;
        }

        /// <summary>
        /// Gets the artist browse URL for the currently playing track.
        /// </summary>
        protected string GetCurrentTrackArtistUrl()
        {
            var name = GetArtistDisplayName(GetCurrentTrackMetadata());
            return $"/artist/{Uri.EscapeDataString(name)}";
        }

        /// <summary>
        /// Gets the SAS URL for the current track's persona image.
        /// </summary>
        protected string GetCurrentTrackPersonaImageUrl()
        {
            var persona = GetCurrentTrackPersona();
            if (persona == null || string.IsNullOrEmpty(persona.ImageBlobPath))
                return null;
            // .persona-image-sm renders at 60 CSS px (40 below 576px).
            return CreatorPersonaService.GetPersonaImageSasUrl(
                persona.ImageBlobPath, persona.ImageVariantWidths, 60, TimeSpan.FromHours(2));
        }

        /// <summary>
        /// Gets the artist display name for a track at the specified index.
        /// </summary>
        protected string GetTrackArtistName(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= _playlistInfo?.Tracks?.Count)
                return ArtistDisplayNames.UnknownArtist;

            var track = _playlistInfo.Tracks[trackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                return GetArtistDisplayName(metadata);
            }

            return ArtistDisplayNames.UnknownArtist;
        }

        /// <summary>
        /// Gets the genre for a track at the given index. Returns "Unknown Genre" if genre is null or whitespace.
        /// </summary>
        protected string GetTrackGenre(int index)
        {
            if (_playlistInfo == null || index >= _playlistInfo.Tracks.Count) return "Unknown Genre";
            
            var track = _playlistInfo.Tracks[index];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata) && !string.IsNullOrWhiteSpace(metadata.Genre))
            {
                return metadata.Genre;
            }
            
            return "Unknown Genre";
        }

        /// <summary>
        /// Gets the URL for the genre playlist page.
        /// </summary>
        protected string GetGenreUrl(string genre)
        {
            return $"/genre/{Uri.EscapeDataString(genre)}";
        }

        /// <summary>
        /// Checks if a specific track should be restricted (60 second preview).
        /// Restricted for non-authenticated users OR authenticated users without an active subscription.
        /// Admins and creators streaming their own songs are never restricted.
        /// </summary>
        protected bool IsTrackRestricted(int trackIndex)
        {
            // If user has an active subscription, they can listen to everything
            if (_hasActiveSubscription)
                return false;

            // Admins can fully stream all songs without a subscription
            if (_isAdmin)
                return false;

            if (_playlistInfo?.Tracks != null && trackIndex >= 0 && trackIndex < _playlistInfo.Tracks.Count)
            {
                var track = _playlistInfo.Tracks[trackIndex];
                if (_metadataLookup.TryGetValue(track.Name, out var metadata) && metadata.DisplayOnHomePage)
                    return false;
            }

            // Creators can fully stream their own songs
            if (_currentUserId.HasValue && _playlistInfo?.Tracks != null && trackIndex >= 0 && trackIndex < _playlistInfo.Tracks.Count)
            {
                var track = _playlistInfo.Tracks[trackIndex];
                if (_metadataLookup.TryGetValue(track.Name, out var metadata) && metadata.Creator?.UserId == _currentUserId.Value)
                    return false;
            }

            // Non-authenticated users or users without subscription are restricted
            return true;
        }

        /// <summary>
        /// Checks if the current track should be restricted (60 second preview).
        /// Restricted for non-authenticated users OR authenticated users without an active subscription.
        /// </summary>
        protected bool IsCurrentTrackRestricted()
        {
            return IsTrackRestricted(_currentTrackIndex);
        }

        protected bool IsProgressBarRestricted()
        {
            // If user has an active subscription, they can listen to everything
            if (_hasActiveSubscription)
                return false;

            // Restrict preview for non-authenticated users OR authenticated users without subscription
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
            return _duration;
        }

        private bool IsImageFile(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".webp";
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

        protected string GetPageTitle()
        {
            if (_loading)
                return $"{GetDisplayTitle()} - StreamTunes";
            var metadata = GetCurrentTrackMetadata();
            if (metadata != null)
            {
                var artist = GetArtistDisplayName(metadata);
                var song = GetTrackTitle(_currentTrackIndex);
                return $"{artist} - {song} - StreamTunes";
            }
            return $"{GetDisplayTitle()} - StreamTunes";
        }

        protected string GetDisplayTitle()
        {
            if (_isRecommendedMode)
            {
                return "Recommended For You";
            }
            return _playlistName ?? _playlistInfo?.PlaylistName ?? "Unknown Playlist";
        }

        protected string GetShareUrl()
        {
            var baseUrl = NavigationManager.BaseUri.TrimEnd('/');
            if (_isRecommendedMode)
            {
                // Recommended playlists are personal and shouldn't be shared publicly
                // Return empty string to disable sharing functionality
                return string.Empty;
            }
            return $"{baseUrl}/playlist/{PlaylistId}";
        }

        /// <summary>
        /// Indicates if the current content can be shared (playlists can be shared, 
        /// but recommended playlists are personal and cannot be shared)
        /// </summary>
        protected bool CanShare => !_isRecommendedMode;

        protected string GetTrackTitle(int index)
        {
            if (_playlistInfo == null || index >= _playlistInfo.Tracks.Count) return "";
            
            // Check for stored SongTitle in metadata
            var track = _playlistInfo.Tracks[index];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                return SongTitleHelper.GetEffectiveTitle(
                    metadata.SongTitle, metadata.Mp3BlobPath, metadata.BlobPath);
            }
            
            // Fall back to extracting from file name
            return SongTitleHelper.GetEffectiveTitle(null, track.Name);
        }

        protected string GetTrackNumber(int index)
        {
            if (_playlistInfo == null || index >= _playlistInfo.Tracks.Count) return (index + 1).ToString();
            
            var track = _playlistInfo.Tracks[index];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata) && metadata.TrackNumber.HasValue)
            {
                return metadata.TrackNumber.Value.ToString();
            }
            
            return (index + 1).ToString(); // Fallback to 1-based index
        }

        /// <summary>
        /// Builds the URL set for one track's cover art, including the pre-resized renditions the
        /// browser can pick from. Falls back to just the full-size blob when none exist yet.
        /// </summary>
        private CoverArtSource BuildTrackArtSource(Models.SongMetadata metadata)
            => CoverArtUrlBuilder.BuildProxy(
                metadata.ImageBlobPath,
                metadata.CoverArtVariantWidths,
                metadata.CoverArtVariantVersion);

        protected CoverArtSource GetTrackArtSource(int index)
        {
            if (!_trackArtSources.TryGetValue(index, out var source) || !source.HasImage)
                return CoverArtSource.None;

            // Check if image dimensions are known and non-square
            if (_playlistInfo != null && index < _playlistInfo.Tracks.Count)
            {
                var track = _playlistInfo.Tracks[index];
                if (_metadataLookup.TryGetValue(track.Name, out var metadata)
                    && metadata.IsImageSquare == false)
                {
                    return CoverArtSource.None; // Non-square image - don't display
                }
            }

            return source;
        }

        protected bool IsCurrentTrackAiGenerated()
        {
            return GetCurrentTrackMetadata()?.IsAiGenerated == true;
        }

        protected bool IsCurrentTrackAiVocals()
        {
            return GetCurrentTrackMetadata()?.IsAiVocals == true;
        }

        protected bool IsCurrentTrackAiLyrics()
        {
            return GetCurrentTrackMetadata()?.IsAiLyrics == true;
        }

        protected double? GetTrackLengthSeconds(int index)
        {
            if (_playlistInfo == null || index >= _playlistInfo.Tracks.Count) return null;
            
            var track = _playlistInfo.Tracks[index];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata) && metadata.TrackLength.HasValue)
            {
                return metadata.TrackLength.Value;
            }
            
            return null;
        }

        protected int GetTrackStreamCount(int index)
        {
            if (_playlistInfo == null || index >= _playlistInfo.Tracks.Count) return 0;
            
            var track = _playlistInfo.Tracks[index];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                if (_trackStreamCounts.TryGetValue(metadata.Id, out var count))
                {
                    return count;
                }
                return metadata.NumberOfStreams;
            }
            
            return 0;
        }

        protected int GetCurrentTrackMetadataId()
        {
            if (_playlistInfo == null || _currentTrackIndex >= _playlistInfo.Tracks.Count) return 0;
            
            var track = _playlistInfo.Tracks[_currentTrackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata))
            {
                return metadata.Id;
            }
            
            return 0;
        }

        protected int GetCurrentTrackStreamQualifyingSeconds()
        {
            if (_playlistInfo == null || _currentTrackIndex >= _playlistInfo.Tracks.Count) return _defaultStreamQualifyingSeconds;
            
            var track = _playlistInfo.Tracks[_currentTrackIndex];
            if (_metadataLookup.TryGetValue(track.Name, out var metadata) && metadata.Creator != null)
            {
                return metadata.Creator.StreamQualifyingSeconds;
            }
            
            return _defaultStreamQualifyingSeconds;
        }

        /// <summary>
        /// Gets a track stream URL by index. Uses pre-fetched URLs if available.
        /// </summary>
        private Task<string> GetTrackStreamUrlAsync(int index)
        {
            // Use pre-fetched URL if available
            if (index >= 0 && _trackStreamUrls.Count > index && !string.IsNullOrWhiteSpace(_trackStreamUrls[index]))
            {
                return Task.FromResult(_trackStreamUrls[index]);
            }

            // Fall back to fetching on-demand
            if (_playlistInfo == null || index < 0 || index >= _playlistInfo.Tracks.Count) return Task.FromResult(string.Empty);
            return Task.FromResult(GetTrackStreamUrl(_playlistInfo.Tracks[index].Name));
        }

        /// <summary>
        /// Gets a direct SAS URL for streaming a track from blob storage.
        /// Calls the storage service directly (Blazor Server runs on the server).
        /// Falls back to the controller streaming endpoint if SAS URL generation fails.
        /// </summary>
        private string GetTrackStreamUrl(string fileName)
        {
            try
            {
                var hasAccess = _hasActiveSubscription;
                var lifetime = hasAccess ? TimeSpan.FromHours(24) : TimeSpan.FromHours(2);
                var uri = AzureStorageService.GetReadSasUri(fileName, lifetime);
                if (uri != null)
                {
                    return uri.ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to get SAS URL for track {FileName}; using fallback", fileName);
            }

            return $"api/music/{SafeEncodePath(fileName)}";
        }

        /// <summary>
        /// Gets SAS URLs for all tracks by calling the storage service directly.
        /// No HTTP round-trips needed since Blazor Server runs on the server.
        /// </summary>
        private List<string> GetTrackStreamUrlsBatch(List<StorageFileInfo> tracks)
        {
            return tracks.Select(t => GetTrackStreamUrl(t.Name)).ToList();
        }

        protected string GetTrackAlbumArtUrl(int index)
        {
            if (_playlistInfo == null || index >= _playlistInfo.Tracks.Count) return "";

            // For now, all tracks share the album cover art
            return _playlistInfo.CoverArtUrl;
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
            if (_playlistInfo == null || index >= _playlistInfo.Tracks.Count) return;

            _currentTrackIndex = index;
            
            // Update shuffle position if shuffle is enabled
            UpdateShufflePosition(index);
            
            _streamUrl = await GetTrackStreamUrlAsync(index);
            _currentTime = 0;
            _duration = 0;

            if (_jsModule != null && !string.IsNullOrWhiteSpace(_streamUrl))
            {
                await _jsModule.InvokeVoidAsync("changeTrack", _audioElement, _streamUrl, IsCurrentTrackRestricted(), GetCurrentTrackMetadataId(), GetCurrentTrackStreamQualifyingSeconds());
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

        [JSInvokable]
        public async Task RecordStream(int songMetadataId)
        {
            Logger.LogInformation("PlaylistPlayer.RecordStream called: songMetadataId={SongMetadataId}, _currentUserId={CurrentUserId}, _isAdmin={IsAdmin}", 
                songMetadataId, _currentUserId, _isAdmin);

            try
            {
                // Call StreamCountService directly (bypasses HTTP which loses auth context in Blazor Server)
                var newCount = await StreamCountService.IncrementStreamCountAsync(songMetadataId, _currentUserId, _isAdmin);
                _trackStreamCounts[songMetadataId] = newCount;
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "PlaylistPlayer: Failed to record stream for song {SongMetadataId}", songMetadataId);
            }
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
            // Show subscribe CTA when a restricted preview ends
            if (IsCurrentTrackRestricted() && _subscribeCtaDialog != null)
            {
                await _subscribeCtaDialog.OnPreviewEndedAsync();
            }

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
                    await _jsModule.InvokeVoidAsync("changeTrack", _audioElement, _streamUrl, IsCurrentTrackRestricted(), GetCurrentTrackMetadataId(), GetCurrentTrackStreamQualifyingSeconds());
                    _isPlaying = true; // Set to true since changeTrack auto-plays the next song
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
            if (_playlistInfo == null || _playlistInfo.Tracks.Count == 0)
                return;

            // Create a list of all track indices except the current one
            var remainingIndices = Enumerable.Range(0, _playlistInfo.Tracks.Count)
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
            if (_playlistInfo == null || _playlistInfo.Tracks.Count == 0)
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
                if (_currentTrackIndex < _playlistInfo.Tracks.Count - 1)
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
            if (_playlistInfo == null || _playlistInfo.Tracks.Count == 0)
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
