namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// Maps a lyrics-alignment step, plus optional progress within that step, onto a single 0-100 figure
/// spanning the whole attempt.
///
/// <para>
/// The lyrics equivalent of <see cref="AudioProcessingProgressCalculator"/>, and separate from it for
/// the same reason <see cref="LyricsAlignmentStep"/> is separate from
/// <see cref="AudioProcessingStep"/>. Two processes share this table — the Python Function app posts
/// progress against it and the web app records and rebroadcasts it — so it is the one place the
/// bands are defined.
/// </para>
///
/// <para>
/// The weighting is far more lopsided than the audio pipeline's, and honestly so: vocal separation
/// really is most of the wall clock. Everything after alignment is seconds of work on kilobytes of
/// JSON, so the tail bands are slivers.
/// </para>
/// </summary>
public static class LyricsAlignmentProgressCalculator
{
    private static readonly IReadOnlyDictionary<LyricsAlignmentStep, (double Start, double End)> Bands =
        new Dictionary<LyricsAlignmentStep, (double, double)>
        {
            [LyricsAlignmentStep.Submitted] = (0d, 5d),
            [LyricsAlignmentStep.Queued] = (5d, 5d),
            [LyricsAlignmentStep.Preparing] = (5d, 15d),

            // The widest band by a wide margin, because Demucs on CPU is most of the run. Its width
            // is load-bearing rather than cosmetic: the activity has no natural progress signal, so
            // it heartbeats on a timer, and a narrow band would make every heartbeat round to the
            // same whole percent and be suppressed by the sender's gate.
            [LyricsAlignmentStep.SeparatingVocals] = (15d, 70d),

            [LyricsAlignmentStep.Aligning] = (70d, 85d),
            [LyricsAlignmentStep.Mapping] = (85d, 90d),
            [LyricsAlignmentStep.WritingOutputs] = (90d, 93d),
            [LyricsAlignmentStep.Copying] = (93d, 96d),
            [LyricsAlignmentStep.Saving] = (96d, 100d),
            [LyricsAlignmentStep.Completed] = (100d, 100d),

            // A failed attempt's bar stops where it was rather than snapping to 0 or 100, so the
            // creator can see how far it got. ToOverallPercent is not consulted for Failed - the
            // last known percent is retained - but the entry keeps the table total.
            [LyricsAlignmentStep.Failed] = (0d, 100d)
        };

    /// <summary>
    /// The overall percent for <paramref name="step"/>, optionally advanced by
    /// <paramref name="stepPercent"/> (0-100) through that step's own band.
    /// </summary>
    public static double ToOverallPercent(LyricsAlignmentStep step, double? stepPercent = null)
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
    public static double BandStart(LyricsAlignmentStep step)
        => Bands.TryGetValue(step, out var band) ? band.Start : 0d;

    /// <summary>The overall percent at which <paramref name="step"/> finishes.</summary>
    public static double BandEnd(LyricsAlignmentStep step)
        => Bands.TryGetValue(step, out var band) ? band.End : 0d;

    /// <summary>
    /// Whether <paramref name="incoming"/> should replace <paramref name="current"/>.
    ///
    /// <para>
    /// Progress posts are fire-and-forget HTTP from the Function and can arrive out of order.
    /// Anything at or below the recorded step is dropped, which is what stops the bar from
    /// stuttering backwards.
    /// </para>
    /// </summary>
    public static bool IsAdvance(LyricsAlignmentStep current, LyricsAlignmentStep incoming)
        => incoming > current;

    /// <summary>Whether <paramref name="step"/> is one an attempt can never move on from.</summary>
    public static bool IsTerminal(LyricsAlignmentStep step)
        => step is LyricsAlignmentStep.Completed or LyricsAlignmentStep.Failed;
}
