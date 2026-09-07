#nullable enable
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Pages.Admin;

/// <summary>
/// The admin review queue for reported artist messages, shaped after AdminReportedSongs.
/// </summary>
public partial class AdminArtistMessagesModel : BlazorBase
{
    protected bool _loading = true;
    protected bool _includeResolved;
    protected string? _statusMessage;
    protected int? _resolvingMessageId;
    protected List<ReportedArtistMessageDto> _reports = [];

    private bool _hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData)
        {
            return;
        }

        _hasLoadedData = true;
        await ReloadAsync();
    }

    protected async Task ToggleResolvedAsync()
    {
        _includeResolved = !_includeResolved;
        await ReloadAsync();
    }

    protected async Task ResolveAsync(int messageId, bool accepted)
    {
        if (_resolvingMessageId is not null)
        {
            return;
        }

        _resolvingMessageId = messageId;

        try
        {
            var resolved = await ArtistMessageModerationService.ResolveReportAsync(messageId, accepted);

            // Directional, not a bare "Saved": the two outcomes do opposite things to the
            // listener's copy of the message, and an admin needs to see which one took effect.
            _statusMessage = resolved
                ? accepted
                    ? "Report upheld. The message is now hidden from the listener."
                    : "Report rejected. The message stays visible to the listener."
                : "That report could not be resolved.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to resolve artist message report {MessageId}.", messageId);
            _statusMessage = "That report could not be resolved.";
        }
        finally
        {
            _resolvingMessageId = null;
            await ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            _reports = (await ArtistMessageModerationService.GetReportedMessagesAsync(_includeResolved)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load reported artist messages.");
            _statusMessage = "We could not load the reports.";
            _reports = [];
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}
