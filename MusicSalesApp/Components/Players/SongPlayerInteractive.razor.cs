using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Notifications;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Players;

public partial class SongPlayerInteractiveModel : BlazorBase, IAsyncDisposable
{
    private const double PREVIEW_DURATION_SECONDS = 60.0;

    [Parameter]
    public string SongTitle { get; set; }

    [Parameter]
    public string TipStatus { get; set; }

    [Parameter]
    public string TipPayPalToken { get; set; }

    protected SfToast _toastRef;
    protected bool _tipReturnHandled;
    protected bool IsProcessingTipReturn => !string.IsNullOrEmpty(TipStatus) && !_tipReturnHandled;

    protected bool _loading = true;
    protected string _error;
    protected StorageFileInfo _songInfo;
    protected CoverArtSource _albumArtSource = CoverArtSource.None;
    protected string _streamUrl;
    protected bool _isPlaying;
    protected double _currentTime;
    protected double _duration;
    protected ElementReference _audioElement;
    protected ElementReference _progressBarContainer;
    protected ElementReference _volumeBarContainer;
    protected bool _shuffleEnabled;
    protected bool _repeatEnabled;
    protected double _volume = 0.4;
    protected double _previousVolume = 0.4;
    protected bool _isMuted;
    protected bool _isAuthenticated;
    protected int _streamCount;
    private Models.SongMetadata _songMetadata;
    private int _defaultStreamQualifyingSeconds = 30;
    private IJSObjectReference _jsModule;
    private DotNetObjectReference<SongPlayerInteractiveModel> _dotNetRef;
    private bool invokedJs = false;
    protected bool _hasActiveSubscription;
    protected bool _isAdmin;
    protected bool _isCreatorOfSong;
    protected SongLyrics _lyrics;
    protected MusicSalesApp.Common.Contracts.LyricsTimingsDocument _lyricsTimings;
    protected bool _showLyrics;
    private int? _currentUserId;

    /// <summary>
    /// The songs Previous/Next step through: this song's genre listing, in the order the
    /// <c>/genre/{name}</c> page shows it.
    ///
    /// <para>
    /// A single-song URL carries no record of where the listener came from, so the transport needs
    /// a defined set to move within. Genre is the one context the page already puts on screen (the
    /// chip under the title links straight to it), which makes the buttons predictable rather than
    /// arbitrary. Empty when the genre has only this song, which is what disables both buttons.
    /// </para>
    /// </summary>
    private List<Models.SongMetadata> _genreSiblings = new();

    private Action<int, int> _streamCountUpdatedHandler;
    private Action<int, int> _hubStreamCountHandler;
    protected SubscribeCtaDialogModel _subscribeCtaDialog;
    protected TipDialogModel _tipDialog;

    protected int GetCreatorIdForTip()
    {
        return _songMetadata?.CreatorId ?? 0;
    }

    protected bool CanShowTipButton()
    {
        return _isAuthenticated && _songMetadata?.CreatorId != null && _songMetadata.CreatorId > 0 && !_isCreatorOfSong;
    }

    protected async Task ShowTipDialog()
    {
        if (_tipDialog != null)
        {
            await _tipDialog.ShowAsync();
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _streamCountUpdatedHandler = OnStreamCountUpdated;
        StreamCountService.OnStreamCountUpdated += _streamCountUpdatedHandler;

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
        if (!_tipReturnHandled && !_loading && _songInfo != null
            && !string.IsNullOrEmpty(TipStatus)
            && (!string.IsNullOrEmpty(TipPayPalToken) || TipStatus == "cancelled"))
        {
            _tipReturnHandled = true;
            await HandleTipReturnAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (!invokedJs && !_loading && !IsProcessingTipReturn && _songInfo != null)
        {
            invokedJs = true;
            _dotNetRef = DotNetObjectReference.Create(this);
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Players/SongPlayerInteractive.razor.js");
            await _jsModule.InvokeVoidAsync("initAudioPlayer", _audioElement, _dotNetRef, IsProgressBarRestricted(), PREVIEW_DURATION_SECONDS, GetSongMetadataId(), GetStreamQualifyingSeconds());
            await _jsModule.InvokeVoidAsync("setupProgressBarDrag", _progressBarContainer, _audioElement, _dotNetRef, IsProgressBarRestricted(), PREVIEW_DURATION_SECONDS);
            await _jsModule.InvokeVoidAsync("setupVolumeBarDrag", _volumeBarContainer, _audioElement, _dotNetRef);

            var savedVolume = await _jsModule.InvokeAsync<double>("getSavedVolume");
            _volume = savedVolume;
            _previousVolume = savedVolume;

            await InvokeAsync(StateHasChanged);
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

    public async ValueTask DisposeAsync()
    {
        if (_streamCountUpdatedHandler != null)
        {
            StreamCountService.OnStreamCountUpdated -= _streamCountUpdatedHandler;
        }

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
            _defaultStreamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();

            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;
            
            var decodedTitle = Uri.UnescapeDataString(SongTitle);

            var allMetadata = await SongMetadataService.GetAllAsync();
            
            _songMetadata = allMetadata.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.Mp3BlobPath) &&
                MusicFileExtensions.IsAudioFile(m.Mp3BlobPath) &&
                (SongTitleHelper.GetEffectiveTitle(m.SongTitle, m.Mp3BlobPath, m.BlobPath)
                    .Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileNameWithoutExtension(Path.GetFileName(m.Mp3BlobPath)).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(m.Mp3BlobPath).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(m.SongTitle) && m.SongTitle.Equals(decodedTitle, StringComparison.OrdinalIgnoreCase))));

            if (_songMetadata == null)
            {
                _error = $"Song '{decodedTitle}' not found.";
                _loading = false;
                return;
            }

            _streamCount = _songMetadata.NumberOfStreams;

            // Published lyrics only. GetForSongAsync returns the row whatever state it is in, and
            // every other state - pending, waiting for the creator, failed - must look identical to
            // "this song has no lyrics" from out here.
            _lyrics = await LyricsService.GetForSongAsync(_songMetadata.Id);

            // The document itself, not just the row. The scroller renders one span per word from a
            // document C# holds, so without this there is nothing on the page for the highlighter to
            // highlight and the panel sits on its empty message forever.
            _lyricsTimings = await LyricsService.GetPublishedTimingsAsync(_songMetadata.Id);

            // The stage opens on lyrics when there are any. The artwork is already on screen in the
            // hero band above, so opening on art would show the same image twice and bury the one
            // thing this layout exists to surface. With no published lyrics this stays false and the
            // stage shows the artwork large, with no toggle above it.
            _showLyrics = HasPublishedLyrics();

            await LoadGenreSiblings();

            _songInfo = new StorageFileInfo
            {
                Name = _songMetadata.Mp3BlobPath ?? _songMetadata.BlobPath,
                Length = 0,
                ContentType = "audio/mpeg",
                LastModified = _songMetadata.UpdatedAt,
                Tags = new Dictionary<string, string>()
            };

            await LoadStreamUrl();

            // Take the cover art from this song's own row rather than hunting for an image whose
            // filename matches the audio: the two no longer share a base name under the GUID
            // scheme, and name matching could pick up a different song's art.
            var hasCoverArt = !string.IsNullOrEmpty(_songMetadata.ImageBlobPath)
                && IsImageFile(_songMetadata.ImageBlobPath)
                && (_songMetadata.IsImageSquare ?? true);

            _albumArtSource = hasCoverArt
                ? CoverArtUrlBuilder.BuildProxy(
                    _songMetadata.ImageBlobPath,
                    _songMetadata.CoverArtVariantWidths,
                    _songMetadata.CoverArtVariantVersion)
                : CoverArtSource.None;

            if (_isAuthenticated)
            {
                await LoadSubscriptionStatus();

                _isAdmin = authState.User.IsInRole(Common.Helpers.Roles.Admin);

                var currentUserId = GetUserId(authState.User);
                if (currentUserId.HasValue)
                {
                    _currentUserId = currentUserId.Value;

                    if (_songMetadata.Creator != null)
                    {
                        _isCreatorOfSong = _songMetadata.Creator.UserId == currentUserId.Value;
                    }
                    else if (_songMetadata.CreatorId.HasValue && _songMetadata.CreatorId > 0)
                    {
                        var creator = await CreatorService.GetCreatorByUserIdAsync(currentUserId.Value);
                        _isCreatorOfSong = creator != null && creator.Id == _songMetadata.CreatorId.Value;
                    }
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
            var response = await Http.GetFromJsonAsync<SasUrlResponse>($"api/music/url/{SafeEncodePath(_songInfo.Name)}");
            if (response != null && !string.IsNullOrEmpty(response.Url))
            {
                _streamUrl = response.Url;
            }
            else
            {
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
        if (_hasActiveSubscription)
            return false;
        if (_isAdmin)
            return false;
        if (_songMetadata?.DisplayOnHomePage == true)
            return false;
        if (_isCreatorOfSong)
            return false;
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
        var artist = GetArtistDisplayName();
        var song = GetDisplayTitle();
        if (!string.IsNullOrEmpty(artist))
            return $"{artist} - {song} - StreamTunes";
        return $"{song} - StreamTunes";
    }

    protected string GetDisplayTitle()
    {
        if (_songMetadata != null)
        {
            return SongTitleHelper.GetEffectiveTitle(
                _songMetadata.SongTitle,
                _songMetadata.Mp3BlobPath,
                _songMetadata.BlobPath);
        }
        
        if (_songInfo == null) return SongTitle ?? "Unknown Song";
        return Path.GetFileNameWithoutExtension(Path.GetFileName(_songInfo.Name));
    }

    protected string GetArtistDisplayName()
    {
        if (_songMetadata == null)
            return null;

        // Use the model's GetEffectiveArtistName() which now includes persona as highest priority
        var name = _songMetadata.GetEffectiveArtistName();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Gets the persona for the current song, or null if no persona is associated.
    /// </summary>
    protected Models.CreatorPersona GetPersona()
    {
        var persona = _songMetadata?.Persona;
        return (persona != null && persona.IsEnabled) ? persona : null;
    }

    /// <summary>
    /// Gets the Creator bio for the current song, used as a fallback when there is no active persona.
    /// Returns null when a persona is already displayed.
    /// </summary>
    protected string GetCreatorBio()
    {
        if (GetPersona() != null)
            return null;
        return _songMetadata?.Creator?.Bio;
    }

    /// <summary>
    /// Gets the SAS URL for the persona image, or null if no persona image exists.
    /// </summary>
    protected string GetPersonaImageUrl()
    {
        var persona = GetPersona();
        if (persona == null || string.IsNullOrEmpty(persona.ImageBlobPath))
            return null;
        // .persona-image-sm renders at 60 CSS px (40 below 576px), so the 60 covers every case.
        return CreatorPersonaService.GetPersonaImageSasUrl(
            persona.ImageBlobPath, persona.ImageVariantWidths, 60, TimeSpan.FromHours(2));
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

    protected string GetGenre()
    {
        if (_songMetadata != null && !string.IsNullOrWhiteSpace(_songMetadata.Genre))
        {
            return _songMetadata.Genre;
        }
        return "Unknown Genre";
    }

    protected bool IsAiGeneratedSong()
    {
        return _songMetadata?.IsAiGenerated == true;
    }

    protected bool IsAiVocalsSong()
    {
        return _songMetadata?.IsAiVocals == true;
    }

    protected bool IsAiLyricsSong()
    {
        return _songMetadata?.IsAiLyrics == true;
    }

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
            await _jsModule.InvokeVoidAsync("seekTo", _audioElement, 0);
            await _jsModule.InvokeVoidAsync("play", _audioElement);
            _isPlaying = true;
        }
        else
        {
            _isPlaying = false;

            if (IsProgressBarRestricted() && _subscribeCtaDialog != null)
            {
                await _subscribeCtaDialog.OnPreviewEndedAsync();
            }
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


    /// <summary>
    /// Whether this song has lyrics a listener is allowed to see.
    ///
    /// <para>
    /// Published is the only state that qualifies. Timings waiting for their creator sit at the same
    /// blob path as published ones and would 404 from the media route anyway, but the toggle is
    /// hidden here as well so nobody is offered a panel that can only disappoint them.
    /// </para>
    /// </summary>
    protected bool HasPublishedLyrics() =>
        _lyrics is { Status: SongLyricsStatus.Published }
        && !string.IsNullOrWhiteSpace(_lyrics.TimingsBlobPath);

    /// <summary>
    /// The timings URL, carrying the version as a cache-buster.
    ///
    /// <para>
    /// <b>The <c>?v=</c> is load-bearing rather than decorative.</b> The blob path never changes
    /// between versions - that is what <c>SongLyrics.Version</c> exists for - and MusicController
    /// serves these with a year-long immutable cache header. Without it, a creator who re-times a
    /// chorus and republishes would find every browser that had already seen the song still showing
    /// the old timings, permanently.
    /// </para>
    /// </summary>
    protected string GetLyricsTimingsUrl() =>
        HasPublishedLyrics()
            ? $"/api/music/{_lyrics.TimingsBlobPath}?v={_lyrics.Version}"
            : null;

    protected void ToggleLyrics() => _showLyrics = !_showLyrics;

    /// <summary>Segmented-control setters. Pressing the active segment is a no-op.</summary>
    protected void ShowLyricsPanel() => _showLyrics = true;

    /// <inheritdoc cref="ShowLyricsPanel" />
    protected void ShowArtPanel() => _showLyrics = false;

    /// <summary>
    /// Whether the stage panel is showing lyrics rather than the artwork. Both are mounted at all
    /// times; this only decides which one carries <c>is-hidden</c>.
    /// </summary>
    protected bool ShowingLyrics() => _showLyrics && HasPublishedLyrics();

    // ---------------------------------------------------------------------------------------
    // Previous / Next
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the genre listing this song belongs to, filtered and ordered exactly as the
    /// <c>/genre/{name}</c> page builds it, so the transport buttons and that page agree on what
    /// "next" means. <see cref="ISongMetadataService.GetByGenreAsync"/> already special-cases
    /// "Unknown Genre" the same way <see cref="GetGenre"/> reports it.
    /// </summary>
    private async Task LoadGenreSiblings()
    {
        _genreSiblings = new List<Models.SongMetadata>();

        if (_songMetadata == null)
        {
            return;
        }

        try
        {
            var genreSongs = await SongMetadataService.GetByGenreAsync(GetGenre());
            if (genreSongs == null)
            {
                return;
            }

            _genreSiblings = genreSongs
                .Where(s => !string.IsNullOrEmpty(s.Mp3BlobPath))
                .ToList();
        }
        catch (Exception ex)
        {
            // A failure here costs the listener two buttons, not the page. Disable and carry on.
            Logger.LogWarning(ex, "SongPlayer: could not load genre siblings for {Genre}", GetGenre());
            _genreSiblings = new List<Models.SongMetadata>();
        }
    }

    private int CurrentSiblingIndex() =>
        _songMetadata == null ? -1 : _genreSiblings.FindIndex(s => s.Id == _songMetadata.Id);

    /// <summary>True when there is somewhere for Previous/Next to go.</summary>
    protected bool HasSiblingSongs() => _genreSiblings.Count > 1 && CurrentSiblingIndex() >= 0;

    /// <summary>
    /// The song <paramref name="offset"/> places away, wrapping at both ends so the buttons never
    /// dead-end. With shuffle on, Next lands on a random other song in the genre instead - Previous
    /// always steps back in listing order, because there is no play history to step back through.
    /// </summary>
    private Models.SongMetadata SiblingAt(int offset)
    {
        if (!HasSiblingSongs())
        {
            return null;
        }

        var index = CurrentSiblingIndex();
        var count = _genreSiblings.Count;

        if (_shuffleEnabled && offset > 0)
        {
            var hop = Random.Shared.Next(1, count);
            return _genreSiblings[(index + hop) % count];
        }

        var target = ((index + offset) % count + count) % count;
        return _genreSiblings[target];
    }

    protected string GetSongUrl(Models.SongMetadata song) =>
        $"/song/{Uri.EscapeDataString(SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath))}";

    /// <summary>
    /// Moves to an adjacent song.
    ///
    /// <para>
    /// <b><c>forceLoad</c> is correct here, not a workaround - please leave it.</b> This page plays
    /// exactly one song, so moving to another one is genuinely leaving it: a different URL, a
    /// different static-SSR SEO shell, a different share link and a different stream count. A full
    /// navigation is what clicking any other link on the site already does. Turning this into a soft
    /// navigation would be making a page transition pretend not to be one.
    /// </para>
    ///
    /// <para>
    /// It also happens to be the only safe option today, which is worth knowing before anyone tries.
    /// <c>initAudioPlayer</c> attaches its audio listeners once and never removes them, and two of
    /// the values they read - <c>isRestricted</c> and <c>maxDuration</c> - are captured in those
    /// listeners' closures rather than held in module state, so they cannot be updated for a new
    /// song. Swapping in place while reusing the element would keep enforcing the previous song's
    /// preview rule and credit its plays to the previous <c>songMetadataId</c> - and re-running
    /// <c>initAudioPlayer</c> instead would simply add a second set of listeners, double-counting
    /// streams. Creators are paid per qualifying stream, so that is a real cost, not a cosmetic one.
    /// A full load tears the whole document down and sidesteps all of it.
    /// </para>
    ///
    /// <para>
    /// The playlist player does swap in place, via <c>playerState</c> and <c>changeTrack</c> in
    /// PlaylistPlayerInteractive.razor.js - but it has to, because there one page owns many tracks.
    /// That is not this page.
    /// </para>
    /// </summary>
    private void NavigateToSibling(int offset)
    {
        var target = SiblingAt(offset);
        if (target == null)
        {
            return;
        }

        NavigationManager.NavigateTo(GetSongUrl(target), forceLoad: true);
    }

    protected void PlayPreviousSong() => NavigateToSibling(-1);

    protected void PlayNextSong() => NavigateToSibling(1);

    protected string PreviousSongTitleForTooltip() => SiblingTooltip("Previous", -1);

    protected string NextSongTitleForTooltip() =>
        _shuffleEnabled && HasSiblingSongs() ? "Next (shuffle)" : SiblingTooltip("Next", 1);

    private string SiblingTooltip(string label, int offset)
    {
        if (!HasSiblingSongs())
        {
            return $"{label} - no other songs in {GetGenre()}";
        }

        // Read the neighbour positionally so the tooltip cannot disagree with where the button goes,
        // and so shuffle's randomness never leaks into a label.
        var index = CurrentSiblingIndex();
        var count = _genreSiblings.Count;
        var neighbour = _genreSiblings[((index + offset) % count + count) % count];

        return $"{label}: {SongTitleHelper.GetEffectiveTitle(neighbour.SongTitle, neighbour.Mp3BlobPath, neighbour.BlobPath)}";
    }

    protected double GetDisplayVolume()
    {
        return _isMuted ? 0 : _volume;
    }
}
