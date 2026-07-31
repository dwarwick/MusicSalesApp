#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Short, stable reasons a rendition could not be produced. Recorded per item by the admin backfill
/// so a run that partly failed can be triaged without reading the application log.
/// </summary>
public static class ImageVariantFailureCodes
{
    /// <summary>The blob path was blank, so there was nothing to derive renditions from.</summary>
    public const string NoSourcePath = "no_source_path";

    /// <summary>The master blob is missing or empty. Usually a database row pointing at a deleted blob.</summary>
    public const string SourceMissing = "source_missing";

    /// <summary>The master downloaded but SkiaSharp could not decode it — truncated, or not actually an image.</summary>
    public const string DecodeFailed = "decode_failed";

    /// <summary>The source decoded but every rendition failed to encode.</summary>
    public const string EncodeFailed = "encode_failed";

    /// <summary>Writing a rendition to blob storage failed.</summary>
    public const string UploadFailed = "upload_failed";

    /// <summary>The database row disappeared between being enumerated and being processed.</summary>
    public const string EntityMissing = "entity_missing";

    /// <summary>
    /// The renditions were written to storage, but recording their widths against the row failed.
    /// The blobs exist and are correct; the row still reports none, so a later run regenerates them.
    /// </summary>
    public const string PersistFailed = "persist_failed";

    /// <summary>The run was cancelled while this item was in flight.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Anything that threw outside the stages above. Distinct from the specific codes so that a
    /// genuinely unexpected fault is not filed under whichever stage happens to be listed last.
    /// </summary>
    public const string Unexpected = "unexpected";
}
