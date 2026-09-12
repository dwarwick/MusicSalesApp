using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// What happened when an admin decided a report, so the admin screen can say so rather than
/// leaving them to infer it from the grid.
/// </summary>
/// <param name="Succeeded">False if the report was missing or had already been decided.</param>
/// <param name="SongDisabled">True if this decision is what took the song down.</param>
/// <param name="Message">One line for the admin, safe to show as-is.</param>
public record ReportResolutionResult(bool Succeeded, bool SongDisabled, string Message);

/// <summary>
/// Thrown when a creator tries to report their own song.
/// </summary>
/// <remarks>
/// A distinct type rather than another message to match on: the API's fallback for
/// <see cref="InvalidOperationException"/> is 404, and a self-report is a refusal, not a missing
/// song. It still derives from <see cref="InvalidOperationException"/> so that existing catch-alls
/// around reporting keep behaving as they did.
/// </remarks>
public class SelfReportNotAllowedException : InvalidOperationException
{
    public SelfReportNotAllowedException()
        : base("You cannot report your own song.")
    {
    }
}

public interface IReportedSongService
{
    Task<ReportedSong> ReportSongAsync(int reportingUserId, int songMetadataId, string reason);
    Task<List<ReportedSong>> GetAllReportsAsync();

    /// <summary>
    /// Records the admin's decision on a report and carries it out.
    /// <para>
    /// <paramref name="accepted"/> is the decision about the <em>report</em>, not the song: true
    /// upholds the report, which disables the song and emails the creator and the admin; false
    /// dismisses it, which leaves the song alone and emails the creator and the admin.
    /// </para>
    /// A report can only be decided once.
    /// </summary>
    /// <param name="reportId">The report being decided.</param>
    /// <param name="accepted">True to uphold the report, false to dismiss it.</param>
    /// <param name="adminUserId">The admin making the decision, for the song's status history.</param>
    /// <param name="baseUrl">Base URL for links in the emails.</param>
    Task<ReportResolutionResult> ResolveReportAsync(int reportId, bool accepted, int adminUserId, string baseUrl);

    /// <summary>
    /// Flips a decision that has already been made, for an appeal that succeeds or a call that
    /// turns out to have been wrong, and carries out the new outcome: reversing an upheld report
    /// re-enables the song, reversing a dismissal disables it. Both email the creator and the admin.
    /// </summary>
    /// <param name="reportId">The report whose decision is being reversed.</param>
    /// <param name="reason">Why it is being reversed. Required, and shown to the creator.</param>
    /// <param name="adminUserId">The admin making the change, for the song's status history.</param>
    /// <param name="baseUrl">Base URL for links in the emails.</param>
    Task<ReportResolutionResult> ReverseDecisionAsync(int reportId, string reason, int adminUserId, string baseUrl);
}
