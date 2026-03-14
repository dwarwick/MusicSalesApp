using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MusicSalesApp.Services;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Notifications;

namespace MusicSalesApp.Components.Pages;

public class MusicLibraryModel : BlazorBase, IAsyncDisposable
{
    private const double PREVIEW_DURATION_SECONDS = 60.0;

    /// <summary>
    /// When true, only shows items marked for display on home page and hides filter radio buttons.
    /// </summary>
    [Parameter]
    public bool ShowHomePageFeatured { get; set; }

    protected bool _loading = true;
    protected string _error;
    protected List<StorageFileInfo> _files = new List<StorageFileInfo>();
    
    // Track DisplayOnHomePage status for standalone songs
    private HashSet<string> _homePageSongs = new HashSet<string>();

    // Track which card is currently playing
    private string _playingCardId;
    private bool _isActuallyPlaying;
    private string _playingFileName; // Track the file name of the currently playing song
    
    // Card player state for the currently active card
    private double _currentTime;
    private double _duration;
    private double _volume = 0.4;
    private bool _isMuted;
    private double _previousVolume = 0.4;

    // Single set of element references for the active card
    protected ElementReference _activeAudioElement;
    protected ElementReference _activeProgressBarElement;
    protected ElementReference _activeVolumeBarElement;

    // Map file names to song art URLs
    private Dictionary<string, string> _songArtUrls = new Dictionary<string, string>();
    
    // Map file names to song prices
    
    // Map file names to song metadata IDs
    private Dictionary<string, int> _songMetadataIds = new Dictionary<string, int>();

    // Map song metadata IDs to stream counts
    private Dictionary<int, int> _streamCounts = new Dictionary<int, int>();
    
    // Map file names to stored song titles
    private Dictionary<string, string> _songTitles = new Dictionary<string, string>();

    // Map file names to artist info (display name and link URL)
    private Dictionary<string, ArtistDisplayInfo> _artistInfoMap = new Dictionary<string, ArtistDisplayInfo>();

    // Map file names to genre
    private Dictionary<string, string> _genreMap = new Dictionary<string, string>();

    // Genre filter state
    protected HashSet<string> _selectedGenres = new HashSet<string>();

    // Artist filter state
    protected HashSet<string> _selectedArtists = new HashSet<string>();

    private IJSObjectReference _jsModule;
    private DotNetObjectReference<MusicLibraryModel> _dotNetRef;
    private bool _needsJsInit;
    protected bool _tipReturnHandled;
    protected bool IsProcessingTipReturn => !string.IsNullOrEmpty(TipStatus) && !_tipReturnHandled;
    protected bool _isAuthenticated;
    protected bool _hasActiveSubscription;
    private bool _isAdmin;
    private int? _currentUserId;
    private Dictionary<int, int?> _creatorUserIdMap = new Dictionary<int, int?>();
    private Dictionary<string, int?> _creatorIdMap = new Dictionary<string, int?>();
    protected TipDialogModel _tipDialog;
    protected int _tipCreatorId;
    protected int? _tipSongMetadataId;
    protected SfToast _toastRef;

    [SupplyParameterFromQuery(Name = "tip_status")]
    public string TipStatus { get; set; }

    [SupplyParameterFromQuery(Name = "token")]
    public string TipPayPalToken { get; set; }
    private int _defaultStreamQualifyingSeconds = 30;
    private Dictionary<string, int> _streamQualifyingSecondsMap = new Dictionary<string, int>();
    private Action<int, int> _streamCountUpdatedHandler;
    private Action<int, int> _hubStreamCountHandler;
    protected SubscribeCtaDialogModel _subscribeCtaDialog;

    /// <summary>
    /// Represents the artist display information for a song card.
    /// </summary>
    protected class ArtistDisplayInfo
    {
        public string DisplayName { get; set; }
        public string LinkUrl { get; set; }
    }

    protected override async Task OnInitializedAsync()
    {
        // Subscribe to stream count updates (local in-process events)
        _streamCountUpdatedHandler = OnStreamCountUpdated;
        StreamCountService.OnStreamCountUpdated += _streamCountUpdatedHandler;

        // Subscribe to SignalR hub for cross-tab updates
        _hubStreamCountHandler = OnStreamCountUpdated;
        StreamCountHubClient.OnStreamCountReceived += _hubStreamCountHandler;
        await StreamCountHubClient.StartAsync();

        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;
        
        // Check subscription status and load user context if authenticated
        if (_isAuthenticated)
        {
            await LoadSubscriptionStatus();
            _isAdmin = authState.User.IsInRole(Common.Helpers.Roles.Admin);
            var appUser = await UserManager.GetUserAsync(authState.User);
            _currentUserId = appUser?.Id;
            Logger.LogInformation("MusicLibrary: Auth context loaded - _isAuthenticated={IsAuthenticated}, _isAdmin={IsAdmin}, _currentUserId={CurrentUserId}", 
                _isAuthenticated, _isAdmin, _currentUserId);
        }
        
        // Load default stream qualifying seconds for songs without a creator
        _defaultStreamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();

        // Then load files - this will set _loading to false
        await LoadFiles();
    }

    private void OnStreamCountUpdated(int songMetadataId, int newCount)
    {
        // Update local stream count tracking
        if (_streamCounts.ContainsKey(songMetadataId))
        {
            _streamCounts[songMetadataId] = newCount;
            InvokeAsync(StateHasChanged);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Handle return from PayPal tip approval or cancellation (once only)
        if (firstRender && !_tipReturnHandled && !string.IsNullOrEmpty(TipStatus)
            && (!string.IsNullOrEmpty(TipPayPalToken) || TipStatus == "cancelled"))
        {
            _tipReturnHandled = true;
            await HandleTipReturnAsync();
            await InvokeAsync(StateHasChanged);
        }

        if (_needsJsInit && !string.IsNullOrEmpty(_playingCardId))
        {
            _needsJsInit = false;
            
            if (_jsModule == null)
            {
                _dotNetRef = DotNetObjectReference.Create(this);
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/MusicLibrary.razor.js");

                // Load saved volume from localStorage
                var savedVolume = await _jsModule.InvokeAsync<double>("getSavedVolume");
                _volume = savedVolume;
                _previousVolume = savedVolume;
            }

            var isRestricted = IsCurrentPlayingTrackRestricted();
            var songMetadataId = GetCurrentPlayingSongMetadataId();
            var streamQualifyingSeconds = GetCurrentPlayingStreamQualifyingSeconds();
            await _jsModule.InvokeVoidAsync("initCardAudioPlayer", _activeAudioElement, _playingCardId, _dotNetRef, isRestricted, PREVIEW_DURATION_SECONDS, songMetadataId, streamQualifyingSeconds);
            await _jsModule.InvokeVoidAsync("setupCardProgressBarDrag", _activeProgressBarElement, _activeAudioElement, _playingCardId, _dotNetRef);
            await _jsModule.InvokeVoidAsync("setupCardVolumeBarDrag", _activeVolumeBarElement, _activeAudioElement, _playingCardId, _dotNetRef);

            // Set the initial track source
            string initialTrackUrl = null;
            if (!string.IsNullOrEmpty(_playingFileName))
            {
                // Playing a song - get the stream URL
                initialTrackUrl = await GetTrackStreamUrlAsync(_playingFileName);
            }

            if (!string.IsNullOrWhiteSpace(initialTrackUrl))
            {
                await _jsModule.InvokeVoidAsync("setTrackSource", _activeAudioElement, initialTrackUrl);
            }

            // Auto-play when card is initialized
            await _jsModule.InvokeVoidAsync("playCard", _activeAudioElement);
            _isActuallyPlaying = true;
            await InvokeAsync(StateHasChanged);
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
                _jsModule = null;
            }
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Music library JS runtime disconnected during disposal");
        }
        _dotNetRef?.Dispose();
        _dotNetRef = null;
    }

    private async Task LoadFiles()
    {
        _loading = true; _error = null;
        try
        {
            // Load metadata from database - SQL is the source of truth
            var allMetadata = await SongMetadataService.GetAllAsync();
            
            // Build StorageFileInfo list from metadata (no longer calling api/music List endpoint)
            var allFiles = new List<StorageFileInfo>();
            foreach (var meta in allMetadata)
            {
                // Add MP3 file if present
                if (!string.IsNullOrEmpty(meta.Mp3BlobPath))
                {
                    allFiles.Add(new StorageFileInfo
                    {
                        Name = meta.Mp3BlobPath,
                        Length = 0, // Not needed for display
                        ContentType = GetContentTypeFromPath(meta.Mp3BlobPath),
                        LastModified = meta.UpdatedAt,
                        Tags = new Dictionary<string, string>() // No longer using tags
                    });
                }
                
                // Add image file if present (separate from MP3)
                if (!string.IsNullOrEmpty(meta.ImageBlobPath))
                {
                    allFiles.Add(new StorageFileInfo
                    {
                        Name = meta.ImageBlobPath,
                        Length = 0, // Not needed for display
                        ContentType = GetContentTypeFromPath(meta.ImageBlobPath),
                        LastModified = meta.UpdatedAt,
                        Tags = new Dictionary<string, string>() // No longer using tags
                    });
                }
                
                // Add legacy BlobPath if neither Mp3BlobPath nor ImageBlobPath is set
                if (string.IsNullOrEmpty(meta.Mp3BlobPath) && string.IsNullOrEmpty(meta.ImageBlobPath) && !string.IsNullOrEmpty(meta.BlobPath))
                {
                    allFiles.Add(new StorageFileInfo
                    {
                        Name = meta.BlobPath,
                        Length = 0, // Not needed for display
                        ContentType = GetContentTypeFromPath(meta.BlobPath),
                        LastModified = meta.UpdatedAt,
                        Tags = new Dictionary<string, string>() // No longer using tags
                    });
                }
            }
            
            // Create lookup dictionary for file metadata (prefer Mp3BlobPath, fallback to BlobPath)
            var metadataLookup = allMetadata.ToDictionary(
                m => m.Mp3BlobPath ?? m.ImageBlobPath ?? m.BlobPath, 
                m => m);
            
            // Get all audio files
            var audioFiles = allFiles.Where(f => MusicFileExtensions.IsAudioFile(f.Name)).ToList();
            
            // Get all image files
            var imageFiles = allFiles.Where(f => IsImageFile(f.Name)).ToList();

            // All audio files are standalone tracks
            _files = audioFiles.ToList();

            // Pre-compute image file lookup for faster art matching
            var imageFilesLookup = imageFiles
                .Select(f => new
                {
                    File = f,
                    BaseName = Path.GetFileNameWithoutExtension(Path.GetFileName(f.Name)).ToLowerInvariant(),
                    Folder = (Path.GetDirectoryName(f.Name)?.Replace("\\", "/") ?? "").ToLowerInvariant()
                })
                .ToLookup(x => (x.BaseName, x.Folder));

            // Clear home page songs tracking
            _homePageSongs.Clear();

            // Build song art URL mappings and extract metadata for tracks
            foreach (var audioFile in _files)
            {
                var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(audioFile.Name)).ToLowerInvariant();
                var folder = (Path.GetDirectoryName(audioFile.Name)?.Replace("\\", "/") ?? "").ToLowerInvariant();
                
                var artFile = imageFilesLookup[(baseName, folder)].FirstOrDefault()?.File;
                if (artFile != null)
                {
                    // Check metadata for image dimensions - only show square images
                    var artMetadata = allMetadata.FirstOrDefault(m =>
                        !string.IsNullOrEmpty(m.ImageBlobPath) &&
                        m.ImageBlobPath.Equals(artFile.Name, StringComparison.OrdinalIgnoreCase));
                    
                    // Unknown dimensions (null) - show image (graceful degradation)
                    var isSquare = artMetadata?.IsImageSquare ?? true;
                    
                    if (isSquare)
                    {
                        _songArtUrls[audioFile.Name] = $"api/music/{SafeEncodePath(artFile.Name)}";
                    }
                }
                
                // Read song metadata from database
                SongMetadata songMeta = null;
                if (!metadataLookup.TryGetValue(audioFile.Name, out songMeta))
                {
                    // Try finding by Mp3BlobPath or BlobPath
                    songMeta = allMetadata.FirstOrDefault(m => m.Mp3BlobPath == audioFile.Name || m.BlobPath == audioFile.Name);
                }
                if (songMeta != null)
                {
                    // Store the metadata ID
                    _songMetadataIds[audioFile.Name] = songMeta.Id;
                    // Store the stream count
                    _streamCounts[songMeta.Id] = songMeta.NumberOfStreams;
                    // Track if this song should be displayed on the home page
                    if (songMeta.DisplayOnHomePage)
                    {
                        _homePageSongs.Add(audioFile.Name);
                    }
                    // Store the song title if available
                    if (!string.IsNullOrEmpty(songMeta.SongTitle))
                    {
                        _songTitles[audioFile.Name] = songMeta.SongTitle;
                    }
                    // Store artist info
                    _artistInfoMap[audioFile.Name] = GetArtistDisplayInfo(songMeta);
                    // Store genre
                    _genreMap[audioFile.Name] = songMeta.Genre;
                    // Store stream qualifying seconds from creator
                    _streamQualifyingSecondsMap[audioFile.Name] = songMeta.Creator?.StreamQualifyingSeconds ?? _defaultStreamQualifyingSeconds;
                    // Store creator user ID for stream recording guard
                    _creatorUserIdMap[songMeta.Id] = songMeta.Creator?.UserId;
                    // Store creator ID for tip functionality
                    _creatorIdMap[audioFile.Name] = songMeta.CreatorId;
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Logger.LogError(ex, "Error loading files for music library");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Determines the artist display name and link URL based on priority:
    /// 1. SongMetadata.ArtistName - links to /artist/{artistName}
    /// 2. Creator.DisplayName - links to /creator/{creatorId}
    /// 3. Creator.User.Email (part before @) - links to /artist/{emailPrefix}
    /// </summary>
    private ArtistDisplayInfo GetArtistDisplayInfo(SongMetadata songMeta)
    {
        // Priority 1: ArtistName from SongMetadata
        if (!string.IsNullOrWhiteSpace(songMeta.ArtistName))
        {
            // Strip email domain if it contains @ to avoid exposing email addresses
            var displayName = songMeta.ArtistName.Contains('@') ? songMeta.ArtistName.Split('@')[0] : songMeta.ArtistName;
            return new ArtistDisplayInfo
            {
                DisplayName = displayName,
                LinkUrl = $"/artist/{Uri.EscapeDataString(displayName)}"
            };
        }

        // Priority 2: DisplayName from Creator - link to /artist/{displayName} to show all songs with this effective artist name
        if (songMeta.Creator != null && !string.IsNullOrWhiteSpace(songMeta.Creator.DisplayName))
        {
            return new ArtistDisplayInfo
            {
                DisplayName = songMeta.Creator.DisplayName,
                LinkUrl = $"/artist/{Uri.EscapeDataString(songMeta.Creator.DisplayName)}"
            };
        }

        // Priority 3: Email from Creator's User - use part before @ symbol for display and filtering
        if (songMeta.Creator?.User?.Email != null)
        {
            var email = songMeta.Creator.User.Email;
            var emailPrefix = email.Split('@')[0];
            return new ArtistDisplayInfo
            {
                DisplayName = emailPrefix,
                LinkUrl = $"/artist/{Uri.EscapeDataString(emailPrefix)}"
            };
        }

        return new ArtistDisplayInfo { DisplayName = null, LinkUrl = null };
    }

    protected ArtistDisplayInfo GetArtistInfo(string fileName)
    {
        return _artistInfoMap.TryGetValue(fileName, out var info) ? info : new ArtistDisplayInfo();
    }

    /// <summary>
    /// Gets the display genre for a song. Returns "Unknown Genre" if genre is null or whitespace.
    /// </summary>
    protected string GetSongGenre(string fileName)
    {
        if (_genreMap.TryGetValue(fileName, out var genre) && !string.IsNullOrWhiteSpace(genre))
        {
            return genre;
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
    /// Gets genre items with counts for the FilterPill component.
    /// Counts are cross-filtered by the currently selected artists.
    /// </summary>
    protected Dictionary<string, int> GetGenreItems()
    {
        var fileNames = _genreMap.Keys.AsEnumerable();

        // Cross-filter: when artists are selected, only count genres from those artists' songs
        if (_selectedArtists.Count > 0)
        {
            fileNames = fileNames.Where(f =>
            {
                var artistInfo = GetArtistInfo(f);
                return !string.IsNullOrEmpty(artistInfo.DisplayName) &&
                       _selectedArtists.Contains(artistInfo.DisplayName);
            });
        }

        return fileNames
            .Select(f => _genreMap[f])
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => grp.Count(), StringComparer.OrdinalIgnoreCase);
    }

    protected void HandleGenreToggled((string item, bool isChecked) args)
    {
        if (args.isChecked)
        {
            _selectedGenres.Add(args.item);
        }
        else
        {
            _selectedGenres.Remove(args.item);
        }
    }

    protected void HandleGenreCleared()
    {
        _selectedGenres.Clear();
    }

    /// <summary>
    /// Gets artist items with counts for the FilterPill component.
    /// Counts are cross-filtered by the currently selected genres.
    /// </summary>
    protected Dictionary<string, int> GetArtistItems()
    {
        var fileNames = _artistInfoMap.Keys.AsEnumerable();

        // Cross-filter: when genres are selected, only count artists from those genres' songs
        if (_selectedGenres.Count > 0)
        {
            fileNames = fileNames.Where(f => _selectedGenres.Contains(GetSongGenre(f)));
        }

        return fileNames
            .Select(f => _artistInfoMap[f])
            .Where(a => !string.IsNullOrEmpty(a.DisplayName))
            .GroupBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grp => grp.Key, grp => grp.Count(), StringComparer.OrdinalIgnoreCase);
    }

    protected void HandleArtistToggled((string item, bool isChecked) args)
    {
        if (args.isChecked)
        {
            _selectedArtists.Add(args.item);
        }
        else
        {
            _selectedArtists.Remove(args.item);
        }
    }

    protected void HandleArtistCleared()
    {
        _selectedArtists.Clear();
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
            Logger.LogDebug(ex, "Unable to load subscription status; user may not be authenticated");
        }
    }

    protected IEnumerable<StorageFileInfo> GetFilteredFiles()
    {
        var files = _files.AsEnumerable();
        
        // When showing home page featured items, filter by DisplayOnHomePage
        if (ShowHomePageFeatured)
        {
            files = files.Where(f => _homePageSongs.Contains(f.Name));
        }
        
        // Apply genre filter
        if (_selectedGenres.Count > 0)
        {
            files = files.Where(f => _selectedGenres.Contains(GetSongGenre(f.Name)));
        }

        // Apply artist filter
        if (_selectedArtists.Count > 0)
        {
            files = files.Where(f =>
            {
                var artistInfo = GetArtistInfo(f.Name);
                return !string.IsNullOrEmpty(artistInfo.DisplayName) &&
                       _selectedArtists.Contains(artistInfo.DisplayName);
            });
        }
        
        return files;
    }

    protected int GetSongMetadataId(string fileName)
    {
        return _songMetadataIds.TryGetValue(fileName, out var id) ? id : 0;
    }

    protected int GetSongStreamCount(string fileName)
    {
        var metadataId = GetSongMetadataId(fileName);
        if (metadataId > 0 && _streamCounts.TryGetValue(metadataId, out var count))
        {
            return count;
        }
        return 0;
    }

    protected bool CanShowTipButton(string fileName)
    {
        if (!_isAuthenticated) return false;
        if (!_creatorIdMap.TryGetValue(fileName, out var creatorId) || creatorId == null || creatorId <= 0) return false;
        // Don't show tip button for own songs
        var metadataId = GetSongMetadataId(fileName);
        if (metadataId > 0 && _creatorUserIdMap.TryGetValue(metadataId, out var creatorUserId) && creatorUserId == _currentUserId)
            return false;
        return true;
    }

    protected async Task ShowTipForSong(string fileName)
    {
        if (_creatorIdMap.TryGetValue(fileName, out var creatorId) && creatorId.HasValue)
        {
            _tipCreatorId = creatorId.Value;
            _tipSongMetadataId = GetSongMetadataId(fileName);
            if (_tipDialog != null)
            {
                await _tipDialog.ShowAsync();
            }
        }
    }

    private async Task HandleTipReturnAsync()
    {
        // Parse the LAST tip_status/token from the URL. When query params accumulate
        // across multiple PayPal round-trips, [SupplyParameterFromQuery] returns the
        // first value, which may be stale. The last pair is the most recent return.
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
        // Blazor's navigation lifecycle (avoids re-running OnAfterRenderAsync / data reload)
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

    private class StreamUrlResponseDto
    {
        public string Url { get; set; }
    }

    private bool IsImageFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".webp";
    }

    private static string GetContentTypeFromPath(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".wma" => "audio/x-ms-wma",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
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
        // Check for stored SongTitle in metadata
        if (_songMetadataIds.TryGetValue(fileName, out var metadataId) && metadataId > 0)
        {
            var storedTitle = GetStoredSongTitle(fileName);
            if (!string.IsNullOrEmpty(storedTitle))
            {
                return storedTitle;
            }
        }
        
        // Fall back to extracting from file name
        return Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
    }
    
    protected string GetStoredSongTitle(string fileName)
    {
        return _songTitles.TryGetValue(fileName, out var title) ? title : null;
    }

    protected string GetStreamUrl(string fileName)
    {
        return $"api/music/{SafeEncodePath(fileName)}";
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
            Logger.LogWarning(ex, "Failed to get SAS URL for {FileName}; using fallback", fileName);
        }

        // Fallback: stream through the MusicController
        return $"api/music/{safePath}";
    }

    protected string GetAlbumArtUrl(string fileName)
    {
        return _songArtUrls.TryGetValue(fileName, out var url) ? url : null;
    }

    protected void GetSongPlayerUrl(string fileName)
    {
        var songTitle = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));

        NavigationManager.NavigateTo($"/song/{Uri.EscapeDataString(songTitle)}");        
    }

    protected bool IsCardPlaying(string cardId)
    {
        return _playingCardId == cardId;
    }

    protected bool IsCardCurrentlyPlaying(string cardId)
    {
        return _playingCardId == cardId && _isActuallyPlaying;
    }

    /// <summary>
    /// Checks if the currently playing track is restricted (60 second preview).
    /// Restricted for non-authenticated users OR authenticated users without an active subscription.
    /// Admins and creators streaming their own songs are never restricted.
    /// </summary>
    protected bool IsCurrentPlayingTrackRestricted()
    {
        // If user has an active subscription, they can listen to everything
        if (_hasActiveSubscription)
            return false;

        // Admins can fully stream all songs without a subscription
        if (_isAdmin)
            return false;

        // Creators can fully stream their own songs
        if (_currentUserId.HasValue && !string.IsNullOrEmpty(_playingFileName))
        {
            var metadataId = GetSongMetadataId(_playingFileName);
            if (metadataId > 0 && _creatorUserIdMap.TryGetValue(metadataId, out var creatorUserId) && creatorUserId == _currentUserId.Value)
                return false;
        }

        // Non-authenticated users are always restricted
        if (!_isAuthenticated)
            return true;

        // Users without subscription are restricted to preview
        return true;
    }

    /// <summary>
    /// Gets the metadata ID for the currently playing track.
    /// </summary>
    protected int GetCurrentPlayingSongMetadataId()
    {
        // For individual songs, get the file's metadata ID
        if (!string.IsNullOrEmpty(_playingFileName))
        {
            return GetSongMetadataId(_playingFileName);
        }

        return 0;
    }

    /// <summary>
    /// Gets the stream qualifying seconds for the currently playing track's creator.
    /// </summary>
    protected int GetCurrentPlayingStreamQualifyingSeconds()
    {
        if (!string.IsNullOrEmpty(_playingFileName) && _streamQualifyingSecondsMap.TryGetValue(_playingFileName, out var seconds))
        {
            return seconds;
        }

        return _defaultStreamQualifyingSeconds;
    }

    protected async Task PlayCard(string fileName)
    {
        var cardId = GetCardId(fileName);
        _playingCardId = cardId;
        _playingFileName = fileName;
        _isActuallyPlaying = false;
        _needsJsInit = true;

        // Reset playback state for new card, but preserve volume
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
        
        if (IsCurrentPlayingTrackRestricted() && _duration > 0)
        {
            var maxTime = Math.Min(_duration, PREVIEW_DURATION_SECONDS);
            return (_currentTime / maxTime) * GetCardPreviewLimitPercentage(cardId);
        }
        
        return _duration > 0 ? (_currentTime / _duration * 100) : 0;
    }

    protected double GetCardPreviewLimitPercentage(string cardId)
    {
        if (_playingCardId != cardId || _duration <= 0) return 100;
        return Math.Min(100, (PREVIEW_DURATION_SECONDS / _duration) * 100);
    }

    protected double GetCardDisplayDuration(string cardId)
    {
        if (_playingCardId != cardId) return 0;
        return _duration;
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
                await _jsModule.InvokeVoidAsync("seekCardToPosition", _activeAudioElement, e.OffsetX, width, cardId);
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
    public async Task CardAudioEnded(string cardId)
    {
        if (_playingCardId == cardId)
        {
            _isActuallyPlaying = false;

            // Show subscribe CTA when a restricted preview ends
            if (IsCurrentPlayingTrackRestricted() && _subscribeCtaDialog != null)
            {
                await _subscribeCtaDialog.OnPreviewEndedAsync();
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    [JSInvokable]
    public async Task RecordStream(int songMetadataId)
    {
        Logger.LogInformation("MusicLibrary.RecordStream called: songMetadataId={SongMetadataId}, _currentUserId={CurrentUserId}, _isAdmin={IsAdmin}", 
            songMetadataId, _currentUserId, _isAdmin);

        try
        {
            // Call StreamCountService directly (bypasses HTTP which loses auth context in Blazor Server)
            var newCount = await StreamCountService.IncrementStreamCountAsync(songMetadataId, _currentUserId, _isAdmin);
            _streamCounts[songMetadataId] = newCount;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MusicLibrary: Failed to record stream for song {SongMetadataId}", songMetadataId);
        }
    }

    protected string GetSongShareUrl(string fileName)
    {
        var songTitle = GetDisplayTitle(fileName);
        var baseUrl = NavigationManager.BaseUri.TrimEnd('/');
        return $"{baseUrl}/song/{Uri.EscapeDataString(songTitle)}";
    }
}
