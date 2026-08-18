namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for SignalR method names used by hub publishers (SendAsync) and
/// subscribers (HubConnection.On). Always use these constants instead of inline strings to
/// avoid silent mismatches between writers and readers.
/// </summary>
public static class SignalRMethodNames
{
    /// <summary>
    /// Broadcast by webhook controllers when a user-facing webhook status changes.
    /// Payload: WebhookStatusMessage.
    /// </summary>
    public const string ReceiveWebhookStatus = "ReceiveWebhookStatus";

    /// <summary>
    /// Sent to a connected user when their pending admin messages changed and the
    /// client should refresh its queue.
    /// </summary>
    public const string ReceiveAdminMessageRefresh = "ReceiveAdminMessageRefresh";

    /// <summary>
    /// Broadcast by the server when site maintenance window settings change so connected
    /// clients can refresh their maintenance banner/dialog in real time.
    /// </summary>
    public const string ReceiveMaintenanceUpdate = "ReceiveMaintenanceUpdate";

    /// <summary>
    /// Broadcast by StreamCountService after a song's stream count is incremented.
    /// Payload: (int songMetadataId, int newCount)
    /// </summary>
    public const string ReceiveStreamCountUpdate = "ReceiveStreamCountUpdate";

    /// <summary>
    /// Broadcast by SongLikeService after a song's like/dislike counts change.
    /// Payload: (int songMetadataId, int likeCount, int dislikeCount)
    /// </summary>
    public const string ReceiveLikeCountUpdate = "ReceiveLikeCountUpdate";

    /// <summary>
    /// Sent to one creator's group as their upload moves through transcoding and assembly.
    /// Payload: AudioProcessingProgress.
    /// </summary>
    public const string ReceiveUploadProgress = "ReceiveUploadProgress";

    /// <summary>
    /// Sent to one creator's group while their dropped cover art is being read and paired, before
    /// any song job exists. Payload: CoverArtMatchProgress.
    /// </summary>
    public const string ReceiveCoverArtMatchProgress = "ReceiveCoverArtMatchProgress";

    /// <summary>
    /// Sent to one creator's group once a cover-art batch has been paired. Payload:
    /// CoverArtMatchResult. This is what releases the upload page's waiting task.
    /// </summary>
    public const string ReceiveCoverArtMatchResult = "ReceiveCoverArtMatchResult";

    /// <summary>
    /// Sent to one creator's group as their song's lyrics are aligned to its audio. Payload:
    /// LyricsAlignmentProgress.
    ///
    /// <para>
    /// Rides the same hub and the same per-creator group as upload progress rather than getting its
    /// own, for the reason CoverArtMatchNotifier already gives: a second hub would have to solve
    /// authentication and group membership again for nothing.
    /// </para>
    /// </summary>
    public const string ReceiveLyricsAlignmentProgress = "ReceiveLyricsAlignmentProgress";
}

/// <summary>
/// Group naming for hubs that fan out to a subset of connections rather than broadcasting.
/// </summary>
public static class SignalRGroups
{
    /// <summary>
    /// The group carrying one creator's upload progress.
    ///
    /// <para>
    /// Callers must derive <paramref name="creatorId"/> from the connected user's own identity,
    /// never from a value the client supplied - otherwise a creator could join another creator's
    /// group and watch their uploads.
    /// </para>
    /// </summary>
    public static string ForCreator(int creatorId) => $"creator-{creatorId}";
}
