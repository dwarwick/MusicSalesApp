namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// The stages one lyrics-alignment attempt passes through, from the creator pressing Submit until
/// the word timings are stored.
///
/// <para>
/// Deliberately a separate enum from <see cref="AudioProcessingStep"/> rather than extra members on
/// it, for the same reason <see cref="CoverArtMatchStep"/> is separate: that one's contract is
/// "ordinals compared against a <c>SongUploadJob.Step</c> column", and a lyrics attempt is compared
/// against a <c>LyricsAlignmentJob.Step</c> column instead. Keeping the types apart makes it
/// impossible to feed one pipeline's step into the other's monotonicity check by accident.
/// </para>
///
/// <para>
/// Values are spaced in tens so a stage can be inserted later without renumbering. Renumbering, or
/// inserting out of sequence, breaks the monotonicity guarantee that
/// <see cref="LyricsAlignmentProgressCalculator.IsAdvance"/> depends on.
/// </para>
/// </summary>
public enum LyricsAlignmentStep
{
    /// <summary>The web app is writing the pasted lyrics to the media container.</summary>
    Submitted = 0,

    /// <summary>
    /// The orchestration has been started and is waiting for a Flex instance. This can be minutes
    /// rather than seconds: a cold instance has to import torch and pull model weights off the
    /// Azure Files mount before the first activity runs.
    /// </summary>
    Queued = 10,

    /// <summary>FFmpeg is resampling to 16 kHz mono, normalising loudness and detecting silence.</summary>
    Preparing = 20,

    /// <summary>
    /// Demucs is separating the vocal stem. By far the longest stage — single-digit minutes for a
    /// normal track, tens of minutes for a long one on a cold CPU instance.
    /// </summary>
    SeparatingVocals = 30,

    /// <summary>Forced alignment of the artist's words against the isolated vocal.</summary>
    Aligning = 60,

    /// <summary>Mapping aligner tokens back onto the artist's own lines and words.</summary>
    Mapping = 80,

    /// <summary>Writing the timing JSON and the LRC to the staging container.</summary>
    WritingOutputs = 90,

    /// <summary>The web app is copying those outputs from staging into the media container.</summary>
    Copying = 93,

    /// <summary>The web app is writing the <c>SongLyrics</c> row.</summary>
    Saving = 96,

    /// <summary>Terminal: timings are stored.</summary>
    Completed = 100,

    /// <summary>
    /// Terminal: the attempt gave up. Highest value for the same reason
    /// <see cref="AudioProcessingStep.Failed"/> is — a late in-flight progress ping must never
    /// overwrite a recorded failure.
    /// </summary>
    Failed = 110
}
