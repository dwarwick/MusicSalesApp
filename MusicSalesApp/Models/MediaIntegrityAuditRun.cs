using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

public class MediaIntegrityAuditRun
{
    [Key]
    public int Id { get; set; }
    public MediaAuditMode Mode { get; set; }
    public MediaAuditRunStatus Status { get; set; } = MediaAuditRunStatus.Queued;
    public int? ActiveLockKey { get; set; }
    public int? SourceRunId { get; set; }
    public int? InitiatedByUserId { get; set; }
    [MaxLength(256)] public string InitiatedByEmail { get; set; }
    [MaxLength(100)] public string HangfireJobId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int CandidateCount { get; set; }
    public int ProcessedCount { get; set; }
    public int HealthyCount { get; set; }
    public int RepairableCount { get; set; }
    public int NamingWarningCount { get; set; }
    public int OriginalSourceMissingCount { get; set; }
    public int ConfirmedUnplayableCount { get; set; }
    public int InconclusiveCount { get; set; }
    public int RepairedCount { get; set; }
    public int QuarantinedCount { get; set; }
    public int NotificationFailureCount { get; set; }

    /// <summary>
    /// How many songs claiming an encrypted-HLS package were checked against storage.
    ///
    /// <para>
    /// Separate from the probe counts above and not a subset of them: those describe songs FFmpeg
    /// was asked to decode, this describes rows checked with a HEAD request. A song can be perfectly
    /// decodable and still have no package.
    /// </para>
    /// </summary>
    public int HlsPackagesCheckedCount { get; set; }

    /// <summary>
    /// How many of those had no package in storage, or an incomplete one.
    ///
    /// <para>
    /// Non-zero is the signature of a restore that did not bring the streaming container back: the
    /// database looks healthy and the catalogue does not play. It is repaired by a packaging backfill
    /// with scope <c>RepairMissing</c>, which keys on the blob's absence rather than on any column,
    /// so no row has to be edited by hand.
    /// </para>
    /// </summary>
    public int HlsPackagesMissingCount { get; set; }

    /// <summary>
    /// True when storage could not be reached, so <see cref="HlsPackagesMissingCount"/> proves
    /// nothing. Recorded rather than inferred from a zero count, because "no problems found" and
    /// "could not look" are the same number and opposite meanings.
    /// </summary>
    public bool HlsPackageCheckInconclusive { get; set; }
    [MaxLength(2000)] public string FailureMessage { get; set; }
    public bool AdminNotificationSent { get; set; }
    public ICollection<MediaIntegrityAuditItem> Items { get; set; } = new List<MediaIntegrityAuditItem>();
    public ICollection<MediaIntegrityAuditNotification> Notifications { get; set; } = new List<MediaIntegrityAuditNotification>();
}
