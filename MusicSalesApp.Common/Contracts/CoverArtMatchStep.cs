namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// The stages a batch of candidate cover art passes through while it is being paired with the audio
/// files uploaded alongside it.
///
/// <para>
/// Deliberately a separate enum from <see cref="AudioProcessingStep"/> rather than extra members on
/// it. That one's contract is "ordinals compared against a <c>SongUploadJob.Step</c> column"; a match
/// batch has no job row, and no song exists yet for its values to mean anything against. Keeping the
/// types apart makes it impossible to feed a match step into the song pipeline's monotonicity check
/// by accident.
/// </para>
/// </summary>
public enum CoverArtMatchStep
{
    /// <summary>The web server is uploading the candidate images to the staging container.</summary>
    Staging = 0,

    /// <summary>Staged and enqueued; waiting for a Function instance to pick the message up.</summary>
    Queued = 10,

    /// <summary>The Function is pulling the batch's images down.</summary>
    Reading = 20,

    /// <summary>Reading the text off each image, one vision call per image.</summary>
    ReadingText = 30,

    /// <summary>The single pairing call that matches every image against every audio filename.</summary>
    Pairing = 40,

    /// <summary>Terminal: a pairing was produced.</summary>
    Completed = 100,

    /// <summary>
    /// Terminal: matching gave up. Highest value for the same reason
    /// <see cref="AudioProcessingStep.Failed"/> is - a late in-flight update must never overwrite a
    /// recorded failure.
    /// </summary>
    Failed = 110
}
