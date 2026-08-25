namespace MusicSalesApp.Services;

/// <summary>
/// Resolves how many continuous seconds of playback count as a stream.
///
/// The per-creator value is contractual - captured from the admin default when the creator is onboarded
/// and locked for the lifetime of the account (see <see cref="Models.Creator.StreamQualifyingSeconds"/>),
/// and referenced by the creator agreement and terms of use. It is therefore never rewritten.
///
/// The promotional reduction here lowers only the EFFECTIVE threshold used for counting. Counting a
/// stream sooner than contracted is more generous than the agreement rather than a departure from it,
/// so it needs no creator consent - the reverse would.
///
/// Why it exists: a listener without a subscription may only hear
/// <c>PREVIEW_DURATION_SECONDS</c> (60) of a restricted song, so any creator whose contracted threshold
/// is 60 or above can never be credited with a stream from a free listener at all. The reduction brings
/// those creators inside the preview window.
/// </summary>
public static class StreamQualifyingPolicy
{
    /// <summary>How much the promotional reduction takes off the contracted threshold.</summary>
    public const int PromotionalReductionSeconds = 20;

    /// <summary>
    /// Floor for the reduction. A stream still has to mean a real listen, and without this a creator
    /// contracted at or below the reduction would land on zero and be credited the instant playback
    /// started.
    /// </summary>
    public const int MinimumQualifyingSeconds = 15;

    /// <summary>
    /// The threshold to actually count against.
    /// </summary>
    /// <param name="contractedSeconds">
    /// The creator's own value, or the admin default for songs with no creator.
    /// </param>
    /// <param name="reductionEnabled">The ReducedStreamQualifyingEnabled admin setting.</param>
    public static int Resolve(int contractedSeconds, bool reductionEnabled)
    {
        if (!reductionEnabled)
        {
            return contractedSeconds;
        }

        return Math.Max(MinimumQualifyingSeconds, contractedSeconds - PromotionalReductionSeconds);
    }
}

/// <summary>
/// The two inputs every stream-threshold decision needs, travelling together so a caller cannot resolve
/// a creator's threshold while forgetting whether the promotional reduction is on.
/// </summary>
/// <param name="DefaultSeconds">The admin default, for songs with no creator.</param>
/// <param name="ReductionEnabled">Whether the promotional reduction applies.</param>
public readonly record struct StreamQualifyingSettings(int DefaultSeconds, bool ReductionEnabled)
{
    /// <param name="creatorSeconds">The owning creator's contracted value, or null when there is none.</param>
    public int Resolve(int? creatorSeconds) =>
        StreamQualifyingPolicy.Resolve(creatorSeconds ?? DefaultSeconds, ReductionEnabled);
}
