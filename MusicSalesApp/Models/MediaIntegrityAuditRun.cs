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
    [MaxLength(2000)] public string FailureMessage { get; set; }
    public bool AdminNotificationSent { get; set; }
    public ICollection<MediaIntegrityAuditItem> Items { get; set; } = new List<MediaIntegrityAuditItem>();
    public ICollection<MediaIntegrityAuditNotification> Notifications { get; set; } = new List<MediaIntegrityAuditNotification>();
}
