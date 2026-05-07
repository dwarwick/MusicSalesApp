namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for SignalR method names used by hub publishers (SendAsync) and
/// subscribers (HubConnection.On). Always use these constants instead of inline strings to
/// avoid silent mismatches between writers and readers.
/// </summary>
public static class SignalRMethodNames
{
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
}
