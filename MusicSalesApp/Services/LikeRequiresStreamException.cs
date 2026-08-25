namespace MusicSalesApp.Services;

/// <summary>
/// Thrown when a user tries to set a like or dislike on a song they have never streamed.
///
/// Setting an opinion requires at least one <c>SongStreams</c> row for that user and song; clearing one
/// never does, so a rating made before this rule stays retractable. Callers map this to a 403 with a body
/// rather than a generic failure: the mobile offline queue drops non-retryable statuses permanently, so
/// "you are not allowed to do this" has to be distinguishable from "try again later".
/// </summary>
public sealed class LikeRequiresStreamException : InvalidOperationException
{
    public LikeRequiresStreamException(int songMetadataId, int userId)
        : base($"User {userId} has not streamed song metadata {songMetadataId} and cannot rate it.")
    {
        SongMetadataId = songMetadataId;
        UserId = userId;
    }

    public int SongMetadataId { get; }

    public int UserId { get; }
}
