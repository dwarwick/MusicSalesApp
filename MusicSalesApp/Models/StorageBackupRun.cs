using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

/// <summary>
/// One execution of the Azure blob backup or restore job. A single row carries both directions
/// so backup and restore share the same singleton lock and can never overlap.
/// </summary>
public class StorageBackupRun
{
    [Key]
    public int Id { get; set; }

    public StorageBackupDirection Direction { get; set; }

    /// <summary>Only meaningful when <see cref="Direction"/> is Restore.</summary>
    public StorageRestoreScope RestoreScope { get; set; } = StorageRestoreScope.MissingAndDiffering;

    /// <summary>Only meaningful when <see cref="Direction"/> is Restore.</summary>
    public bool OverwriteNewerLive { get; set; } = true;

    /// <summary>Only meaningful when <see cref="Direction"/> is Backup.</summary>
    public bool ForceFullCopy { get; set; }

    public StorageBackupRunStatus Status { get; set; } = StorageBackupRunStatus.Queued;

    /// <summary>
    /// Always 1 while the run is active, null otherwise. A unique filtered index on this column
    /// makes "only one run at a time" race-proof at the database level.
    /// </summary>
    public int? ActiveLockKey { get; set; }

    public int? InitiatedByUserId { get; set; }
    [MaxLength(256)] public string InitiatedByEmail { get; set; }
    [MaxLength(100)] public string HangfireJobId { get; set; }
    [MaxLength(20)] public string TriggerSource { get; set; } = StorageBackupTriggerSources.Admin;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancellationRequestedAt { get; set; }

    public int TotalBlobCount { get; set; }
    public int ProcessedCount { get; set; }
    public int CopiedCount { get; set; }
    public int SkippedCount { get; set; }
    public int SkippedNewerLiveCount { get; set; }
    public int FailedCount { get; set; }
    public long CopiedBytes { get; set; }

    [MaxLength(2000)] public string FailureMessage { get; set; }

    public ICollection<StorageBackupContainerProgress> Containers { get; set; }
        = new List<StorageBackupContainerProgress>();

    public ICollection<StorageBackupItemFailure> Failures { get; set; }
        = new List<StorageBackupItemFailure>();
}
