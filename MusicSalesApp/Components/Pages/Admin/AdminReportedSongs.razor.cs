#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Helpers;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages.Admin;

public partial class AdminReportedSongsModel : BlazorBase
{
    protected bool _loading = true;
    protected string? _error;
    protected string? _statusMessage;
    protected List<ReportedSongViewModel> _reports = [];
    protected SfGrid<ReportedSongViewModel>? _grid;
    private bool _hasLoadedData;

    // Confirmation state. Both decisions are carried out, not merely recorded - one disables the
    // song and mails the creator - so the dialog restates the one being taken before it is saved.
    protected bool _showConfirmDialog;
    protected bool _pendingAccept;
    protected int? _pendingReportId;
    protected string _pendingSongTitle = string.Empty;
    protected string _pendingReason = string.Empty;
    protected int? _busyReportId;

    // Reversal state. The reason is asked for rather than assumed, because it is the only thing
    // the creator is told about why their song came back or went away.
    //
    // The cap leaves room for the sentence the service wraps it in: both columns it lands in are
    // MaxLength(1000), and going over throws inside the status service, which the admin would see
    // as "could not be changed, please try again" - and every retry would fail the same way.
    protected const int ReverseReasonMaxLength = 800;

    protected bool _showReverseDialog;
    protected bool _reverseWasUpheld;
    protected string _reverseReason = string.Empty;
    protected string? _reverseValidationError;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadReportsAsync();
            }
            catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
            {
                // The visitor left, or the circuit dropped, while this was still awaiting.
                // Nothing is wrong and there is nobody to tell, so it must not reach the
                // Error sink - that is what emailed the admin five times on 2026-09-02.
                Logger.LogDebug(ex, "Error loading reported songs");
            }
            catch (Exception ex)
            {
                _error = $"Error loading reports: {ex.Message}";
                Logger.LogError(ex, "Error loading reported songs");
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task LoadReportsAsync()
    {
        var reports = await ReportedSongService.GetAllReportsAsync();

        _reports = reports.Select(r => new ReportedSongViewModel
        {
            Id = r.Id,
            SongTitle = r.SongMetadata == null
                ? "Unknown"
                : SongTitleHelper.GetEffectiveTitle(
                    r.SongMetadata.SongTitle,
                    r.SongMetadata.Mp3BlobPath,
                    r.SongMetadata.BlobPath),
            CreatorEmail = GetCreatorEmail(r),
            ReporterEmail = r.ReportingUser?.Email ?? "Unknown",
            Reason = r.Reason,
            CreatedAt = r.CreatedAt,
            ResolutionDateTime = r.ResolutionDateTime,
            // ResolutionAccepted is stored as "did the admin accept the report", so true is the
            // finding against the song. The column says so in words rather than making the reader
            // work out which side "Accepted" was about.
            ResolutionStatus = r.ResolutionAccepted switch
            {
                true => ReportDecisions.Upheld,
                false => ReportDecisions.Dismissed,
                null => ReportDecisions.Pending
            },
            SongStatus = (r.SongMetadata?.IsEnabled ?? false) ? "Active" : "Inactive"
        }).ToList();
    }

    private static string GetCreatorEmail(ReportedSong report)
    {
        // Creator navigation: SongMetadata -> Creator -> User
        // The Creator entity has a UserId FK, but the User nav property
        // may not be loaded. We need a different approach.
        // Since GetAllReportsAsync includes Creator, check if it has user info.
        return report.SongMetadata?.Creator?.User?.Email ?? "Unknown";
    }

    /// <summary>
    /// The admin acting, and the site they are acting on. Both go into the song's status history
    /// and into the links in the creator's email, so they are read fresh for each decision rather
    /// than cached - an admin session can outlive the page.
    /// </summary>
    private async Task<(int AdminUserId, string BaseUrl)> GetActingAdminAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var appUser = await UserManager.GetUserAsync(authState.User);
        return (appUser?.Id ?? 0, NavigationManager.BaseUri);
    }

    protected void PromptResolve(ReportedSongViewModel report, bool accepted)
    {
        _statusMessage = null;
        _pendingReportId = report.Id;
        _pendingAccept = accepted;
        _pendingSongTitle = report.SongTitle;
        _pendingReason = report.Reason;
        _showConfirmDialog = true;
    }

    protected void CancelResolve()
    {
        _showConfirmDialog = false;
        _pendingReportId = null;
    }

    protected async Task ConfirmResolveAsync()
    {
        if (_pendingReportId is not int reportId)
            return;

        _showConfirmDialog = false;
        _busyReportId = reportId;
        _pendingReportId = null;

        try
        {
            var (adminUserId, baseUrl) = await GetActingAdminAsync();
            var result = await ReportedSongService.ResolveReportAsync(reportId, _pendingAccept, adminUserId, baseUrl);
            _statusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _statusMessage = "Something went wrong deciding that report. It has been left as it was.";
            Logger.LogError(ex, "Error resolving report {ReportId}", reportId);
        }
        finally
        {
            await RefreshAfterDecisionAsync(reportId);
        }
    }

    protected void PromptReverse(ReportedSongViewModel report)
    {
        _statusMessage = null;
        _reverseValidationError = null;
        _reverseReason = string.Empty;
        _pendingReportId = report.Id;
        _pendingSongTitle = report.SongTitle;
        _pendingReason = report.Reason;
        _reverseWasUpheld = report.ResolutionStatus == ReportDecisions.Upheld;
        _showReverseDialog = true;
    }

    protected void CancelReverse()
    {
        _showReverseDialog = false;
        _pendingReportId = null;
        _reverseValidationError = null;
    }

    protected async Task ConfirmReverseAsync()
    {
        if (_pendingReportId is not int reportId)
            return;

        if (string.IsNullOrWhiteSpace(_reverseReason))
        {
            _reverseValidationError = "A reason is required - the creator is shown it.";
            return;
        }

        if (_reverseReason.Trim().Length > ReverseReasonMaxLength)
        {
            _reverseValidationError =
                $"That reason is too long. Keep it to {ReverseReasonMaxLength} characters - it is stored with the song's status history.";
            return;
        }

        _showReverseDialog = false;
        _busyReportId = reportId;
        _pendingReportId = null;

        try
        {
            var (adminUserId, baseUrl) = await GetActingAdminAsync();
            var result = await ReportedSongService.ReverseDecisionAsync(reportId, _reverseReason.Trim(), adminUserId, baseUrl);
            _statusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _statusMessage = "Something went wrong reversing that decision. It has been left as it was.";
            Logger.LogError(ex, "Error reversing decision on report {ReportId}", reportId);
        }
        finally
        {
            _reverseReason = string.Empty;
            await RefreshAfterDecisionAsync(reportId);
        }
    }

    /// <summary>
    /// Reloads the grid without touching the message the decision produced. The decision has
    /// already happened - the song is disabled, the creator has been emailed - so a failure to
    /// redraw the list must not overwrite that with "it has been left as it was", which would send
    /// the admin looking for a takedown that did in fact happen.
    /// </summary>
    private async Task RefreshAfterDecisionAsync(int reportId)
    {
        try
        {
            await LoadReportsAsync();
        }
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            Logger.LogDebug(ex, "Circuit dropped while refreshing reports after deciding {ReportId}", reportId);
        }
        catch (Exception ex)
        {
            _statusMessage = $"{_statusMessage} (The list below could not be refreshed - reload the page to see it.)";
            Logger.LogError(ex, "Error refreshing reports after deciding {ReportId}", reportId);
        }
        finally
        {
            _busyReportId = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// What the admin decided about a report, in the reader's words rather than the column's.
    /// These are display strings only - the stored value is <see cref="ReportedSong.ResolutionAccepted"/>.
    /// </summary>
    protected static class ReportDecisions
    {
        public const string Upheld = "Upheld - in violation";
        public const string Dismissed = "Dismissed - no violation";
        public const string Pending = "Pending";
    }

    protected class ReportedSongViewModel
    {
        public int Id { get; set; }
        public string SongTitle { get; set; } = string.Empty;
        public string CreatorEmail { get; set; } = string.Empty;
        public string ReporterEmail { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? ResolutionDateTime { get; set; }
        public string ResolutionStatus { get; set; } = ReportDecisions.Pending;
        public string SongStatus { get; set; } = "Active";
    }
}
