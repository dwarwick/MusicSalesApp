#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>Which images an image-variant backfill run processes.</summary>
public enum ImageVariantBackfillScope
{
    /// <summary>Song cover art and creator persona images.</summary>
    All = 0,

    /// <summary>Song cover art only.</summary>
    CoverArtOnly = 1,

    /// <summary>Creator persona images only.</summary>
    PersonaOnly = 2
}

/// <summary>Lifecycle of one image-variant backfill run.</summary>
public enum ImageVariantBackfillStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,

    /// <summary>Finished, but at least one image could not be processed.</summary>
    CompletedWithErrors = 3,

    /// <summary>Aborted - too many consecutive failures, or the job itself threw.</summary>
    Failed = 4,

    Cancelled = 5
}

/// <summary>Where a backfill run was started from.</summary>
public static class ImageVariantBackfillTriggerSources
{
    public const string Admin = "Admin";
}

/// <summary>
/// The confirmation phrase an operator must type before a run that regenerates renditions which
/// already exist. A plain "generate missing" run needs no confirmation - it only fills gaps - but a
/// full regeneration rewrites every rendition blob in the environment.
/// </summary>
public static class ImageVariantBackfillConfirmation
{
    public const string RegeneratePhrase = "REGENERATE";
}
