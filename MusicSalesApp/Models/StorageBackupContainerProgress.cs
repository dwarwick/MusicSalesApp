using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

/// <summary>
/// Per-container progress within a <see cref="StorageBackupRun"/>. This row is what the admin
/// page's progress bars render, and persisting it is what lets a page opened mid-run show
/// correct state immediately.
/// </summary>
public class StorageBackupContainerProgress
{
    [Key]
    public int Id { get; set; }

    public int RunId { get; set; }
    public StorageBackupRun Run { get; set; }

    /// <summary>The live container, e.g. "musiccontainer".</summary>
    [MaxLength(128)] public string SourceContainerName { get; set; }

    /// <summary>The backup container, e.g. "backup-musiccontainer".</summary>
    [MaxLength(128)] public string DestinationContainerName { get; set; }

    public StorageBackupContainerStatus Status { get; set; } = StorageBackupContainerStatus.Pending;

    /// <summary>Zero until listing completes, which is why the bar is indeterminate while Listing.</summary>
    public int TotalBlobCount { get; set; }

    public int ProcessedCount { get; set; }
    public int CopiedCount { get; set; }
    public int SkippedCount { get; set; }
    public int SkippedNewerLiveCount { get; set; }
    public int FailedCount { get; set; }
    public long CopiedBytes { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [MaxLength(2000)] public string FailureMessage { get; set; }
}
