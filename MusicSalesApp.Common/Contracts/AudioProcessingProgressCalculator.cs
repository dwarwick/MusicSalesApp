namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// Maps a step, plus optional progress within that step, onto a single 0-100 figure spanning the
/// whole upload pipeline.
///
/// <para>
/// The creator watches one bar per song from the moment the browser accepts the file until the song
/// is live, but the work behind it is split across three processes: the upload page stages the
/// bytes, the Azure Function transcodes, and the API assembles. This class is the one place the
/// bands are defined, so those three can never disagree about what "60%" means.
/// </para>
///
/// <para>
/// The bands are weighted by how long each stage actually takes, not evenly. Transcoding dominates,
/// so it gets the widest band; writing a metadata row is near-instant and gets a sliver. Only
/// <see cref="AudioProcessingStep.Staging"/> and <see cref="AudioProcessingStep.Transcoding"/>
/// report meaningful within-step progress — everything else jumps to its band start and then moves
/// on, which is honest about the pipeline rather than animating a fake crawl.
/// </para>
/// </summary>
public static class AudioProcessingProgressCalculator
{
    /// <summary>
    /// Band start/end for each step, in overall percent. Ends are exclusive of the next band's
    /// start only in the sense that a step at 100% within-step progress reaches its own end value;
    /// the next step then begins there, so the sequence is continuous and monotonic.
    /// </summary>
    private static readonly IReadOnlyDictionary<AudioProcessingStep, (double Start, double End)> Bands =
        new Dictionary<AudioProcessingStep, (double, double)>
        {
            [AudioProcessingStep.Staging] = (0d, 15d),
            [AudioProcessingStep.Queued] = (15d, 15d),
            [AudioProcessingStep.Downloading] = (15d, 20d),
            [AudioProcessingStep.Analyzing] = (20d, 35d),
            [AudioProcessingStep.Transcoding] = (35d, 70d),
            [AudioProcessingStep.Verifying] = (70d, 75d),
            [AudioProcessingStep.Uploading] = (75d, 80d),

            // The widest of the tail bands, because it is the most work in them: one download, one
            // decode, then a resample, an encode and a cross-region PUT per rung. Its width is also
            // load-bearing rather than cosmetic - ProgressReporter gates on whole percents, so a
            // band narrower than one point per rung would swallow every per-rung update.
            [AudioProcessingStep.RenderingArtwork] = (80d, 88d),

            [AudioProcessingStep.Copying] = (88d, 93d),
            [AudioProcessingStep.SavingMetadata] = (93d, 96d),

            // Only the Open Graph share image now; the renditions moved to RenderingArtwork. Kept
            // non-trivial anyway because it is the last band before Completed, and a bar that pauses
            // at 96 reads better than one that pauses at 99.
            [AudioProcessingStep.GeneratingArtwork] = (96d, 100d),
            [AudioProcessingStep.Completed] = (100d, 100d),

            // A failed song's bar stops where it was rather than snapping to 0 or 100, so the
            // creator can see how far it got. ToOverallPercent is not consulted for Failed - the
            // last known percent is retained - but the entry keeps the table total.
            [AudioProcessingStep.Failed] = (0d, 100d)
        };

    /// <summary>
    /// The overall percent for <paramref name="step"/>, optionally advanced by
    /// <paramref name="stepPercent"/> (0-100) through that step's own band.
    /// </summary>
    public static double ToOverallPercent(AudioProcessingStep step, double? stepPercent = null)
    {
        if (!Bands.TryGetValue(step, out var band))
        {
            return 0d;
        }

        if (!stepPercent.HasValue)
        {
            return band.Start;
        }

        var clamped = Math.Clamp(stepPercent.Value, 0d, 100d);
        return band.Start + ((band.End - band.Start) * (clamped / 100d));
    }

    /// <summary>The overall percent at which <paramref name="step"/> begins.</summary>
    public static double BandStart(AudioProcessingStep step)
        => Bands.TryGetValue(step, out var band) ? band.Start : 0d;

    /// <summary>The overall percent at which <paramref name="step"/> finishes.</summary>
    public static double BandEnd(AudioProcessingStep step)
        => Bands.TryGetValue(step, out var band) ? band.End : 0d;

    /// <summary>
    /// Whether <paramref name="incoming"/> should replace <paramref name="current"/>.
    ///
    /// <para>
    /// Progress posts are fire-and-forget HTTP from the Function and can arrive out of order, and
    /// a queue retry can replay an earlier step entirely. Anything at or below the recorded step is
    /// dropped, which is what stops the bar from stuttering backwards.
    /// </para>
    /// </summary>
    public static bool IsAdvance(AudioProcessingStep current, AudioProcessingStep incoming)
        => incoming > current;

    /// <summary>Whether <paramref name="step"/> is one a song can never move on from.</summary>
    public static bool IsTerminal(AudioProcessingStep step)
        => step is AudioProcessingStep.Completed or AudioProcessingStep.Failed;

    /// <summary>
    /// Share of the batch bar given to receiving the creator's files into the server, before any
    /// song exists.
    ///
    /// <para>
    /// Smaller than its wall-clock share feels, deliberately. Receiving moves the bytes once;
    /// processing then moves them again to staging, pulls them down in the Function, decodes,
    /// transcodes, verifies, uploads, copies across accounts and builds artwork. Weighting them
    /// evenly would park the bar at 50% for most of the run.
    /// </para>
    /// </summary>
    public const double ReceivingShare = 25d;

    /// <summary>Share of the batch bar given to everything after the files have been received.</summary>
    public const double ProcessingShare = 100d - ReceivingShare;

    /// <summary>
    /// One figure for a whole batch: receiving all the files, then processing every song.
    ///
    /// <para>
    /// The per-song bars answer "where is this song"; this answers "how far through is the batch",
    /// which is the only question worth asking while ten songs are in flight at once. It is a mean
    /// rather than a min or max so that one slow song does not hide nine finished ones, nor the
    /// reverse.
    /// </para>
    /// </summary>
    /// <param name="receivingPercent">
    /// 0-100 for the receiving phase. Pass 100 once every file has been received, including when
    /// rows are restored on a later visit and the receiving phase never happened here.
    /// </param>
    /// <param name="songPercents">
    /// Each song's own overall percent. Terminal songs - published or failed - should be passed as
    /// 100: a failed song is never going to progress further, and leaving it short would strand the
    /// batch bar below 100 forever.
    /// </param>
    public static double ToBatchPercent(double receivingPercent, IReadOnlyList<double> songPercents)
    {
        var receiving = Math.Clamp(receivingPercent, 0d, 100d) / 100d * ReceivingShare;

        if (songPercents is null || songPercents.Count == 0)
        {
            return receiving;
        }

        var mean = songPercents.Sum(percent => Math.Clamp(percent, 0d, 100d)) / songPercents.Count / 100d;
        return receiving + (mean * ProcessingShare);
    }
}
