using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

/// <summary>
/// One execution of the image-variant backfill: the one-off pass that generates pre-resized WebP
/// renditions for cover art and persona images that were uploaded before the feature existed.
///
/// <para>
/// The job only ever writes to the containers configured for the environment it runs in, so it is
/// run once per environment - local, then test, then production - rather than once globally.
/// Propagating the results to the <c>backup-*</c> containers is a separate, existing step: run a
/// storage backup afterwards.
/// </para>
/// </summary>
public class ImageVariantBackfillRun
{
    [Key]
    public int Id { get; set; }

    public ImageVariantBackfillScope Scope { get; set; } = ImageVariantBackfillScope.All;

    /// <summary>Decode, resample and measure, but write nothing to storage or the database.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Reprocess images that already have renditions recorded. Off by default, which is what makes
    /// a run idempotent and resumable: re-running only picks up what is still missing.
    /// </summary>
    public bool RegenerateExisting { get; set; }

    /// <summary>
    /// Also delete the superseded <c>_fb.png</c> sharing images left behind when share images moved
    /// from PNG to JPEG. Folded into this job because it already walks every song.
    /// </summary>
    public bool RemoveLegacyPngSharingImages { get; set; }

    public ImageVariantBackfillStatus Status { get; set; } = ImageVariantBackfillStatus.Queued;

    /// <summary>
    /// Always 1 while the run is active, null otherwise. A unique filtered index on this column
    /// makes "only one run at a time" race-proof at the database level.
    /// </summary>
    public int? ActiveLockKey { get; set; }

    public int? InitiatedByUserId { get; set; }
    [MaxLength(256)] public string InitiatedByEmail { get; set; }
    [MaxLength(100)] public string HangfireJobId { get; set; }
    [MaxLength(20)] public string TriggerSource { get; set; } = ImageVariantBackfillTriggerSources.Admin;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancellationRequestedAt { get; set; }

    public int TotalItemCount { get; set; }
    public int ProcessedCount { get; set; }
    public int GeneratedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>Rendition blobs written (or, on a dry run, that would have been).</summary>
    public int VariantBlobCount { get; set; }

    /// <summary>Encoded bytes across every rendition, so a dry run can report the storage delta.</summary>
    public long BytesWritten { get; set; }

    /// <summary>
    /// Images whose source was narrower than the smallest ladder width, and so produced a single
    /// rendition at their own size. Worth surfacing after a dry run as a data-quality signal.
    /// </summary>
    public int UndersizedSourceCount { get; set; }

    /// <summary>Superseded PNG sharing images removed, when that option was enabled.</summary>
    public int LegacySharingImagesRemoved { get; set; }

    [MaxLength(2000)] public string FailureMessage { get; set; }

    public ICollection<ImageVariantBackfillItemFailure> Failures { get; set; }
        = new List<ImageVariantBackfillItemFailure>();
}
