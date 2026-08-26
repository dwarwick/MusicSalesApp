using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Shared;

public partial class LikeDislikeButtonsModel : BlazorBase, IDisposable
{
    [Parameter]
    public int SongMetadataId { get; set; }

    [Parameter]
    public EventCallback OnLikeStatusChanged { get; set; }

    [Parameter]
    public bool VerticalLayout { get; set; } = false;

    /// <summary>
    /// Set when the host page already knows whether the current user has streamed this song, which is
    /// what entitles them to rate it. Null means the component finds out for itself.
    ///
    /// The distinction is one instance versus hundreds. On the single-song players this component can
    /// afford its own eligibility query and its own StreamCountHub subscription - there is one of it.
    /// The music library renders one per card, and self-managed instances there meant one DB query per
    /// card per load, plus one DB query per card on every stream broadcast site-wide: a single stream
    /// anywhere fanned out to hundreds of queries on every connected library circuit. The library
    /// resolves eligibility once in bulk and feeds each card through this parameter instead; supplied
    /// instances run no eligibility query and take no hub subscription.
    ///
    /// Decided at first render: a page that wants the supplied mode must pass a value from the start
    /// (the library always does), not flip from null later.
    /// </summary>
    [Parameter]
    public bool? KnownHasStreamed { get; set; }

    protected int _likeCount = 0;
    protected int _dislikeCount = 0;
    protected bool? _userLikeStatus = null; // true = liked, false = disliked, null = no preference
    protected bool _hasStreamed = false;
    protected bool _isProcessing = false;
    private int? _currentUserId = null;
    private int _previousSongMetadataId = 0;
    private bool _needsDataReload = false;
    protected SfDialog _loginDialog;

    /// <summary>
    /// Whether the buttons are live for this user and song.
    ///
    /// Setting an opinion requires having streamed the song. An existing rating stays clickable
    /// regardless, so a rating made before that rule can always be taken back - the same asymmetry
    /// <see cref="SongLikeService"/> enforces server-side.
    /// </summary>
    protected bool CanRate => _hasStreamed || _userLikeStatus != null;

    protected override void OnParametersSet()
    {
        // Track parameter changes; actual DB work happens in OnAfterRenderAsync
        if (SongMetadataId > 0 && SongMetadataId != _previousSongMetadataId)
        {
            _previousSongMetadataId = SongMetadataId;
            _needsDataReload = true;
        }

        // Supplied eligibility stays live across parent re-renders, so when the library records a
        // stream and refreshes its bulk set, the affected card's buttons come alive on that render.
        if (KnownHasStreamed.HasValue)
        {
            _hasStreamed = KnownHasStreamed.Value;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Subscribe to real-time like count updates from other clients
            LikeCountHubClient.OnLikeCountReceived += HandleLikeCountReceived;
            await LikeCountHubClient.StartAsync();

            // The pages that host these buttons are also the pages that record streams, so this is how
            // the buttons come alive part-way through a listen rather than waiting for a page reload.
            // Only in self-managed mode: a supplied instance leaves mid-listen updates to its host,
            // because hundreds of per-card handlers each re-querying on every broadcast is exactly the
            // fan-out KnownHasStreamed exists to remove.
            if (KnownHasStreamed is null)
            {
                StreamCountHubClient.OnStreamCountReceived += HandleStreamCountReceived;
                await StreamCountHubClient.StartAsync();
            }
        }

        if ((firstRender || _needsDataReload) && SongMetadataId > 0)
        {
            _needsDataReload = false;
            await LoadLikeCounts();
            await LoadUserLikeStatus();
            await InvokeAsync(StateHasChanged);
        }
        else if (_needsDataReload && SongMetadataId <= 0)
        {
            // SongMetadataId not yet populated — clear stale data and wait
            _needsDataReload = false;
            _likeCount = 0;
            _dislikeCount = 0;
            _userLikeStatus = null;
            _hasStreamed = false;
        }
    }

    private async Task LoadLikeCounts()
    {
        try
        {
            var counts = await SongLikeService.GetLikeCountsAsync(SongMetadataId);
            _likeCount = counts.likeCount;
            _dislikeCount = counts.dislikeCount;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading like counts for song {SongMetadataId}", SongMetadataId);
        }
    }

    private async Task LoadUserLikeStatus()
    {
        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user.Identity?.IsAuthenticated == true)
            {
                var userId = GetUserId(user);
                if (userId.HasValue)
                {
                    _currentUserId = userId.Value;
                    _userLikeStatus = await SongLikeService.GetUserLikeStatusAsync(userId.Value, SongMetadataId);

                    // Only self-managed instances ask the database; the library answers this for all
                    // of its cards in one query and supplies the answer as a parameter.
                    if (KnownHasStreamed is null)
                    {
                        _hasStreamed = await StreamCountService.HasUserStreamedSongAsync(userId.Value, SongMetadataId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading user like status for song {SongMetadataId}", SongMetadataId);
        }
    }

    protected async Task HandleLikeClick()
    {
        if (_isProcessing || _currentUserId == null || !CanRate)
            return;

        _isProcessing = true;
        try
        {
            var wasLiked = await SongLikeService.ToggleLikeAsync(_currentUserId.Value, SongMetadataId);

            // Update UI state
            _userLikeStatus = wasLiked ? true : null;

            // Reload counts
            await LoadLikeCounts();

            // Sync Liked Songs playlist
            await PlaylistService.SyncLikedSongsPlaylistAsync(_currentUserId.Value);

            // Notify parent component
            await OnLikeStatusChanged.InvokeAsync();
        }
        catch (LikeRequiresStreamException)
        {
            // Eligibility was stale - the buttons were enabled from a check made when the component
            // loaded. Correct the local view rather than logging an error; the server is right.
            _hasStreamed = false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error toggling like for song {SongMetadataId}", SongMetadataId);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    protected async Task HandleDislikeClick()
    {
        if (_isProcessing || _currentUserId == null || !CanRate)
            return;

        _isProcessing = true;
        try
        {
            var wasDisliked = await SongLikeService.ToggleDislikeAsync(_currentUserId.Value, SongMetadataId);
            
            // Update UI state
            _userLikeStatus = wasDisliked ? false : null;
            
            // Reload counts
            await LoadLikeCounts();
            
            // Sync Liked Songs playlist (removing liked songs when disliked)
            await PlaylistService.SyncLikedSongsPlaylistAsync(_currentUserId.Value);
            
            // Notify parent component
            await OnLikeStatusChanged.InvokeAsync();
        }
        catch (LikeRequiresStreamException)
        {
            // See HandleLikeClick - stale eligibility, not a failure.
            _hasStreamed = false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error toggling dislike for song {SongMetadataId}", SongMetadataId);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    protected string GetLikeButtonClass()
    {
        return _userLikeStatus == true ? "like-button active" : "like-button";
    }

    protected string GetDislikeButtonClass()
    {
        return _userLikeStatus == false ? "dislike-button active" : "dislike-button";
    }

    protected string GetLikeButtonTitle()
    {
        if (_userLikeStatus == true)
            return "Remove like";

        return CanRate ? "Like this song" : NotStreamedTitle;
    }

    protected string GetDislikeButtonTitle()
    {
        if (_userLikeStatus == false)
            return "Remove dislike";

        return CanRate ? "Dislike this song" : NotStreamedTitle;
    }

    private const string NotStreamedTitle = "Listen to this song before rating it";

    private void HandleLikeCountReceived(int songMetadataId, int likeCount, int dislikeCount)
    {
        if (songMetadataId != SongMetadataId) return;

        DispatchUiUpdate(() =>
        {
            _likeCount = likeCount;
            _dislikeCount = dislikeCount;
        });
    }

    /// <summary>
    /// A stream was recorded somewhere. The hub broadcasts to every client, and
    /// <see cref="IStreamCountService.IncrementStreamCountAsync"/> fires it even for streams it decided
    /// not to count, so this is a prompt to re-check rather than proof that a row now exists for us.
    ///
    /// Short-circuiting on <see cref="CanRate"/> keeps that cheap: once the buttons are live there is
    /// nothing to learn, so a popular song broadcasting steadily costs no queries on this circuit.
    /// </summary>
    private void HandleStreamCountReceived(int songMetadataId, int newCount)
    {
        if (songMetadataId != SongMetadataId || CanRate || _currentUserId == null)
            return;

        var userId = _currentUserId.Value;

        DispatchUiUpdate(async () =>
        {
            _hasStreamed = await StreamCountService.HasUserStreamedSongAsync(userId, SongMetadataId);
        });
    }

    public void Dispose()
    {
        LikeCountHubClient.OnLikeCountReceived -= HandleLikeCountReceived;
        StreamCountHubClient.OnStreamCountReceived -= HandleStreamCountReceived;
    }

    protected async Task HandleUnauthenticatedClick()
    {
        if (_loginDialog != null)
        {
            await _loginDialog.ShowAsync();
        }
        else
        {
            Logger.LogWarning("Login dialog reference is null when attempting to show dialog");
        }
    }

    protected async Task NavigateToLogin()
    {
        await HideLoginDialog();
        NavigationManager.NavigateTo("/login", forceLoad: true);
    }

    protected async Task CloseLoginDialog()
    {
        await HideLoginDialog();
    }

    private async Task HideLoginDialog()
    {
        if (_loginDialog != null)
        {
            await _loginDialog.HideAsync();
        }
    }
}
