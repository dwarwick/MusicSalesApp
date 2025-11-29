using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MusicSalesApp.Services;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Common.Helpers;

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
    public const decimal DEFAULT_ALBUM_PRICE = 9.99m;
    
    public string AlbumName { get; set; }
    public string CoverArtUrl { get; set; }
    public string CoverArtFileName { get; set; }
    public List<StorageFileInfo> Tracks { get; set; } = new List<StorageFileInfo>();
    public decimal Price { get; set; } = DEFAULT_ALBUM_PRICE;
}

public class MusicLibraryModel : BlazorBase, IAsyncDisposable
{
    protected bool _loading = true;
    protected string _error;
    protected List<StorageFileInfo> _files = new List<StorageFileInfo>();
    protected List<AlbumInfo> _albums = new List<AlbumInfo>();
    protected FilterMode _filterMode = FilterMode.All;
    protected HashSet<string> _ownedSongs = new HashSet<string>();
    protected HashSet<string> _cartSongs = new HashSet<string>();
    protected HashSet<string> _cartAlbums = new HashSet<string>();
    protected HashSet<string> _animatingCartButtons = new HashSet<string>();

    // Track which card is currently playing
    private string _playingCardId;
    private bool _isActuallyPlaying;
    
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

    private IJSObjectReference _jsModule;
    private DotNetObjectReference<MusicLibraryModel> _dotNetRef;
    private bool _needsJsInit;
    private bool _isAuthenticated;

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;
        
        await LoadFiles();
        
        if (_isAuthenticated)
        {
            await LoadCartAndOwnedSongs();
        }
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
                _jsModule = null;
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit is already disconnected, safe to ignore
        }
        _dotNetRef?.Dispose();
        _dotNetRef = null;
    }

    private async Task LoadFiles()
    {
        _loading = true; _error = null;
        try
        {
            var result = await Http.GetFromJsonAsync<IEnumerable<StorageFileInfo>>("api/music");
            var allFiles = result?.ToList() ?? new List<StorageFileInfo>();
            
            // Get all audio files
            var audioFiles = allFiles.Where(f => IsAudioFile(f.Name)).ToList();
            
            // Get all image files
            var imageFiles = allFiles.Where(f => IsImageFile(f.Name)).ToList();

            // Find album covers (images with IsAlbumCover=true metadata)
            var albumCovers = imageFiles
                .Where(f => f.Metadata != null && 
                            f.Metadata.TryGetValue(MetadataNames.IsAlbumCover, out var isAlbumCover) && 
                            string.Equals(isAlbumCover, "true", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Build albums from album covers
            _albums.Clear();
            var tracksInAlbums = new HashSet<string>();
            
            foreach (var cover in albumCovers)
            {
                if (cover.Metadata.TryGetValue(MetadataNames.AlbumName, out var albumName) && !string.IsNullOrWhiteSpace(albumName))
                {
                    // Find all tracks with the same album name
                    var albumTracks = audioFiles
                        .Where(f => f.Metadata != null && 
                                    f.Metadata.TryGetValue(MetadataNames.AlbumName, out var trackAlbum) && 
                                    string.Equals(trackAlbum, albumName, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => Path.GetFileName(f.Name))
                        .ToList();

                    if (albumTracks.Any())
                    {
                        var album = new AlbumInfo
                        {
                            AlbumName = albumName,
                            CoverArtUrl = $"api/music/{SafeEncodePath(cover.Name)}",
                            CoverArtFileName = cover.Name,
                            Tracks = albumTracks,
                            Price = AlbumInfo.DEFAULT_ALBUM_PRICE
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

            // Build album art URL map using pre-computed lookup for standalone tracks
            foreach (var audioFile in _files)
            {
                var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(audioFile.Name)).ToLowerInvariant();
                var folder = (Path.GetDirectoryName(audioFile.Name)?.Replace("\\", "/") ?? "").ToLowerInvariant();
                
                var artFile = imageFilesLookup[(baseName, folder)].FirstOrDefault()?.File;
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

    private async Task LoadCartAndOwnedSongs()
    {
        try
        {
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
        catch (HttpRequestException)
        {
            // User not authenticated or other error, ignore
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

    protected async Task ToggleCartItem(string fileName)
    {
        try
        {
            var response = await Http.PostAsJsonAsync("api/cart/toggle", new { SongFileName = fileName, Price = 0.99m });
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
                    NavMenuModel.NotifyCartUpdated();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error toggling cart: {ex.Message}");
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
        return _cartAlbums.Contains(album.AlbumName);
    }

    protected async Task PlayAlbum(AlbumInfo album)
    {
        var cardId = GetAlbumCardId(album);
        _playingCardId = cardId;
        _isActuallyPlaying = false;
        _needsJsInit = true;
        _playingAlbum = album;
        _currentTrackIndex = 0;

        // Build list of track URLs for the album
        _albumTrackUrls = album.Tracks.Select(t => GetStreamUrl(t.Name)).ToList();

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
            await _jsModule.InvokeVoidAsync("changeTrack", _activeAudioElement, _albumTrackUrls[_currentTrackIndex]);
            await InvokeAsync(StateHasChanged);
        }
    }

    protected string GetAlbumPlayerUrl(AlbumInfo album)
    {
        return $"/album/{Uri.EscapeDataString(album.AlbumName)}";
    }

    protected async Task ToggleAlbumCartItem(AlbumInfo album)
    {
        try
        {
            // Get all track file names in the album
            var trackFileNames = album.Tracks.Select(t => t.Name).ToList();
            
            var response = await Http.PostAsJsonAsync("api/cart/toggle-album", new 
            { 
                AlbumName = album.AlbumName,
                TrackFileNames = trackFileNames,
                Price = album.Price
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
                    NavMenuModel.NotifyCartUpdated();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error toggling album cart: {ex.Message}");
        }
    }
}
