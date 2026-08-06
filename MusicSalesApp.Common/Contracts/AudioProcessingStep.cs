namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// The stages a song passes through between the creator picking a file and the song being playable.
///
/// <para>
/// The numeric values are load-bearing: progress updates travel over HTTP from the Azure Function
/// and can arrive out of order after a retry. A receiver compares ordinals and discards anything
/// lower than the step it has already recorded, so the creator's progress bar can never run
/// backwards. Renumbering these, or inserting a value out of sequence, breaks that guarantee.
/// </para>
/// </summary>
public enum AudioProcessingStep
{
    /// <summary>The browser is uploading the raw file to the staging container. Owned by the upload page.</summary>
    Staging = 0,

    /// <summary>Staged and enqueued; waiting for a Function instance to pick the message up.</summary>
    Queued = 10,

    /// <summary>The Function is pulling the source blob down to its local disk.</summary>
    Downloading = 20,

    /// <summary>FFmpeg is decoding the source to prove it is playable and to measure its duration.</summary>
    Analyzing = 30,

    /// <summary>FFmpeg is transcoding to MP3. Skipped when the source is already MP3.</summary>
    Transcoding = 40,

    /// <summary>The produced MP3 is being container-sniffed and decode-checked.</summary>
    Verifying = 50,

    /// <summary>The playback MP3 is being written back to the staging container.</summary>
    Uploading = 60,

    /// <summary>
    /// The Function is decoding the staged cover art and writing its WebP renditions straight into
    /// the song's folder in the media container. Skipped entirely when the upload has no cover art.
    ///
    /// <para>
    /// One step rather than four (download / decode / encode / upload) because every transition
    /// persists a row update in <c>MediaProcessingController.Progress</c>, and the whole block is a
    /// few seconds of work. The band carries per-rung progress instead.
    /// </para>
    /// </summary>
    RenderingArtwork = 65,

    /// <summary>The API is copying the staged blobs into the song's GUID folder in the media container.</summary>
    Copying = 70,

    /// <summary>The API is writing the SongMetadata row.</summary>
    SavingMetadata = 80,

    /// <summary>
    /// The API is generating the Open Graph sharing image. The WebP renditions are no longer built
    /// here - the Function produces them at <see cref="RenderingArtwork"/>, from the bitmap it has
    /// already decoded, rather than making the API re-download and re-decode the master.
    /// </summary>
    GeneratingArtwork = 90,

    /// <summary>Terminal: the song is live.</summary>
    Completed = 100,

    /// <summary>
    /// Terminal: processing gave up. Deliberately the highest value so a late in-flight update can
    /// never overwrite a recorded failure.
    /// </summary>
    Failed = 110
}
