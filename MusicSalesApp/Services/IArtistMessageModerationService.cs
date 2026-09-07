#nullable enable
namespace MusicSalesApp.Services;

/// <summary>
/// The admin review queue for reported artist messages.
/// </summary>
/// <remarks>
/// A thank-you is the first creator-authored free text StreamTunes shows to another user, so this
/// ships with the feature rather than after it. Admins see the message text, the persona and the
/// reason; the reporting listener stays anonymous here too, identified only by the pseudonym they
/// already wear for that artist.
/// </remarks>
public interface IArtistMessageModerationService
{
    /// <summary>
    /// Reported messages, unresolved first, then most recently reported.
    /// </summary>
    Task<IReadOnlyList<ReportedArtistMessageDto>> GetReportedMessagesAsync(
        bool includeResolved = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a report. <paramref name="accepted"/> true upholds it and hides the message from the
    /// listener; false rejects it and leaves the message visible. Matches
    /// <c>IReportedSongService.ResolveReportAsync</c>.
    /// </summary>
    Task<bool> ResolveReportAsync(int messageId, bool accepted, CancellationToken cancellationToken = default);
}

/// <summary>
/// A reported message as an admin sees it.
/// </summary>
public sealed record ReportedArtistMessageDto(
    int MessageId,
    int CreatorPersonaId,
    string ArtistName,
    string ReporterDisplayName,
    string MessageText,
    string? ReportReason,
    DateTime? ReportedAtUtc,
    DateTime CreatedDateUtc,
    DateTime? ModerationResolvedAtUtc,
    bool? ModerationAccepted);
