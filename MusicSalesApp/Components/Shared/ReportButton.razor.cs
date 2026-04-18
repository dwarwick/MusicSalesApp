#nullable enable
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Shared;

public partial class ReportButtonModel : BlazorBase
{
    [Microsoft.AspNetCore.Components.Parameter]
    public int SongMetadataId { get; set; }

    protected SfDialog _reportDialog = default!;
    protected bool _isSubmitting;
    protected bool _submitted;
    protected string? _errorMessage;

    protected async Task ShowReportDialog()
    {
        _submitted = false;
        _isSubmitting = false;
        _errorMessage = null;
        await _reportDialog.ShowAsync();
    }

    protected async Task SubmitReport(string reason)
    {
        if (_isSubmitting || SongMetadataId <= 0) return;

        _isSubmitting = true;
        _errorMessage = null;

        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var userId = GetUserId(authState.User);

            if (!userId.HasValue)
            {
                _errorMessage = "You must be logged in to report a song.";
                return;
            }

            await ReportedSongService.ReportSongAsync(userId.Value, SongMetadataId, reason);
            _submitted = true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already reported"))
        {
            _errorMessage = "You have already reported this song.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error submitting song report for SongMetadataId {Id}", SongMetadataId);
            _errorMessage = "An error occurred while submitting your report. Please try again.";
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    protected async Task CloseDialog()
    {
        await _reportDialog.HideAsync();
    }
}
