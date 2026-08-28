using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

/// <summary>Which songs a packaging run is for.</summary>
public enum HlsPackagingBackfillScope
{
    /// <summary>
    /// Songs that have never been packaged (<c>HlsStreamId</c> is null). The one-time pass over the
    /// existing catalogue, run once per environment.
    /// </summary>
    Missing = 0,

    /// <summary>
    /// Songs whose row records a package that storage does not actually hold.
    ///
    /// <para>
    /// This is the disaster-recovery path. After a blob restore that did not bring the streaming
    /// container back, every row still carries its <c>HlsStreamId</c> and every one of them points
    /// at a folder that is gone — the database looks perfectly healthy while nothing plays. Keying
    /// on the blob's absence rather than on a column is what makes this repairable without anyone
    /// hand-editing rows.
    /// </para>
    ///
    /// <para>
    /// Costs one blob existence check per song, which is why it is a deliberate scope and not the
    /// default.
    /// </para>
    /// </summary>
    RepairMissing = 1,

    /// <summary>
    /// Every playable song, packaged again from scratch. For a codec or segment-length change.
    /// Each song gets a fresh <c>HlsStreamId</c>, so nothing is overwritten in place and the old
    /// package keeps serving until the new one is recorded.
    /// </summary>
    RepackageAll = 2
}

public enum HlsPackagingBackfillStatus
{
    Queued = 0,
    Dispatching = 1,

    /// <summary>
    /// Every message is on the queue and the run is waiting for callbacks. The distinct state
    /// matters: the Hangfire job has returned by now, and without it a run in this state would look
    /// abandoned rather than in progress.
    /// </summary>
    AwaitingCallbacks = 2,

    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// One execution of the encrypted-HLS packaging backfill.
///
/// <para>
/// Modelled on <see cref="ImageVariantBackfillRun"/>, with one structural difference that shapes
/// everything else: FFmpeg does not run here. This app only <em>dispatches</em> queue messages, and
/// the Function does the work in Azure — so a run finishes on its <b>last callback</b>, not when its
/// Hangfire job returns. That is the same arrangement the media-integrity audit uses.
/// </para>
///
/// <para>
/// Scoped to the environment it runs in, because the containers and the database come from this
/// process's configuration. Run it once per environment — local, then test, then production.
/// </para>
/// </summary>
public class HlsPackagingBackfillRun
{
    /// <summary>
    /// Cap on recorded failure rows. A systemic failure — a misconfigured queue, a Function that
    /// cannot start — would otherwise write one row per song in the catalogue, turning a diagnostic
    /// aid into a second incident. The counters stay exact regardless; only the detail is bounded.
    /// </summary>
    public const int MaxRecordedFailures = 200;

    [Key]
    public int Id { get; set; }

    public HlsPackagingBackfillScope Scope { get; set; } = HlsPackagingBackfillScope.Missing;

    /// <summary>Select and count the work, but enqueue nothing.</summary>
    public bool DryRun { get; set; }

    public HlsPackagingBackfillStatus Status { get; set; } = HlsPackagingBackfillStatus.Queued;

    /// <summary>
    /// Always 1 while the run is active, null otherwise. A unique filtered index on this column is
    /// what makes "only one run at a time" race-proof at the database level rather than by
    /// convention — the same mechanism the image-variant backfill uses.
    /// </summary>
    public int? ActiveLockKey { get; set; }

    public int? InitiatedByUserId { get; set; }

    [MaxLength(256)]
    public string InitiatedByEmail { get; set; }

    [MaxLength(100)]
    public string HangfireJobId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancellationRequestedAt { get; set; }

    /// <summary>
    /// When the most recent callback landed. This is the run's liveness signal, and the only one
    /// there is: the Hangfire job returns as soon as dispatch finishes, so a run that has stopped
    /// receiving callbacks is indistinguishable from a finished one by any other measure.
    /// </summary>
    public DateTime? LastCallbackAt { get; set; }

    /// <summary>How many songs the scope selected.</summary>
    public int TotalItemCount { get; set; }

    /// <summary>How many messages actually reached the queue.</summary>
    public int DispatchedCount { get; set; }

    public int SucceededCount { get; set; }

    public int FailedCount { get; set; }

    /// <summary>
    /// Sum of the Function's own per-song packaging time, in seconds.
    ///
    /// <para>
    /// Kept alongside the wall clock because on their own each is misleading. Wall clock reflects how
    /// many songs Azure happened to be running at once, which for a short run is mostly scale-out
    /// ramp; this total reflects the real cost of the work. Divided by <see cref="SucceededCount"/>
    /// it gives the per-song figure a larger run can be projected from, and divided by the wall clock
    /// it gives the concurrency actually achieved.
    /// </para>
    /// </summary>
    public double TotalProcessingSeconds { get; set; }

    /// <summary>
    /// Mean Function time per packaged song, or null when there is nothing to average.
    ///
    /// <para>
    /// Null rather than zero when no time was recorded, which is the state of every run that
    /// completed before this column existed. Returning zero there would render as a confident
    /// "0.0s per song" and feed a projection of nothing at all.
    /// </para>
    /// </summary>
    public double? AverageProcessingSeconds
        => SucceededCount > 0 && TotalProcessingSeconds > 0
            ? TotalProcessingSeconds / SucceededCount
            : null;

    /// <summary>How long the run took end to end, once it has finished.</summary>
    public TimeSpan? Elapsed
        => StartedAt.HasValue && CompletedAt.HasValue ? CompletedAt.Value - StartedAt.Value : null;

    /// <summary>
    /// Songs packaged simultaneously on average: total Function time over wall-clock time.
    ///
    /// <para>
    /// 1.0 means it ran effectively one at a time. Higher means Azure had several instances going.
    /// This is the number that decides how a small run extrapolates, and it is the one thing neither
    /// the queue nor the portal will tell you directly.
    /// </para>
    /// </summary>
    public double? ObservedConcurrency
    {
        get
        {
            var elapsed = Elapsed?.TotalSeconds ?? 0;
            return elapsed > 0 && TotalProcessingSeconds > 0
                ? TotalProcessingSeconds / elapsed
                : null;
        }
    }

    [MaxLength(2000)]
    public string FailureMessage { get; set; }

    public ICollection<HlsPackagingBackfillFailure> Failures { get; set; } = new List<HlsPackagingBackfillFailure>();

    /// <summary>Callbacks still outstanding. Zero with dispatch finished means the run is done.</summary>
    public int OutstandingCount => Math.Max(0, DispatchedCount - SucceededCount - FailedCount);
}

/// <summary>One song that could not be packaged, kept for the admin page's failure table.</summary>
public class HlsPackagingBackfillFailure
{
    [Key]
    public int Id { get; set; }

    public int RunId { get; set; }

    public HlsPackagingBackfillRun Run { get; set; }

    public int SongMetadataId { get; set; }

    [MaxLength(100)]
    public string FailureCode { get; set; }

    [MaxLength(1000)]
    public string Reason { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
