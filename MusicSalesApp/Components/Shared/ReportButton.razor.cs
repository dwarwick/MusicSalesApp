#nullable enable
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Shared;

public partial class ReportButtonModel : BlazorBase
{
    [Microsoft.AspNetCore.Components.Parameter]
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Whether the song belongs to the person looking at it. When true the button is not rendered:
    /// the server refuses a self-report either way, so offering it would only produce an error
    /// message where there should not have been a control.
    /// </summary>
    /// <remarks>
    /// Passed in rather than worked out here. Each surface already knows - the library from the
    /// creator-user-id map it builds for the stream guard, the player from the auth context it
    /// loads once - and asking again per card would be a query per row.
    /// </remarks>
    [Microsoft.AspNetCore.Components.Parameter]
    public bool IsOwnSong { get; set; }

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
        catch (SelfReportNotAllowedException)
        {
            // The button is hidden on your own songs, so this is only reachable from a page that
            // was already open when the song changed hands. Saying so beats a generic failure.
            _errorMessage = "You cannot report your own song.";
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
