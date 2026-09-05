#nullable enable
using Hangfire;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Artist-to-follower messages: sending the one thank-you a creator gets per follower, and the
/// listener's side of reading, hiding and reporting it.
/// </summary>
/// <remarks>
/// Listeners cannot reply in version 1. That is a deliberate limit rather than an unfinished
/// feature: an artist acknowledging support is a small, bounded trust surface, whereas a two-way
/// channel needs its own moderation, muting and abuse handling on both ends.
/// </remarks>
public interface IArtistFollowerMessageService
{
    /// <summary>
    /// Sends a creator's thank-you to one of their followers.
    /// </summary>
    /// <param name="artistFollowerId">The follow relationship being replied to.</param>
    /// <param name="creatorId">
    /// The signed-in creator. Checked against the persona's owner - a creator cannot message
    /// someone else's follower.
    /// </param>
    /// <param name="messageText">
    /// Raw text as typed. It is normalised and validated here, so a caller that skipped the
    /// client-side check cannot get anything past this.
    /// </param>
    Task<ArtistThankYouResult> SendThankYouAsync(
        int artistFollowerId,
        int creatorId,
        string messageText,
        int? relatedSongMetadataId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this creator has any thank-yous left today, used to disable the button before it is
    /// pressed rather than explaining a refusal afterwards.
    /// </summary>
    Task<int> GetRemainingDailyThankYousAsync(
        int creatorPersonaId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The listener's messages, newest first, excluding any they have hidden.
    /// </summary>
    Task<IReadOnlyList<ArtistMessageDto>> GetMessagesForListenerAsync(
        int listenerUserId,
        CancellationToken cancellationToken = default);

    Task<int> GetUnreadMessageCountAsync(int listenerUserId, CancellationToken cancellationToken = default);

    Task<bool> MarkReadAsync(int messageId, int listenerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hides a message from the listener's own list. Not a delete - a reported message has to
    /// survive for an admin to look at.
    /// </summary>
    Task<bool> HideAsync(int messageId, int listenerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flags a message for admin review. <paramref name="reason"/> must be one of
    /// <c>ReportReasonTypes</c>.
    /// </summary>
    Task<bool> ReportAsync(int messageId, int listenerUserId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emails the messages that have not been emailed yet, to listeners who want them.
    /// The every-15-minutes Hangfire job.
    /// </summary>
    /// <returns>The number of emails successfully sent.</returns>
    // Hangfire resolves filters from Job.Method, which for an interface-registered job is
    // this declaration. The same attribute on the implementation is silently ignored.
    //
    // The lock matters more here than on a daily job: this one runs every 15 minutes but sleeps
    // 5 seconds between sends to stay out of spam filters, so a busy day genuinely can overrun
    // its own interval. AutomaticRetry(0) goes with it because DisableConcurrentExecution throws
    // on lock timeout rather than swallowing it - without a policy, one harmless overlap becomes
    // ten retries. Skipping a run costs nothing; the next one picks up the same unsent rows.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    Task<int> SendPendingEmailsAsync();
}
