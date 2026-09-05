#nullable enable
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Shared;

/// <summary>
/// "Messages from artists" inside Manage Account.
/// </summary>
public partial class ArtistMessagesSectionModel : BlazorBase
{
    /// <summary>The signed-in listener, resolved once by the host page.</summary>
    [Parameter]
    public int? UserId { get; set; }

    protected bool _loading = true;
    protected string? _statusMessage;
    protected List<ArtistMessageDto> _messages = [];

    protected bool _showReportDialog;
    protected string _reportReason = ReportReasonTypes.TermsOfUseViolation;
    private ArtistMessageDto? _reportTarget;

    private bool _hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData || UserId is null)
        {
            return;
        }

        _hasLoadedData = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (UserId is null)
        {
            return;
        }

        try
        {
            _messages = (await ArtistFollowerMessageService.GetMessagesForListenerAsync(UserId.Value)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load artist messages for user {UserId}.", UserId);
            _statusMessage = "We could not load your messages.";
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected static string ArtistUrl(string artistName) =>
        $"/artist/{Uri.EscapeDataString(artistName)}";

    protected static string SongUrl(string songTitle) =>
        $"/song/{Uri.EscapeDataString(songTitle)}";

    protected async Task MarkReadAsync(ArtistMessageDto message)
    {
        await ApplyAsync(
            () => ArtistFollowerMessageService.MarkReadAsync(message.MessageId, UserId!.Value),
            "Marked as read.");
    }

    protected async Task HideAsync(ArtistMessageDto message)
    {
        await ApplyAsync(
            () => ArtistFollowerMessageService.HideAsync(message.MessageId, UserId!.Value),
            $"Message from {message.ArtistName} hidden.");
    }

    protected async Task OpenReportDialogAsync(ArtistMessageDto message)
    {
        _reportTarget = message;
        _reportReason = ReportReasonTypes.TermsOfUseViolation;
        _showReportDialog = true;

        await InvokeAsync(StateHasChanged);
    }

    protected async Task SubmitReportAsync()
    {
        if (_reportTarget is null || UserId is null)
        {
            return;
        }

        var target = _reportTarget;
        _showReportDialog = false;
        _reportTarget = null;

        await ApplyAsync(
            () => ArtistFollowerMessageService.ReportAsync(target.MessageId, UserId.Value, _reportReason),
            "Reported. A StreamTunes admin will review it.");
    }

    protected void CloseReportDialog()
    {
        _showReportDialog = false;
        _reportTarget = null;
    }

    private async Task ApplyAsync(Func<Task<bool>> change, string successMessage)
    {
        if (UserId is null)
        {
            return;
        }

        try
        {
            var applied = await change();
            _statusMessage = applied ? successMessage : "That change could not be saved.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change an artist message for user {UserId}.", UserId);
            _statusMessage = "That change could not be saved.";
        }

        await ReloadAsync();
    }
}
