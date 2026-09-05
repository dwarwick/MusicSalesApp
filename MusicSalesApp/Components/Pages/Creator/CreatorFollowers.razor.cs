#nullable enable
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages.Creator;

/// <summary>
/// The creator's follower list, analytics and thank-you dialog.
/// </summary>
public partial class CreatorFollowersModel : BlazorBase
{
    protected bool _loading = true;
    protected string? _errorMessage;

    protected List<CreatorPersona> _personas = [];
    protected int _selectedPersonaId;
    protected string _selectedPersonaName = string.Empty;

    protected ArtistFollowerAnalyticsDto? _analytics;
    protected List<ArtistFollowerSummaryDto> _followers = [];

    protected bool _showThankYouDialog;
    protected ArtistFollowerSummaryDto? _thankYouTarget;
    protected string _thankYouText = string.Empty;
    protected string? _thankYouError;
    protected bool _sendingThankYou;
    protected int _thankYouLength;

    private int? _creatorId;
    private bool _hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // OnAfterRenderAsync with a guard, never OnInitializedAsync: the latter can run more than
        // once per circuit, and two overlapping loads share one scoped DbContext.
        if (!firstRender || _hasLoadedData)
        {
            return;
        }

        _hasLoadedData = true;

        try
        {
            // Identity from the cookie claim. UserManager.GetUserAsync is a database round trip
            // through the circuit's single scoped context, and on a cold circuit every island
            // first-renders at once.
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var userId = GetUserId(authState.User);

            if (userId is null)
            {
                _errorMessage = "We could not identify your account.";
                return;
            }

            _creatorId = await CreatorService.GetCreatorIdForUserAsync(userId.Value);

            if (_creatorId is null)
            {
                _errorMessage = "You are not set up as a creator yet.";
                return;
            }

            _personas = (await CreatorPersonaService.GetPersonasByCreatorIdAsync(_creatorId.Value)).ToList();

            if (_personas.Count > 0)
            {
                await LoadPersonaAsync(_personas[0].Id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the creator followers page.");
            _errorMessage = "We could not load your followers. Please try again.";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task SelectPersonaAsync(int personaId)
    {
        if (personaId == _selectedPersonaId)
        {
            return;
        }

        await LoadPersonaAsync(personaId);
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadPersonaAsync(int personaId)
    {
        if (_creatorId is null)
        {
            return;
        }

        _selectedPersonaId = personaId;
        _selectedPersonaName = _personas.FirstOrDefault(persona => persona.Id == personaId)?.Name
                               ?? ArtistDisplayNames.UnknownArtist;

        _analytics = await ArtistFollowerAnalyticsService.GetAnalyticsAsync(personaId, _creatorId.Value);

        // Null means "not your persona", which cannot happen for an id that came from your own
        // persona list - but treating it as an empty list rather than crashing keeps a stale page
        // (a persona deleted in another tab) survivable.
        var followers = await ArtistFollowerDirectoryService.GetFollowersAsync(personaId, _creatorId.Value);
        _followers = followers?.ToList() ?? [];
    }

    /// <summary>
    /// The artist page for a follower who is themselves a creator, so the two can find each other.
    /// </summary>
    /// <remarks>
    /// Name-keyed because that is what /artist/{ArtistName} takes - the same URL the song cards
    /// build. Only ever reached for a follower the service already identified as an active artist.
    /// </remarks>
    protected static string ArtistUrl(string artistName) =>
        $"/artist/{Uri.EscapeDataString(artistName ?? string.Empty)}";

    protected string GetPersonaButtonClass(int personaId) =>
        personaId == _selectedPersonaId
            ? "e-btn settings-btn settings-btn-violet"
            : "e-btn settings-btn";

    protected async Task OpenThankYouDialogAsync(ArtistFollowerSummaryDto follower)
    {
        _thankYouTarget = follower;
        _thankYouText = string.Empty;
        _thankYouLength = 0;
        _thankYouError = null;
        _showThankYouDialog = true;

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Keeps the character counter honest while typing.
    /// </summary>
    /// <remarks>
    /// Counts the NORMALISED length, which is what the validator will measure. Counting the raw
    /// text instead would tell a creator they are over the limit while trailing whitespace they
    /// cannot see makes up the difference.
    /// </remarks>
    protected void OnThankYouTextInput(Syncfusion.Blazor.Inputs.InputEventArgs args)
    {
        _thankYouText = args.Value ?? string.Empty;
        _thankYouLength = ArtistMessageContentPolicy.Normalize(_thankYouText).Length;
        _thankYouError = null;
    }

    protected async Task SendThankYouAsync()
    {
        if (_thankYouTarget is null || _creatorId is null || _sendingThankYou)
        {
            return;
        }

        _sendingThankYou = true;
        _thankYouError = null;

        try
        {
            var result = await ArtistFollowerMessageService.SendThankYouAsync(
                _thankYouTarget.ArtistFollowerId, _creatorId.Value, _thankYouText);

            if (result.Succeeded)
            {
                _showThankYouDialog = false;
                _thankYouTarget = null;
                await LoadPersonaAsync(_selectedPersonaId);
                return;
            }

            // The reason is shown verbatim where the policy gave one, because "messages cannot
            // contain links" tells the sender what to change and a generic failure does not.
            _thankYouError = result.Outcome switch
            {
                ArtistThankYouOutcome.ContentRejected => result.RejectionReason,
                ArtistThankYouOutcome.AlreadyThanked => "You have already thanked this listener.",
                ArtistThankYouOutcome.NotFollowing => "This listener no longer follows you.",
                ArtistThankYouOutcome.Blocked => "This listener is not accepting messages from you.",
                ArtistThankYouOutcome.RateLimited => "You have sent a lot of thank-yous today. Try again tomorrow.",
                ArtistThankYouOutcome.ArtistUnavailable => "This persona is not currently active.",
                _ => "We could not send that message.",
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send a thank-you to follower {FollowerId}.", _thankYouTarget?.ArtistFollowerId);
            _thankYouError = "We could not send that message. Please try again.";
        }
        finally
        {
            _sendingThankYou = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void CloseThankYouDialog()
    {
        _showThankYouDialog = false;
        _thankYouTarget = null;
        _thankYouError = null;
    }
}
