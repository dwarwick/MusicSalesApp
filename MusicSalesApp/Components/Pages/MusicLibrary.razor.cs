using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MusicSalesApp.Services;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages;

public enum FilterMode
{
    All,
    Owned,
    NotOwned
}

/// <summary>
/// Represents an album with its cover art and tracks.
/// </summary>
public class AlbumInfo
{
    public string AlbumName { get; set; }
    public string CoverArtUrl { get; set; }
    public string CoverArtFileName { get; set; }
    public List<StorageFileInfo> Tracks { get; set; } = new List<StorageFileInfo>();
    public decimal Price { get; set; } = PriceDefaults.DefaultAlbumPrice;
    public int MetadataId { get; set; } // ID of the album cover's SongMetadata record
}

public class MusicLibraryModel : BlazorBase, IAsyncDisposable
{
    private const double PREVIEW_DURATION_SECONDS = 60.0;

    protected bool _loading = true;
    protected string _error;
    protected List<StorageFileInfo> _files = new List<StorageFileInfo>();
    protected List<AlbumInfo> _albums = new List<AlbumInfo>();
    protected FilterMode _filterMode = FilterMode.All;
    
    // String property for Syncfusion radio button binding
    protected string _filterModeString
    {
        get => _filterMode.ToString();
        set => _filterMode = Enum.Parse<FilterMode>(value);
    }
    
    protected HashSet<string> _ownedSongs = new HashSet<string>();
    protected HashSet<string> _cartSongs = new HashSet<string>();
    protected HashSet<string> _cartAlbums = new HashSet<string>();
    protected HashSet<string> _animatingCartButtons = new HashSet<string>();

    // Track which card is currently playing
    private string _playingCardId;
    private bool _isActuallyPlaying;
    private string _playingFileName; // Track the file name of the currently playing song
    
    // Card player state for the currently active card
    private double _currentTime;
    private double _duration;
    private double _volume = 1.0;
    private bool _isMuted;
    private double _previousVolume = 1.0;

    // Album playback state
    private AlbumInfo _playingAlbum;
    private int _currentTrackIndex;
    private List<string> _albumTrackUrls = new List<string>();

    // Single set of element references for the active card
    protected ElementReference _activeAudioElement;
    protected ElementReference _activeProgressBarElement;
    protected ElementReference _activeVolumeBarElement;

    // Map file names to album art URLs
    private Dictionary<string, string> _albumArtUrls = new Dictionary<string, string>();
    
    // Map file names to song prices
    private Dictionary<string, decimal> _songPrices = new Dictionary<string, decimal>();
    
    // Map file names to song metadata IDs for cart operations
    private Dictionary<string, int> _songMetadataIds = new Dictionary<string, int>();

    private IJSObjectReference _jsModule;
    private DotNetObjectReference<MusicLibraryModel> _dotNetRef;
    private bool _needsJsInit;
    private bool _isAuthenticated;
    protected bool _hasActiveSubscription;

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;
        
        // Load cart and owned songs first if authenticated
        if (_isAuthenticated)
        {
            await LoadCartAndOwnedSongs();
        }
        
        // Then load files - this will set _loading to false
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

            var isRestricted = IsCurrentPlayingTrackRestricted();
            await _jsModule.InvokeVoidAsync("initCardAudioPlayer", _activeAudioElement, _playingCardId, _dotNetRef, isRestricted, PREVIEW_DURATION_SECONDS);
            await _jsModule.InvokeVoidAsync("setupCardProgressBarDrag", _activeProgressBarElement, _activeAudioElement, _playingCardId, _dotNetRef);
            await _jsModule.InvokeVoidAsync("setupCardVolumeBarDrag", _activeVolumeBarElement, _activeAudioElement, _playingCardId, _dotNetRef);

            // Set the initial track source
            string initialTrackUrl = null;
            if (_playingAlbum != null && _albumTrackUrls.Count > 0)
            {
                // Playing an album - get the current track URL
                initialTrackUrl = GetCurrentAlbumTrackUrl();
            }
            else if (!string.IsNullOrEmpty(_playingFileName))
            {
                // Playing an individual song - get the stream URL
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
            var audioFiles = allFiles.Where(f => IsAudioFile(f.Name)).ToList();
            
            // Get all image files
            var imageFiles = allFiles.Where(f => IsImageFile(f.Name)).ToList();

            // Find album covers from database metadata
            var albumCoverMetadata = allMetadata.Where(m => m.IsAlbumCover).ToList();

            // Build albums from album covers
            _albums.Clear();
            var tracksInAlbums = new HashSet<string>();
            
            foreach (var coverMeta in albumCoverMetadata)
            {
                if (!string.IsNullOrWhiteSpace(coverMeta.AlbumName))
                {
                    // Find all tracks with the same album name from metadata
                    var trackMetadata = allMetadata
                        .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath) &&
                                    !string.IsNullOrEmpty(m.AlbumName) &&
                                    string.Equals(m.AlbumName, coverMeta.AlbumName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Map metadata to StorageFileInfo
                    var albumTracks = new List<StorageFileInfo>();
                    foreach (var trackMeta in trackMetadata)
                    {
                        var mp3Path = trackMeta.Mp3BlobPath ?? trackMeta.BlobPath;
                        var fileInfo = allFiles.FirstOrDefault(f => f.Name == mp3Path);
                        if (fileInfo != null)
                        {
                            albumTracks.Add(fileInfo);
                            // Store the metadata ID for cart operations
                            _songMetadataIds[fileInfo.Name] = trackMeta.Id;
                        }
                    }

                    if (albumTracks.Any())
                    {
                        // Get album price from database metadata
                        decimal albumPrice = coverMeta.AlbumPrice ?? PriceDefaults.DefaultAlbumPrice;

                        var imagePath = coverMeta.ImageBlobPath ?? coverMeta.BlobPath;
                        var album = new AlbumInfo
                        {
                            AlbumName = coverMeta.AlbumName,
                            CoverArtUrl = $"api/music/{SafeEncodePath(imagePath)}",
                            CoverArtFileName = imagePath,
                            Tracks = albumTracks.OrderBy(f => Path.GetFileName(f.Name)).ToList(),
                            Price = albumPrice,
                            MetadataId = coverMeta.Id
                        };
                        _albums.Add(album);

                        // Mark these tracks as being part of an album
                        foreach (var track in albumTracks)
                        {
                            tracksInAlbums.Add(track.Name);
                        }
                    }
                }
            }

            // Filter audio files to only include standalone tracks (not part of any album)
            _files = audioFiles.Where(f => !tracksInAlbums.Contains(f.Name)).ToList();

            // Pre-compute image file lookup for faster album art matching (for standalone tracks)
            var imageFilesLookup = imageFiles
                .Select(f => new
                {
                    File = f,
                    BaseName = Path.GetFileNameWithoutExtension(Path.GetFileName(f.Name)).ToLowerInvariant(),
                    Folder = (Path.GetDirectoryName(f.Name)?.Replace("\\", "/") ?? "").ToLowerInvariant()
                })
                .ToLookup(x => (x.BaseName, x.Folder));

            // Build album art URL mappings and extract song prices for standalone tracks
            foreach (var audioFile in _files)
            {
                var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(audioFile.Name)).ToLowerInvariant();
                var folder = (Path.GetDirectoryName(audioFile.Name)?.Replace("\\", "/") ?? "").ToLowerInvariant();
                
                var artFile = imageFilesLookup[(baseName, folder)].FirstOrDefault()?.File;
                if (artFile != null)
                {
                    _albumArtUrls[audioFile.Name] = $"api/music/{SafeEncodePath(artFile.Name)}";
                }
                
                // Read song price from database metadata (try Mp3BlobPath first, then BlobPath)
                decimal songPrice = PriceDefaults.DefaultSongPrice;
                SongMetadata songMeta = null;
                if (!metadataLookup.TryGetValue(audioFile.Name, out songMeta))
                {
                    // Try finding by Mp3BlobPath or BlobPath
                    songMeta = allMetadata.FirstOrDefault(m => m.Mp3BlobPath == audioFile.Name || m.BlobPath == audioFile.Name);
                }
                if (songMeta != null)
                {
                    if (songMeta.SongPrice.HasValue)
                    {
                        songPrice = songMeta.SongPrice.Value;
                    }
                    // Store the metadata ID for cart operations
                    _songMetadataIds[audioFile.Name] = songMeta.Id;
                }
                _songPrices[audioFile.Name] = songPrice;
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

    private async Task LoadCartAndOwnedSongs()
    {
        try
        {
            // Check subscription status
            var subscriptionResponse = await Http.GetFromJsonAsync<SubscriptionStatusDto>("api/subscription/status");
            _hasActiveSubscription = subscriptionResponse?.HasSubscription ?? false;

            // Load owned songs
            var ownedResponse = await Http.GetFromJsonAsync<IEnumerable<string>>("api/cart/owned");
            _ownedSongs = new HashSet<string>(ownedResponse ?? Enumerable.Empty<string>());

            // Load cart items
            var cartResponse = await Http.GetFromJsonAsync<CartResponseDto>("api/cart");
            if (cartResponse?.Items != null)
            {
                _cartSongs = new HashSet<string>(cartResponse.Items.Select(i => i.SongFileName));
            }
            if (cartResponse?.Albums != null)
            {
                _cartAlbums = new HashSet<string>(cartResponse.Albums.Select(a => a.AlbumName));
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogDebug(ex, "Unable to load cart or owned songs; user may not be authenticated");
        }
    }

    protected IEnumerable<StorageFileInfo> GetFilteredFiles()
    {
        return _filterMode switch
        {
            FilterMode.Owned => _files.Where(f => _ownedSongs.Contains(f.Name)),
            FilterMode.NotOwned => _files.Where(f => !_ownedSongs.Contains(f.Name)),
            _ => _files
        };
    }

    protected void SetFilter(FilterMode mode)
    {
        _filterMode = mode;
    }

    protected bool IsSongOwned(string fileName)
    {
        return _ownedSongs.Contains(fileName);
    }

    protected bool IsSongInCart(string fileName)
    {
        return _cartSongs.Contains(fileName);
    }

    protected decimal GetSongPrice(string fileName)
    {
        return _songPrices.TryGetValue(fileName, out var price) ? price : PriceDefaults.DefaultSongPrice;
    }

    protected int GetSongMetadataId(string fileName)
    {
        return _songMetadataIds.TryGetValue(fileName, out var id) ? id : 0;
    }

    protected async Task ToggleCartItem(string fileName)
    {
        try
        {
            var price = GetSongPrice(fileName);
            int? songMetadataId = _songMetadataIds.TryGetValue(fileName, out var id) ? id : (int?)null;
            var response = await Http.PostAsJsonAsync("api/cart/toggle", new { SongFileName = fileName, Price = price, SongMetadataId = songMetadataId });
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CartToggleResponse>();
                if (result != null)
                {
                    if (result.InCart)
                    {
                        _cartSongs.Add(fileName);
                        // Trigger animation
                        _animatingCartButtons.Add(fileName);
                        await InvokeAsync(StateHasChanged);
                        
                        // Remove animation class after animation completes
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(800);
                            _animatingCartButtons.Remove(fileName);
                            await InvokeAsync(StateHasChanged);
                        });
                    }
                    else
                    {
                        _cartSongs.Remove(fileName);
                    }
                    
                    // Notify the NavMenu to update the cart count
                    CartService.NotifyCartUpdated();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error toggling cart item {FileName}", fileName);
        }
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

    private class CartToggleResponse
    {
        public bool InCart { get; set; }
        public int Count { get; set; }
    }

    private class StreamUrlResponseDto
    {
        public string Url { get; set; }
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
        return Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
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
        return _albumArtUrls.TryGetValue(fileName, out var url) ? url : null;
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
    /// Restricted for non-authenticated users OR authenticated users who don't own the track.
    /// </summary>
    protected bool IsCurrentPlayingTrackRestricted()
    {
        // If user has an active subscription, they can listen to everything
        if (_hasActiveSubscription)
            return false;

        // Non-authenticated users are always restricted
        if (!_isAuthenticated)
            return true;

        // For albums, check if the current track is owned
        if (_playingAlbum != null && _currentTrackIndex < _playingAlbum.Tracks.Count)
        {
            return !_ownedSongs.Contains(_playingAlbum.Tracks[_currentTrackIndex].Name);
        }

        // For individual songs, check if the file is owned
        if (!string.IsNullOrEmpty(_playingFileName))
        {
            return !_ownedSongs.Contains(_playingFileName);
        }

        return true; // Default to restricted if we can't determine
    }

    protected async Task PlayCard(string fileName)
    {
        var cardId = GetCardId(fileName);
        _playingCardId = cardId;
        _playingFileName = fileName;
        _isActuallyPlaying = false;
        _needsJsInit = true;
        _playingAlbum = null; // Clear album state since this is an individual song

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
        if (IsCurrentPlayingTrackRestricted())
        {
            return Math.Min(_duration, PREVIEW_DURATION_SECONDS);
        }
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
    public void CardAudioEnded(string cardId)
    {
        if (_playingCardId == cardId)
        {
            // Check if this is an album playing and we need to play the next track
            if (_playingAlbum != null && _currentTrackIndex < _albumTrackUrls.Count - 1)
            {
                _currentTrackIndex++;
                // Will need to trigger next track play via JS
                InvokeAsync(async () =>
                {
                    await PlayNextAlbumTrack();
                });
                return;
            }
            
            _isActuallyPlaying = false;
            _playingAlbum = null;
            _currentTrackIndex = 0;
            InvokeAsync(StateHasChanged);
        }
    }

    // Album-specific methods
    protected IEnumerable<AlbumInfo> GetFilteredAlbums()
    {
        return _filterMode switch
        {
            FilterMode.Owned => _albums.Where(a => IsAlbumOwned(a)),
            FilterMode.NotOwned => _albums.Where(a => !IsAlbumOwned(a)),
            _ => _albums
        };
    }

    protected string GetAlbumCardId(AlbumInfo album)
    {
        // Create a stable card ID from the album name prefixed with "album:"
        return "album_" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(album.AlbumName)).Replace("+", "-").Replace("/", "_");
    }

    protected bool IsAlbumOwned(AlbumInfo album)
    {
        // An album is owned if all of its tracks are owned
        return album.Tracks.All(t => _ownedSongs.Contains(t.Name));
    }

    protected bool IsAlbumInCart(AlbumInfo album)
    {
        // An album is in cart if all of its tracks are in cart
        return album.Tracks.All(t => _cartSongs.Contains(t.Name));
    }

    protected async Task PlayAlbum(AlbumInfo album)
    {
        var cardId = GetAlbumCardId(album);
        _playingCardId = cardId;
        _isActuallyPlaying = false;
        _needsJsInit = true;
        _playingAlbum = album;
        _playingFileName = null; // Clear individual song state since this is an album
        _currentTrackIndex = 0;

        // Build list of track URLs for the album using SAS URLs for direct blob streaming
        // Use Task.WhenAll for parallel fetching to improve performance with many tracks
        var trackUrlTasks = album.Tracks.Select(t => GetTrackStreamUrlAsync(t.Name));
        _albumTrackUrls = (await Task.WhenAll(trackUrlTasks)).ToList();

        // Reset state for new card
        _volume = 1.0;
        _previousVolume = 1.0;
        _isMuted = false;
        _currentTime = 0;
        _duration = 0;

        await InvokeAsync(StateHasChanged);
    }

    protected string GetCurrentAlbumTrackUrl()
    {
        if (_playingAlbum == null || _currentTrackIndex >= _albumTrackUrls.Count)
            return string.Empty;
        return _albumTrackUrls[_currentTrackIndex];
    }

    protected string GetCurrentAlbumTrackName()
    {
        if (_playingAlbum == null || _currentTrackIndex >= _playingAlbum.Tracks.Count)
            return string.Empty;
        return GetDisplayTitle(_playingAlbum.Tracks[_currentTrackIndex].Name);
    }

    private async Task PlayNextAlbumTrack()
    {
        if (_jsModule != null && _playingAlbum != null && _currentTrackIndex < _albumTrackUrls.Count)
        {
            _currentTime = 0;
            _duration = 0;
            var isRestricted = IsCurrentPlayingTrackRestricted();
            await _jsModule.InvokeVoidAsync("changeTrack", _activeAudioElement, _albumTrackUrls[_currentTrackIndex], _playingCardId, isRestricted);
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void GetAlbumPlayerUrl(AlbumInfo album)
    {
        NavigationManager.NavigateTo($"/album/{Uri.EscapeDataString(album.AlbumName)}");
    }

    protected async Task ToggleAlbumCartItem(AlbumInfo album)
    {
        try
        {
            // Get all track file names in the album
            var trackFileNames = album.Tracks.Select(t => t.Name).ToList();
            
            // Build dictionary of track filenames to metadata IDs
            var trackMetadataIds = new Dictionary<string, int>();
            foreach (var track in album.Tracks)
            {
                if (_songMetadataIds.TryGetValue(track.Name, out var id))
                {
                    trackMetadataIds[track.Name] = id;
                }
            }
            
            var response = await Http.PostAsJsonAsync("api/cart/toggle-album", new 
            { 
                AlbumName = album.AlbumName,
                TrackFileNames = trackFileNames,
                Price = album.Price,
                TrackMetadataIds = trackMetadataIds
            });
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CartToggleResponse>();
                if (result != null)
                {
                    if (result.InCart)
                    {
                        _cartAlbums.Add(album.AlbumName);
                        // Also add individual tracks to cart tracking
                        foreach (var track in album.Tracks)
                        {
                            _cartSongs.Add(track.Name);
                        }
                        // Trigger animation
                        _animatingCartButtons.Add(album.AlbumName);
                        await InvokeAsync(StateHasChanged);
                        
                        // Remove animation class after animation completes
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(800);
                            _animatingCartButtons.Remove(album.AlbumName);
                            await InvokeAsync(StateHasChanged);
                        });
                    }
                    else
                    {
                        _cartAlbums.Remove(album.AlbumName);
                        // Also remove individual tracks from cart tracking
                        foreach (var track in album.Tracks)
                        {
                            _cartSongs.Remove(track.Name);
                        }
                    }
                    
                    // Notify the NavMenu to update the cart count
                    CartService.NotifyCartUpdated();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error toggling album cart for {AlbumName}", album.AlbumName);
        }
    }
}
