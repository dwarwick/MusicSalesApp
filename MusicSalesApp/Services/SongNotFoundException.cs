namespace MusicSalesApp.Services;

/// <summary>
/// Thrown when an operation references a song that no longer exists.
///
/// Distinguishing this from a general write failure matters for the mobile offline like/dislike queue:
/// the client retries a 500 indefinitely, so a song deleted while an intent was queued would otherwise
/// jam that queue forever. Callers map this to a 4xx the client can act on.
/// </summary>
public sealed class SongNotFoundException : InvalidOperationException
{
    public SongNotFoundException(int songMetadataId, Exception innerException = null)
        : base($"Song metadata {songMetadataId} does not exist.", innerException)
    {
        SongMetadataId = songMetadataId;
    }

    public int SongMetadataId { get; }
}
