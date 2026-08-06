namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// Maps a <see cref="CoverArtMatchStep"/> onto a 0-100 figure for the pre-pairing phase, and defines
/// how that phase shares the upload page's "receiving" bar with the two transfers around it.
///
/// <para>
/// This is the phase that used to show a static "Files received. Matching cover art..." with the bar
/// pinned at 100 while several vision calls ran. The bands exist so it reports something true.
/// </para>
/// </summary>
public static class CoverArtMatchProgressCalculator
{
    private static readonly IReadOnlyDictionary<CoverArtMatchStep, (double Start, double End)> Bands =
        new Dictionary<CoverArtMatchStep, (double, double)>
        {
            [CoverArtMatchStep.Staging] = (0d, 30d),
            [CoverArtMatchStep.Queued] = (30d, 30d),
            [CoverArtMatchStep.Reading] = (30d, 40d),

            // The widest band by far: one vision call per image, and the only part of this phase
            // whose duration scales with how many files the creator dropped.
            [CoverArtMatchStep.ReadingText] = (40d, 85d),

            [CoverArtMatchStep.Pairing] = (85d, 100d),
            [CoverArtMatchStep.Completed] = (100d, 100d),

            // As with the song pipeline, a failed batch's bar stops where it got to rather than
            // snapping. The entry keeps the table total.
            [CoverArtMatchStep.Failed] = (0d, 100d)
        };

    /// <summary>
    /// The percent for <paramref name="step"/>, optionally advanced by <paramref name="stepPercent"/>
    /// (0-100) through that step's own band.
    /// </summary>
    public static double ToOverallPercent(CoverArtMatchStep step, double? stepPercent = null)
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

    /// <summary>The percent at which <paramref name="step"/> begins.</summary>
    public static double BandStart(CoverArtMatchStep step)
        => Bands.TryGetValue(step, out var band) ? band.Start : 0d;

    /// <summary>The percent at which <paramref name="step"/> finishes.</summary>
    public static double BandEnd(CoverArtMatchStep step)
        => Bands.TryGetValue(step, out var band) ? band.End : 0d;

    /// <summary>Whether <paramref name="step"/> is one a batch can never move on from.</summary>
    public static bool IsTerminal(CoverArtMatchStep step)
        => step is CoverArtMatchStep.Completed or CoverArtMatchStep.Failed;

    /// <summary>
    /// How the upload page's receiving phase - the 0-100 it feeds to
    /// <see cref="AudioProcessingProgressCalculator.ToBatchPercent"/> - divides between its three
    /// stages.
    ///
    /// <para>
    /// <see cref="AudioProcessingProgressCalculator.ReceivingShare"/> itself is deliberately
    /// untouched: changing it would move the meaning of every percentage the batch bar has ever
    /// shown. This subdivides the receiving phase's own scale instead.
    /// </para>
    ///
    /// <para>
    /// Weighted by what actually moves. Pulling the creator's files across the browser connection is
    /// the largest transfer and dominates wall clock; pushing the images to staging is a second,
    /// smaller one; matching moves no bytes at all and is pure API latency.
    /// </para>
    /// </summary>
    public const double ReceivingFilesEnd = 60d;

    /// <inheritdoc cref="ReceivingFilesEnd"/>
    public const double StagingImagesEnd = 75d;

    /// <summary>Maps a 0-100 browser-to-server receive figure onto the receiving phase's own scale.</summary>
    public static double ToReceivingPercent(double filesPercent)
        => Math.Clamp(filesPercent, 0d, 100d) / 100d * ReceivingFilesEnd;

    /// <summary>Maps a 0-100 image-staging figure onto the receiving phase's own scale.</summary>
    public static double ToStagingImagesPercent(double stagingPercent)
        => ReceivingFilesEnd
            + (Math.Clamp(stagingPercent, 0d, 100d) / 100d * (StagingImagesEnd - ReceivingFilesEnd));

    /// <summary>Maps a match-phase percent onto the receiving phase's own scale.</summary>
    public static double ToMatchingPercent(double matchPercent)
        => StagingImagesEnd
            + (Math.Clamp(matchPercent, 0d, 100d) / 100d * (100d - StagingImagesEnd));
}
