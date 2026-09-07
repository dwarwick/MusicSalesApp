#nullable enable
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Shared;

/// <summary>
/// Follow / Following toggle for one artist, with an optional follower count.
/// </summary>
/// <remarks>
/// Shaped after <see cref="LikeDislikeButtonsModel"/>, including its sign-in dialog for anonymous
/// visitors: both are a small per-item control that a signed-out person will click, and giving them
/// two different answers to that would be the odd choice.
/// </remarks>
public partial class FollowArtistButtonModel : BlazorBase
{
    /// <summary>
    /// The persona to follow. Zero or less renders nothing - see the note in the markup.
    /// </summary>
    [Parameter]
    public int CreatorPersonaId { get; set; }

    [Parameter]
    public string PersonaName { get; set; } = ArtistDisplayNames.UnknownArtist;

    /// <summary>
    /// The song the visitor is looking at, recorded so the creator can see which music is driving
    /// follows. Ignored server-side unless the song really belongs to this persona.
    /// </summary>
    [Parameter]
    public int? SourceSongMetadataId { get; set; }

    [Parameter]
    public bool ShowFollowerCount { get; set; }

    /// <summary>
    /// Set when the host page already knows whether this visitor follows the artist, which lets
    /// the component skip its own query entirely.
    /// </summary>
    /// <remarks>
    /// The distinction is one instance versus hundreds, and it is the same trap
    /// <see cref="LikeDislikeButtonsModel.KnownHasStreamed"/> exists to avoid. On a player page
    /// there is one of these and it can afford to ask. The music library renders one per card, so
    /// self-resolving instances would mean one database round trip per card on every load. The
    /// library resolves the whole set in one query and feeds each card through this instead.
    ///
    /// Null means "find out for yourself".
    /// </remarks>
    [Parameter]
    public bool? KnownIsFollowing { get; set; }

    [Parameter]
    public EventCallback<bool> OnFollowStateChanged { get; set; }

    protected bool _isFollowing;
    protected bool _isProcessing;
    protected int _followerCount;
    protected SfDialog? _loginDialog;

    private int? _currentUserId;
    private int _loadedPersonaId;

    // The dialogs are not rendered until one is needed - see the note in the markup.
    protected bool _dialogsRequested;
    protected bool _showFollowAsDialog;
    protected IReadOnlyList<FollowAsPersonaDto> _followAsOptions = [];
    protected int? _selectedFollowAsPersonaId;

    /// <summary>
    /// The count as shown. Abbreviated past a thousand because the exact figure stops being the
    /// interesting part and starts being noise on a card.
    /// </summary>
    protected string FollowerCountText => _followerCount switch
    {
        1 => "1 follower",
        < 1000 => $"{_followerCount} followers",
        _ => $"{_followerCount / 1000.0:0.#}K followers",
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Data loading belongs here rather than in OnInitializedAsync: this component renders once
        // per card on the library page, and OnInitializedAsync can run more than once per circuit,
        // which is how concurrent DbContext use starts.
        if (CreatorPersonaId <= 0 || CreatorPersonaId == _loadedPersonaId)
        {
            return;
        }

        _loadedPersonaId = CreatorPersonaId;

        try
        {
            await LoadStateAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load follow state for persona {PersonaId}.", CreatorPersonaId);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    protected override void OnParametersSet()
    {
        // Supplied state stays live across parent re-renders, so a card whose artist is followed
        // from another surface comes up correct on the next render rather than staying stale.
        if (KnownIsFollowing.HasValue)
        {
            _isFollowing = KnownIsFollowing.Value;
        }
    }

    private async Task LoadStateAsync()
    {
        if (ShowFollowerCount)
        {
            _followerCount = await ArtistFollowService.GetFollowerCountAsync(CreatorPersonaId);
        }

        // Identity from the cookie claim, never UserManager.GetUserAsync. That is a round trip
        // through the circuit's single scoped DbContext, and on a cold circuit every island first
        // renders at once - two in-flight calls throw "a second operation was started".
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _currentUserId = GetUserId(authState.User);

        // A supplied value is authoritative - asking again would be the per-card query this
        // parameter exists to avoid.
        if (_currentUserId.HasValue && !KnownIsFollowing.HasValue)
        {
            _isFollowing = await ArtistFollowService.IsFollowingAsync(CreatorPersonaId, _currentUserId.Value);
        }
    }

    protected async Task HandleToggleAsync()
    {
        if (_isProcessing || CreatorPersonaId <= 0)
        {
            return;
        }

        if (!_currentUserId.HasValue)
        {
            // Authorized but no id claim: the circuit is in a state this component cannot act on.
            await ShowLoginDialogAsync();
            return;
        }

        var wanted = !_isFollowing;

        // Only when STARTING to follow, and only for a listener who has consented, is there an
        // identity to choose. Unfollowing never asks anything.
        if (wanted)
        {
            try
            {
                var options = await ArtistFollowService.GetFollowAsOptionsAsync(_currentUserId.Value);

                if (options.NeedsChoice)
                {
                    _followAsOptions = options.Personas;

                    // Anonymous is the pre-selected option even here. Consenting in general is not
                    // consenting to every artist, and the safe answer should be the default one.
                    _selectedFollowAsPersonaId = null;
                    _dialogsRequested = true;
                    _showFollowAsDialog = true;

                    await InvokeAsync(StateHasChanged);
                    return;
                }

                // One persona, or none, or no consent - nothing to ask.
                await ApplyFollowStateAsync(true, options.DefaultPersonaId);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to read follow-as options for persona {PersonaId}.", CreatorPersonaId);
                return;
            }
        }

        await ApplyFollowStateAsync(false, null);
    }

    /// <summary>
    /// The listener picked an identity in the dialog.
    /// </summary>
    protected async Task ConfirmFollowAsAsync()
    {
        _showFollowAsDialog = false;
        await ApplyFollowStateAsync(true, _selectedFollowAsPersonaId);
    }

    protected void CancelFollowAs()
    {
        // Cancel means cancel: no follow is created, because the listener never settled on how
        // they wanted to appear.
        _showFollowAsDialog = false;
        _selectedFollowAsPersonaId = null;
    }

    private async Task ApplyFollowStateAsync(bool wanted, int? followAsPersonaId)
    {
        if (_isProcessing || _currentUserId is null)
        {
            return;
        }

        _isProcessing = true;

        try
        {
            var outcome = await ArtistFollowService.SetFollowStateAsync(
                CreatorPersonaId, _currentUserId.Value, wanted, SourceSongMetadataId, followAsPersonaId);

            // Set from the outcome rather than from what was asked for. A refusal - the artist is
            // gone, or the listener blocked them from another tab - must leave the button showing
            // the truth, not the click.
            _isFollowing = outcome switch
            {
                ArtistFollowOutcome.Followed or ArtistFollowOutcome.AlreadyFollowing => true,
                ArtistFollowOutcome.Unfollowed or ArtistFollowOutcome.NotFollowing => false,
                _ => _isFollowing,
            };

            if (ShowFollowerCount && outcome is ArtistFollowOutcome.Followed or ArtistFollowOutcome.Unfollowed)
            {
                _followerCount = Math.Max(0, _followerCount + (outcome == ArtistFollowOutcome.Followed ? 1 : -1));
            }

            if (OnFollowStateChanged.HasDelegate)
            {
                await OnFollowStateChanged.InvokeAsync(_isFollowing);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change follow state for persona {PersonaId}.", CreatorPersonaId);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    /// <summary>
    /// The bell. Filled when following, outline when not - the class carries the colour, the
    /// markup swaps the glyph.
    /// </summary>
    protected string GetBellClass() =>
        _isFollowing ? "follow-artist-bell is-following" : "follow-artist-bell";

    /// <summary>
    /// Doubles as the title and the aria-label. A bell with no text is meaningless to a screen
    /// reader and ambiguous on hover, so this is not decoration.
    /// </summary>
    protected string GetButtonTitle() =>
        _isFollowing
            ? $"Following {PersonaName} - click to stop"
            : $"Follow {PersonaName} for new releases";

    protected async Task ShowLoginDialogAsync()
    {
        // The dialogs are absent from the render tree until now, so ask for them and let the
        // component render once before reaching for the reference.
        if (!_dialogsRequested)
        {
            _dialogsRequested = true;
            await InvokeAsync(StateHasChanged);
        }

        if (_loginDialog is not null)
        {
            await _loginDialog.ShowAsync();
        }
    }

    protected async Task NavigateToLoginAsync()
    {
        await CloseLoginDialogAsync();
        NavigationManager.NavigateTo(AppPageRoutes.Login, forceLoad: true);
    }

    protected async Task CloseLoginDialogAsync()
    {
        if (_loginDialog is not null)
        {
            await _loginDialog.HideAsync();
        }
    }
}
