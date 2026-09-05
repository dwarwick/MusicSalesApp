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
using MusicSalesApp.Helpers;

namespace MusicSalesApp.Components.Pages.Public;

public class MusicLibraryModel : BlazorBase, IAsyncDisposable
{
    private const double PREVIEW_DURATION_SECONDS = 60.0;
    protected const string AiFilterAll = "All";
    protected const string AiFilterAny = "AnyAI";
    protected const string AiFilterAiMusic = "AIMusic";
    protected const string AiFilterAiVocals = "AIVocals";
    protected const string AiFilterAiLyrics = "AILyrics";
    protected const string AiFilterNonAi = "Non-AI";

    /// <summary>
    /// When true, only shows items marked for display on home page and hides filter radio buttons.
    /// </summary>
    [Parameter]
    public bool ShowHomePageFeatured { get; set; }

    [Parameter]
    public string EmbeddedPageTitle { get; set; }

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

    /// <summary>
    /// Which songs have lyrics a listener may see, keyed by song id.
    ///
    /// <para>
    /// One bulk query for the whole grid rather than a lookup per card, the same reason
    /// <c>GetForSongsAsync</c> exists for the creator's list. Only the ROW is loaded here - deciding
    /// whether to offer the toggle costs nothing, while reading the timings themselves is a blob
    /// fetch and would be a hundred of them on a full page.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<int, SongLyrics> _lyricsBySongId = new Dictionary<int, SongLyrics>();

    /// <summary>The timings for the card now playing, read when it starts rather than up front.</summary>
    protected MusicSalesApp.Common.Contracts.LyricsTimingsDocument _playingCardTimings;

    /// <summary>Whether the playing card is showing its lyrics in place of its art.</summary>
    protected bool _showLyricsOnCard;

    // Map file names to song art URL sets (full-size plus the pre-resized renditions)
    private Dictionary<string, CoverArtSource> _songArtSources = new Dictionary<string, CoverArtSource>();
    
    // Map file names to song prices
    
    // Map file names to song metadata IDs
    private Dictionary<string, int> _songMetadataIds = new Dictionary<string, int>();

    // File name to the song's true length in seconds, from SongMetadata rather than from the media
    // element - see where it is populated.
    private Dictionary<string, double> _songTrackLengths = new Dictionary<string, double>();

    // Map song metadata IDs to stream counts
    private Dictionary<int, int> _streamCounts = new Dictionary<int, int>();

    // Map file names to library/home display order
    private Dictionary<string, int?> _songDisplayOrders = new Dictionary<string, int?>();
    
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

    // AI filter state
    protected string _selectedAiFilter = AiFilterAll;

    // Title filter state. A free-text, case-insensitive substring match, so unlike genre and
    // artist there is no option list and no selection set - what is typed is the filter.
    protected string _titleFilter = string.Empty;

    /// <summary>
    /// Which pill's panel is open, if any. One field rather than a flag per pill: the panel now
    /// renders below the pill row instead of anchored to its pill, so only one fits at a time.
    /// </summary>
    protected enum FilterPanel { None, Ai, Genre, Artist, Title }

    protected FilterPanel _openPanel = FilterPanel.None;

    // Track AI content disclosure status by file name
    private Dictionary<string, bool> _aiGeneratedMap = new Dictionary<string, bool>();
    private Dictionary<string, bool> _aiVocalsMap = new Dictionary<string, bool>();
    private Dictionary<string, bool> _aiLyricsMap = new Dictionary<string, bool>();

    private IJSObjectReference _jsModule;
    private DotNetObjectReference<MusicLibraryModel> _dotNetRef;
    private bool _needsJsInit;
    protected bool _tipReturnHandled;
    protected bool IsProcessingTipReturn => !string.IsNullOrEmpty(TipStatus) && !_tipReturnHandled;
    protected bool _isAuthenticated;
    protected bool _hasActiveSubscription;
    private bool _isAdmin;
    private int? _currentUserId;

    /// <summary>
    /// Which songs the signed-in user has streamed - what entitles them to rate. Resolved once for the
    /// whole page and fed to every card's LikeDislikeButtons, instead of each of several hundred cards
    /// asking the database for itself. Empty when signed out, and on a load failure - failing closed
    /// only dims the thumbs; the server enforces the real rule either way.
    /// </summary>
    private HashSet<int> _streamedSongIds = new();

    // Resolved once per load, for the same reason _streamedSongIds is: the library renders
    // hundreds of cards, and a self-resolving Follow button on each would be one query per card.
    private HashSet<int> _followedPersonaIds = new();

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
    private StreamQualifyingSettings _streamQualifying = new(30, false);
    private Dictionary<string, int> _streamQualifyingSecondsMap = new Dictionary<string, int>();
    private Action<int, int> _streamCountUpdatedHandler;
    private Action<int, int> _hubStreamCountHandler;
    protected SubscribeCtaDialogModel _subscribeCtaDialog;
    private bool _hasLoadedData = false;

    /// <summary>
    /// Represents the artist display information for a song card.
    /// </summary>
    protected class ArtistDisplayInfo
    {
        public string DisplayName { get; set; }
        public string LinkUrl { get; set; }
        /// <summary>
        /// SAS URL for the persona profile image, if a persona is associated.
        /// Null if no persona or no persona image.
        /// </summary>
        public string PersonaImageUrl { get; set; }

        /// <summary>
        /// The persona behind <see cref="DisplayName"/>, or null when the name came from free
        /// text or a creator display name rather than a persona.
        /// </summary>
        /// <remarks>
        /// Null is the normal case for plenty of songs, and it is what decides whether a card gets
        /// a Follow button at all - following is artist-level, and without a persona there is no
        /// artist entity to follow.
        /// </remarks>
        public int? PersonaId { get; set; }
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
    }

    private void OnStreamCountUpdated(int songMetadataId, int newCount)
    {
        // Update local stream count tracking. The membership test only reads; the write is inside
        // the hop, because a Dictionary written from a hub thread while the renderer enumerates it
        // is not a repaint bug.
        if (!_streamCounts.ContainsKey(songMetadataId))
        {
            return;
        }

        DispatchUiUpdate(() => _streamCounts[songMetadataId] = newCount);

        // The cards' LikeDislikeButtons take their eligibility from this page, so mid-listen
        // enablement from ANOTHER tab or device arrives here too - the per-card hub handlers that used
        // to provide it are gone, that being the point of the bulk set. One query per broadcast for the
        // one song broadcast about, only while it is still unstreamed, and only for a signed-in user -
        // not the per-card fan-out this page just got rid of.
        if (_currentUserId is int userId && !_streamedSongIds.Contains(songMetadataId))
        {
            DispatchUiUpdate(async () =>
            {
                if (await StreamCountService.HasUserStreamedSongAsync(userId, songMetadataId))
                {
                    _streamedSongIds.Add(songMetadataId);
                }
            });
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Load data on first render to avoid DbContext threading issues
        // (OnInitializedAsync can be called multiple times during circuit reconnections)
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

                if (_isAuthenticated)
                {
                    await LoadSubscriptionStatus();
                    _isAdmin = authState.User.IsInRole(Common.Helpers.Roles.Admin);
                    _currentUserId = GetUserId(authState.User);
                    Logger.LogInformation("MusicLibrary: Auth context loaded - _isAuthenticated={IsAuthenticated}, _isAdmin={IsAdmin}, _currentUserId={CurrentUserId}",
                        _isAuthenticated, _isAdmin, _currentUserId);
                }

                _streamQualifying = await AppSettingsService.GetStreamQualifyingSettingsAsync();
                await LoadFiles();
                await LoadStreamedSongIdsAsync();
            }
            catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
            {
                // The visitor left, or the circuit dropped, while this was still awaiting.
                // Nothing is wrong and there is nobody to tell, so it must not reach the
                // Error sink - that is what emailed the admin five times on 2026-09-02.
                Logger.LogDebug(ex, "Error loading MusicLibrary data");
            }
            catch (Exception ex)
            {
                _error = $"Error loading data: {ex.Message}";
                Logger.LogError(ex, "Error loading MusicLibrary data");
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }

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
            
            await EnsureJsModuleAsync();

            var isRestricted = IsCurrentPlayingTrackRestricted();
            var songMetadataId = GetCurrentPlayingSongMetadataId();
            var streamQualifyingSeconds = GetCurrentPlayingStreamQualifyingSeconds();
            // The song's true length goes to JS as well, because a preview listener's media element
            // only knows about the first 60 seconds.
            var trackLengthSeconds = GetTrackLengthSeconds(_playingFileName);

            await _jsModule.InvokeVoidAsync("initCardAudioPlayer", _activeAudioElement, _playingCardId, _dotNetRef, isRestricted, PREVIEW_DURATION_SECONDS, songMetadataId, streamQualifyingSeconds, trackLengthSeconds);
            await _jsModule.InvokeVoidAsync("setupCardProgressBarDrag", _activeProgressBarElement, _activeAudioElement, _playingCardId, _dotNetRef);
            await _jsModule.InvokeVoidAsync("setupCardVolumeBarDrag", _activeVolumeBarElement, _activeAudioElement, _playingCardId, _dotNetRef);

            // Set the initial track source
            string initialTrackUrl = null;
            if (!string.IsNullOrEmpty(_playingFileName))
            {
                // Playing a song - get the stream URL
                initialTrackUrl = await GetTrackStreamUrlAsync(_playingFileName);
            }

            if (string.IsNullOrWhiteSpace(initialTrackUrl))
            {
                // Play used to be attempted regardless, which produced the least helpful failure
                // available: the element had no source, so play() rejected with
                // "NotSupportedError: no supported source was found" - an error describing the
                // symptom while the actual reason sat in the server log, invisible to whoever was
                // looking at the browser. Stop here instead, and say so on both sides.
                Logger.LogWarning(
                    "No playable source for {FileName}; not attempting playback.",
                    _playingFileName);

                await _jsModule.InvokeVoidAsync(
                    "reportNoSource",
                    $"No playable source for '{_playingFileName}'.");

                _isActuallyPlaying = false;
                await InvokeAsync(StateHasChanged);
                return;
            }

            await _jsModule.InvokeVoidAsync("setTrackSource", _activeAudioElement, initialTrackUrl);

            // Auto-play when card is initialized
            await _jsModule.InvokeVoidAsync("playCard", _activeAudioElement);
            _isActuallyPlaying = true;

            await LoadPlayingCardLyricsAsync(songMetadataId);

            await InvokeAsync(StateHasChanged);
        }

        await TryInitLazyCardAnimationsAsync();
    }

    /// <summary>
    /// Imports the page's JS module once and restores the saved volume. Shared because the module
    /// is now needed before playback starts - the lazy card animations are set up on first render.
    /// </summary>
    private bool _lazyAnimationsReady;

    /// <summary>
    /// Hooks up the lazy card animations. Retried on each render until it reports success: the
    /// grid lives inside the page's loading/error branch, so it does not exist on first render.
    /// Once attached, a MutationObserver on the JS side keeps up with filter changes, so this is
    /// not called again - a per-render interop round trip would be far worse than the problem,
    /// since the grid re-renders on every playback progress tick.
    /// </summary>
    private async Task TryInitLazyCardAnimationsAsync()
    {
        if (_lazyAnimationsReady)
            return;

        try
        {
            await EnsureJsModuleAsync();
            _lazyAnimationsReady = await _jsModule.InvokeAsync<bool>("initLazyCardAnimations");
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Music library JS runtime disconnected before card animations were set up");
        }
    }

    private async Task EnsureJsModuleAsync()
    {
        if (_jsModule != null)
            return;

        _dotNetRef = DotNetObjectReference.Create(this);
        _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Public/MusicLibrary.razor.js");

        // Load saved volume from localStorage
        var savedVolume = await _jsModule.InvokeAsync<double>("getSavedVolume");
        _volume = savedVolume;
        _previousVolume = savedVolume;
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
                // Before disposing the module: this disconnects both observers and removes every
                // mounted player. Leaving them attached would keep animation loops running against
                // a page that is going away.
                await _jsModule.InvokeVoidAsync("disposeLazyCardAnimations");

                // Same reasoning for the audio: the hls.js instance holds a worker and a segment
                // fetch loop that would outlive the page.
                await _jsModule.InvokeVoidAsync("disposeAudioPlayer", _activeAudioElement);

                // And the bar drags. Four of each bar's six listeners are on `document`, so they
                // outlive the bars - and in a Blazor SPA nothing unloads the page to collect them.
                await _jsModule.InvokeVoidAsync("disposeBarDrags", _activeProgressBarElement, _activeVolumeBarElement);

                await _jsModule.DisposeAsync();
                _jsModule = null;
            }
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // Not just a disconnected browser: a circuit being torn down cancels the pending
            // interop call instead, which surfaces as TaskCanceledException.
            Logger.LogDebug(ex, "Music library JS runtime unavailable during disposal");
        }
        catch (Exception ex)
        {
            // Nothing may escape DisposeAsync - an exception thrown here is unhandled and
            // destroys the circuit being torn down. Warning, not Error, so a genuine fault stays
            // visible in the log without emailing the admin about a page that is already gone.
            Logger.LogWarning(ex, "Music library disposal did not complete cleanly.");
        }
        _dotNetRef?.Dispose();
        _dotNetRef = null;
    }

    /// <summary>
    /// Whether this song's lyrics are ones a listener may see.
    ///
    /// <para>
    /// Gated on the row's status rather than on a timings path existing, for the reason
    /// <c>IsPubliclyReadableAsync</c> spells out: timings held back for review sit at exactly the
    /// blob path a published song would use, so the path proves nothing. Since alignment stopped
    /// publishing, <c>NeedsReview</c> is where every successful run lands - it is the common case
    /// here, not an edge one.
    /// </para>
    /// </summary>
    protected bool HasPublishedLyrics(string fileName)
    {
        var songId = GetSongMetadataId(fileName);

        return songId > 0
            && _lyricsBySongId.TryGetValue(songId, out var lyrics)
            && lyrics.Status == SongLyricsStatus.Published
            && !string.IsNullOrWhiteSpace(lyrics.TimingsBlobPath);
    }

    /// <summary>
    /// Read the timings for the card that just started, if it has any.
    ///
    /// <para>
    /// One blob read per song actually played, rather than one per card drawn. The words are
    /// rendered server-side from this document, so it has to reach C# - a URL would only ever reach
    /// the browser's highlighter, which can colour spans but cannot create them.
    /// </para>
    /// </summary>
    private async Task LoadPlayingCardLyricsAsync(int songMetadataId)
    {
        // A new song starts on its art. Carrying the toggle over would open the next card on lyrics
        // that have not loaded yet.
        _showLyricsOnCard = false;
        _playingCardTimings = null;

        if (songMetadataId <= 0 || string.IsNullOrEmpty(_playingFileName) || !HasPublishedLyrics(_playingFileName))
        {
            return;
        }

        try
        {
            _playingCardTimings = await LyricsService.GetPublishedTimingsAsync(songMetadataId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex, "Could not read lyric timings for song {SongId} on a library card.", songMetadataId);
        }
    }

    /// <summary>Swap the playing card between its art and its lyrics.</summary>
    protected void ToggleCardLyrics() => _showLyricsOnCard = !_showLyricsOnCard;

    private async Task LoadFiles()
    {
        _loading = true; _error = null;
        try
        {
            // Load metadata from database - SQL is the source of truth
            var allMetadata = await SongMetadataService.GetAllAsync();

            // Swallowed deliberately, following CreatorSongManagement: lyrics are supplementary to
            // this page - the listener came for the songs - so a lyrics lookup that fails must cost
            // the toggle, not the entire library.
            try
            {
                // Coalesced rather than trusted: this feeds a TryGetValue on every card drawn, so a
                // null here would take the whole library down over a column nobody had asked for.
                _lyricsBySongId = await LyricsService.GetForSongsAsync(allMetadata.Select(m => m.Id))
                                  ?? new Dictionary<int, SongLyrics>();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not load lyrics availability for the music library.");
                _lyricsBySongId = new Dictionary<int, SongLyrics>();
            }
            
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
                
                // Cover art is no longer added here: it is read straight off the song's row via
                // artByAudioPath below, so a parallel list of image entries has no consumer.

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
            
            // Get all audio files. They are all standalone tracks.
            _files = allFiles.Where(f => MusicFileExtensions.IsAudioFile(f.Name)).ToList();

            // Cover art belongs to a song by database row, not by sharing a filename with the
            // audio. Matching on the base name would break the moment the two stop agreeing
            // (as they do under the GUID scheme, "{guid}-music" vs "{guid}-coverart") and could
            // previously cross-match a different song whose file happened to share a name.
            var artByAudioPath = allMetadata
                .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath) && !string.IsNullOrEmpty(m.ImageBlobPath))
                .GroupBy(m => m.Mp3BlobPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Clear home page songs tracking
            _homePageSongs.Clear();
            _aiGeneratedMap.Clear();
            _aiVocalsMap.Clear();
            _aiLyricsMap.Clear();
            _songDisplayOrders.Clear();

            // Build song art URL mappings and extract metadata for tracks
            foreach (var audioFile in _files)
            {
                if (artByAudioPath.TryGetValue(audioFile.Name, out var artMetadata))
                {
                    // Unknown dimensions (null) - show image (graceful degradation)
                    var isSquare = artMetadata.IsImageSquare ?? true;

                    if (isSquare)
                    {
                        _songArtSources[audioFile.Name] = CoverArtUrlBuilder.BuildProxy(
                            artMetadata.ImageBlobPath,
                            artMetadata.CoverArtVariantWidths,
                            artMetadata.CoverArtVariantVersion);
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

                    // The song's TRUE length, which the media element can no longer be asked for:
                    // a free-preview listener gets a manifest truncated to 60 seconds, so the
                    // element honestly reports a one-minute song. Showing that would mislabel every
                    // track and peg the preview marker at the end of the bar.
                    if (songMeta.TrackLength.HasValue)
                    {
                        _songTrackLengths[audioFile.Name] = songMeta.TrackLength.Value;
                    }
                    // Store the stream count
                    _streamCounts[songMeta.Id] = songMeta.NumberOfStreams;
                    // Track if this song should be displayed on the home page
                    if (songMeta.DisplayOnHomePage)
                    {
                        _homePageSongs.Add(audioFile.Name);
                    }
                    _songDisplayOrders[audioFile.Name] = songMeta.DisplayOrder;
                    // Store the song title if available
                    var effectiveTitle = SongTitleHelper.GetEffectiveTitle(
                        songMeta.SongTitle, songMeta.Mp3BlobPath, songMeta.BlobPath);
                    if (!string.IsNullOrEmpty(effectiveTitle))
                    {
                        _songTitles[audioFile.Name] = effectiveTitle;
                    }
                    _aiGeneratedMap[audioFile.Name] = songMeta.IsAiGenerated;
                    _aiVocalsMap[audioFile.Name] = songMeta.IsAiVocals;
                    _aiLyricsMap[audioFile.Name] = songMeta.IsAiLyrics;
                    // Store artist info
                    _artistInfoMap[audioFile.Name] = GetArtistDisplayInfo(songMeta);
                    // Store genre
                    _genreMap[audioFile.Name] = songMeta.Genre;
                    // Store stream qualifying seconds from creator
                    _streamQualifyingSecondsMap[audioFile.Name] = _streamQualifying.Resolve(songMeta.Creator?.StreamQualifyingSeconds);
                    // Store creator user ID for stream recording guard
                    _creatorUserIdMap[songMeta.Id] = songMeta.Creator?.UserId;
                    // Store creator ID for tip functionality
                    _creatorIdMap[audioFile.Name] = songMeta.CreatorId;
                }
            }
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // The visitor left, or the circuit dropped, while this was still awaiting.
            // Nothing is wrong and there is nobody to tell, so it must not reach the
            // Error sink - that is what emailed the admin five times on 2026-09-02.
            Logger.LogDebug(ex, "Error loading files for music library");
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
    /// 1. Persona.Name (if a persona is linked)
    /// 2. SongMetadata.ArtistName - links to /artist/{artistName}
    /// 3. Creator.DisplayName - links to /artist/{displayName}
    /// 4. Creator.User.Email (part before @) - links to /artist/{emailPrefix}
    /// </summary>
    private ArtistDisplayInfo GetArtistDisplayInfo(SongMetadata songMeta)
    {
        // Priority 1: Persona name
        if (songMeta.Persona != null && songMeta.Persona.IsEnabled && !string.IsNullOrWhiteSpace(songMeta.Persona.Name))
        {
            return new ArtistDisplayInfo
            {
                DisplayName = songMeta.Persona.Name,
                PersonaId = songMeta.PersonaId,
                LinkUrl = $"/artist/{Uri.EscapeDataString(songMeta.Persona.Name)}",
                PersonaImageUrl = string.IsNullOrEmpty(songMeta.Persona.ImageBlobPath)
                    ? null
                    // .card-persona-image renders at 40 CSS px at every breakpoint.
                    : PersonaImageUrlBuilder.BuildProxy(
                        songMeta.Persona.ImageBlobPath,
                        songMeta.Persona.ImageVariantWidths,
                        40,
                        songMeta.Persona.ImageVariantVersion)
            };
        }

        // Priority 2: ArtistName from SongMetadata
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

        // Priority 3: DisplayName from Creator - link to /artist/{displayName} to show all songs with this effective artist name
        if (songMeta.Creator != null && !string.IsNullOrWhiteSpace(songMeta.Creator.DisplayName))
        {
            return new ArtistDisplayInfo
            {
                DisplayName = songMeta.Creator.DisplayName,
                LinkUrl = $"/artist/{Uri.EscapeDataString(songMeta.Creator.DisplayName)}"
            };
        }

        // Priority 4: Email from Creator's User - use part before @ symbol for display and filtering
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
    /// Whether this visitor already follows the given persona, from the set resolved in bulk at
    /// load. Null when there is no persona, which is what keeps the card's Follow button off.
    /// </summary>
    protected bool? IsFollowingArtist(int? personaId) =>
        personaId.HasValue ? _followedPersonaIds.Contains(personaId.Value) : null;

    /// <summary>
    /// One card changed its follow state, so bring every other card by the same artist with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Following is artist-level while a card is song-level, so a library page routinely shows a
    /// dozen buttons for one artist. Without this they disagree until the page is reloaded - the
    /// clicked card says Following and the rest still say Follow.
    /// </para>
    /// <para>
    /// Deliberately local, and deliberately not SignalR. Every card that needs to know is in this
    /// circuit, so a broadcast would be a round trip to learn something the page already knows -
    /// and follow state is per-user, where a like COUNT is public. LikeDislikeButtons draws the
    /// same line: LikeCountHub carries counts, and _userLikeStatus never leaves the circuit.
    /// </para>
    /// <para>
    /// Updating the shared set is the whole mechanism. Each button reads KnownIsFollowing on every
    /// render, so re-rendering the parent - which returning from an EventCallback does on its own -
    /// is what makes the others agree.
    /// </para>
    /// </remarks>
    protected void OnArtistFollowStateChanged(int? personaId, bool isFollowing)
    {
        if (personaId is not int id)
        {
            return;
        }

        if (isFollowing)
        {
            _followedPersonaIds.Add(id);
        }
        else
        {
            _followedPersonaIds.Remove(id);
        }
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
    /// Counts are cross-filtered by the AI, title and currently selected artist filters.
    /// </summary>
    protected Dictionary<string, int> GetGenreItems()
    {
        var fileNames = _genreMap.Keys.AsEnumerable();

        fileNames = fileNames.Where(MatchesAiFilter).Where(MatchesTitleFilter);

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

    protected void SetAiFilter(string filter)
    {
        _selectedAiFilter = filter;
    }

    protected void TogglePanel(FilterPanel panel)
    {
        _openPanel = _openPanel == panel ? FilterPanel.None : panel;
    }

    protected void SelectAiFilterOption(string filter)
    {
        SetAiFilter(filter);
        _openPanel = FilterPanel.None;
    }

    protected void HandleAiFilterClear()
    {
        SetAiFilter(AiFilterAll);
        _openPanel = FilterPanel.None;
    }

    protected bool HasActiveAiFilter()
    {
        return _selectedAiFilter != AiFilterAll;
    }

    protected bool HasActiveTitleFilter()
    {
        return !string.IsNullOrWhiteSpace(_titleFilter);
    }

    /// <summary>
    /// Case-insensitive substring match on the song's display title, so typing "be" finds
    /// "The Best Song". A blank filter matches everything.
    /// </summary>
    protected bool MatchesTitleFilter(string fileName)
    {
        var query = _titleFilter?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return GetDisplayTitle(fileName).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    protected void HandleTitleChanged(string value)
    {
        _titleFilter = value ?? string.Empty;
    }

    protected void HandleTitleCleared()
    {
        _titleFilter = string.Empty;
        _openPanel = FilterPanel.None;
    }

    /// <summary>One music-type choice: the stored value, its menu label, and an optional icon.</summary>
    protected sealed record AiFilterOption(string Value, string Label, string Icon);

    /// <summary>
    /// The music-type menu, in display order. Data rather than six near-identical buttons, which is
    /// also what stops the menu labels and the pill label drifting apart.
    /// </summary>
    protected static readonly IReadOnlyList<AiFilterOption> AiFilterOptions = new[]
    {
        new AiFilterOption(AiFilterAll, "All Music", null),
        new AiFilterOption(AiFilterAny, "Any AI", null),
        new AiFilterOption(AiFilterAiMusic, "AI Music", "/images/music_icon_24.png"),
        new AiFilterOption(AiFilterAiVocals, "AI Vocals", "/images/vocals_icon_24.png"),
        new AiFilterOption(AiFilterAiLyrics, "AI Lyrics", "/images/lyrics_icon_24.png"),
        new AiFilterOption(AiFilterNonAi, "Non-AI Music", null),
    };

    protected string GetAiFilterPillLabel()
    {
        // "All Music" is the menu wording for the inactive state; the pill says what it filters.
        if (_selectedAiFilter == AiFilterAll)
        {
            return "Music Type";
        }

        return AiFilterOptions.FirstOrDefault(o => o.Value == _selectedAiFilter)?.Label ?? "Music Type";
    }

    protected string GetAiFilterOptionClass(string filter)
    {
        return _selectedAiFilter == filter ? "selected" : string.Empty;
    }

    /// <summary>
    /// Gets artist items with counts for the FilterPill component.
    /// Counts are cross-filtered by the AI, title and currently selected genre filters.
    /// </summary>
    protected Dictionary<string, int> GetArtistItems()
    {
        var fileNames = _artistInfoMap.Keys.AsEnumerable();

        fileNames = fileNames.Where(MatchesAiFilter).Where(MatchesTitleFilter);

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

    /// <summary>
    /// True when any of the four filters is narrowing the list. Used to tell an empty catalogue
    /// apart from a catalogue the filters have emptied - the second is recoverable and needs to
    /// say so, the first does not.
    /// </summary>
    protected bool HasAnyActiveFilter()
        => _selectedGenres.Count > 0 || _selectedArtists.Count > 0 || HasActiveAiFilter()
           || HasActiveTitleFilter();

    /// <summary>Clears all four filters at once, for the empty-results state.</summary>
    protected void ClearAllFilters()
    {
        _selectedGenres.Clear();
        _selectedArtists.Clear();
        SetAiFilter(AiFilterAll);
        _titleFilter = string.Empty;
        _openPanel = FilterPanel.None;
    }

    /// <summary>
    /// Retry after a failed load. LoadFiles clears _error on entry, but nothing else ever did -
    /// its finally resets only _loading - so before this a single transient failure left the page
    /// blank for the rest of the circuit with no way back.
    /// </summary>
    protected async Task RetryLoadAsync()
    {
        _error = null;
        await LoadFiles();
        // Everything the first-render block loads after LoadFiles has to come back with it, or a
        // recovered failure leaves the page half-loaded for the rest of the circuit. Eligibility is the
        // one that bites: the cards get their KnownHasStreamed from this set, and a supplied instance
        // never self-corrects - without this line, a retry left every thumb dimmed until a full reload.
        await LoadStreamedSongIdsAsync();
        await InvokeAsync(StateHasChanged);
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

    private int? GetSongDisplayOrder(string fileName)
    {
        return _songDisplayOrders.TryGetValue(fileName, out var displayOrder)
            ? displayOrder
            : null;
    }

    private int GetNullDisplayOrderTieBreaker(string fileName)
    {
        return GetSongDisplayOrder(fileName).HasValue ? int.MinValue : GetSongMetadataId(fileName);
    }

    protected IEnumerable<StorageFileInfo> GetFilteredFiles()
    {
        var files = _files.AsEnumerable();
        
        // When showing home page featured items, filter by DisplayOnHomePage
        if (ShowHomePageFeatured)
        {
            files = files.Where(f => _homePageSongs.Contains(f.Name));
        }

        files = files.Where(f => MatchesAiFilter(f.Name));
        
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

        // Apply title filter
        files = files.Where(f => MatchesTitleFilter(f.Name));

        // New songs with a null DisplayOrder float to the top. Ranked songs follow
        // in ascending DisplayOrder, and null ties prefer newer uploads via higher Ids.
        return files
            .OrderBy(f => GetSongDisplayOrder(f.Name).HasValue ? 1 : 0)
            .ThenBy(f => GetSongDisplayOrder(f.Name) ?? int.MaxValue)
            .ThenByDescending(f => GetNullDisplayOrderTieBreaker(f.Name))
            .ThenBy(f => GetSongMetadataId(f.Name));
    }

    protected bool IsAiGeneratedSong(string fileName)
    {
        return _aiGeneratedMap.TryGetValue(fileName, out var isAiGenerated) && isAiGenerated;
    }

    protected bool IsAiVocalsSong(string fileName)
    {
        return _aiVocalsMap.TryGetValue(fileName, out var isAiVocals) && isAiVocals;
    }

    protected bool IsAiLyricsSong(string fileName)
    {
        return _aiLyricsMap.TryGetValue(fileName, out var isAiLyrics) && isAiLyrics;
    }

    private bool MatchesAiFilter(StorageFileInfo file)
    {
        return MatchesAiFilter(file.Name);
    }

    private bool MatchesAiFilter(string fileName)
    {
        var isAiMusic = IsAiGeneratedSong(fileName);
        var isAiVocals = IsAiVocalsSong(fileName);
        var isAiLyrics = IsAiLyricsSong(fileName);
        var hasAnyAi = isAiMusic || isAiVocals || isAiLyrics;

        return _selectedAiFilter switch
        {
            AiFilterAny => hasAnyAi,
            AiFilterAiMusic => isAiMusic,
            AiFilterAiVocals => isAiVocals,
            AiFilterAiLyrics => isAiLyrics,
            AiFilterNonAi => !hasAnyAi,
            _ => true
        };
    }


    /// <summary>
    /// The song's true length in seconds, or 0 when it is unknown.
    ///
    /// <para>
    /// Comes from <c>SongMetadata.TrackLength</c> rather than from the media element, because a
    /// free-preview listener is served a manifest truncated to <see cref="PREVIEW_DURATION_SECONDS"/>
    /// and the element therefore reports a one-minute song. Displaying that would mislabel every
    /// track and push the preview marker to the far right of the progress bar — the opposite of what
    /// the marker is for.
    /// </para>
    /// </summary>
    private double GetTrackLengthSeconds(string fileName)
        => !string.IsNullOrEmpty(fileName) && _songTrackLengths.TryGetValue(fileName, out var length)
            ? length
            : 0;

    protected int GetSongMetadataId(string fileName)
    {
        return _songMetadataIds.TryGetValue(fileName, out var id) ? id : 0;
    }

    /// <summary>
    /// One bulk query for the whole page. Must run after <see cref="LoadFiles"/>, which is what fills
    /// <see cref="_songMetadataIds"/>.
    /// </summary>
    private async Task LoadStreamedSongIdsAsync()
    {
        if (!_currentUserId.HasValue || _songMetadataIds.Count == 0)
            return;

        try
        {
            _streamedSongIds = await StreamCountService.GetUserStreamedSongIdsAsync(
                _currentUserId.Value, _songMetadataIds.Values);

            var personaIds = _artistInfoMap.Values
                .Where(info => info.PersonaId.HasValue)
                .Select(info => info.PersonaId!.Value)
                .Distinct()
                .ToList();

            if (personaIds.Count > 0)
            {
                _followedPersonaIds = (await ArtistFollowService.GetFollowedPersonaIdsAsync(
                    personaIds, _currentUserId.Value)).ToHashSet();
            }
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // The visitor left, or the circuit dropped, while this was still awaiting.
            // Nothing is wrong and there is nobody to tell, so it must not reach the
            // Error sink - that is what emailed the admin five times on 2026-09-02.
            Logger.LogDebug(ex, "Error loading streamed-song eligibility for user {UserId}", _currentUserId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading streamed-song eligibility for user {UserId}", _currentUserId);
        }
    }

    protected bool HasUserStreamedSong(string fileName)
    {
        return _streamedSongIds.Contains(GetSongMetadataId(fileName));
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
        return SongTitleHelper.GetEffectiveTitle(null, fileName);
    }
    
    protected string GetStoredSongTitle(string fileName)
    {
        return _songTitles.TryGetValue(fileName, out var title) ? title : null;
    }

    /// <summary>
    /// The encrypted-HLS manifest URL for one card's track.
    ///
    /// <para>
    /// This used to fetch <c>api/music/url/{path}</c> to obtain a SAS — the server asking itself a
    /// question over HTTP — and fall back to <c>api/music/{path}</c>, an anonymous proxy that served
    /// the plaintext MP3 to anyone. Both are gone, and there is deliberately no fallback: a fallback
    /// here would be a way to get the audio without the encryption.
    /// </para>
    ///
    /// <para>
    /// The row is fetched at play time rather than held for every card on the page. The page carries
    /// only file-name-to-id for its whole grid, and loading full metadata for every card to serve
    /// the one the listener eventually clicks would be the wrong trade.
    /// </para>
    /// </summary>
    private async Task<string> GetTrackStreamUrlAsync(string fileName)
    {
        var songMetadataId = GetSongMetadataId(fileName);
        if (songMetadataId <= 0)
        {
            Logger.LogWarning("No metadata id for {FileName}; it cannot be played.", fileName);
            return null;
        }

        var metadata = await SongMetadataService.GetByIdAsync(songMetadataId);
        if (metadata == null)
        {
            return null;
        }

        // IsCurrentPlayingTrackRestricted already encodes every exemption this page applies -
        // subscription, admin, featured song, own song - so the entitlement is simply its inverse.
        var url = HlsStreamUrls.BuildManifestUrl(
            metadata,
            _currentUserId,
            !IsCurrentPlayingTrackRestricted());

        if (url == null)
        {
            Logger.LogWarning(
                "Song {SongMetadataId} has no encrypted HLS package, so it cannot be played yet.",
                songMetadataId);
        }

        return url;
    }

    protected CoverArtSource GetAlbumArtSource(string fileName)
    {
        return _songArtSources.TryGetValue(fileName, out var source) ? source : CoverArtSource.None;
    }

    /// <summary>
    /// Navigates to the song player. Named for what it does - it used to be called
    /// GetSongPlayerUrl, which returned nothing and navigated as a side effect.
    /// Called from .card-art-open, the transparent button covering the card artwork.
    /// </summary>
    protected void OpenSongPlayer(string fileName)
    {
        var songTitle = GetDisplayTitle(fileName);

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

        // Home-page featured songs are full-length for everyone
        if (!string.IsNullOrEmpty(_playingFileName) && _homePageSongs.Contains(_playingFileName))
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

        return _streamQualifying.Resolve(creatorSeconds: null);
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

            // This card's LikeDislikeButtons get their eligibility from the page's bulk set, so the
            // thumbs only come alive mid-listen if the set moves too. Asked back from the database
            // rather than assumed, because the increment can decline to record - a creator playing
            // their own song, most notably - and assuming would light buttons the server will refuse.
            //
            // Its own try/catch: the stream above is already recorded, so a failure here must neither
            // log as a failed recording nor skip the repaint the new count needs.
            if (_currentUserId.HasValue && !_streamedSongIds.Contains(songMetadataId))
            {
                try
                {
                    if (await StreamCountService.HasUserStreamedSongAsync(_currentUserId.Value, songMetadataId))
                    {
                        _streamedSongIds.Add(songMetadataId);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex,
                        "MusicLibrary: Stream recorded for song {SongMetadataId} but the rating-eligibility re-check failed; thumbs stay dimmed until reload",
                        songMetadataId);
                }
            }

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
