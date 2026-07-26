namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Whether a storage backup run copies live containers into their backup containers,
/// or copies backup containers back over the live containers.
/// </summary>
public enum StorageBackupDirection
{
    Backup = 0,
    Restore = 1
}

/// <summary>
/// Lifecycle status of a storage backup or restore run.
/// </summary>
public enum StorageBackupRunStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// Per-container status within a run. Drives the admin progress bars.
/// </summary>
public enum StorageBackupContainerStatus
{
    Pending = 0,
    Listing = 1,
    Copying = 2,
    Completed = 3,
    Skipped = 4,
    Failed = 5
}

/// <summary>
/// How aggressively a restore replaces blobs that already exist in the live container.
/// </summary>
public enum StorageRestoreScope
{
    /// <summary>Only copy blobs that are absent from the live container.</summary>
    MissingOnly = 0,

    /// <summary>Copy blobs that are absent from, or differ from, the backup copy.</summary>
    MissingAndDiffering = 1
}

/// <summary>
/// Naming rules for backup containers. A backup container is always the source container
/// name prefixed with "backup-", so the mapping is derivable in both directions.
/// </summary>
public static class StorageBackupNaming
{
    public const string BackupContainerPrefix = "backup-";

    public static string ToBackupContainerName(string sourceContainerName)
        => BackupContainerPrefix + sourceContainerName;

    public static bool IsBackupContainerName(string containerName)
        => !string.IsNullOrWhiteSpace(containerName)
           && containerName.StartsWith(BackupContainerPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Metadata keys stamped onto backup blobs so that the next run can decide what changed
/// from two container listings alone, with no per-blob HEAD requests.
/// Azure blob metadata keys must be valid C# identifiers, hence snake_case.
/// </summary>
public static class StorageBackupMetadataKeys
{
    /// <summary>ETag of the source blob at the moment it was copied. The change signal.</summary>
    public const string SourceETag = "src_etag";
    public const string SourceLength = "src_length";
    public const string SourceLastModified = "src_lastmodified";
    public const string SourceMd5 = "src_md5";
    public const string SourceContainer = "src_container";
    public const string BackupRunId = "backup_run_id";
    public const string BackupAt = "backup_at";

    /// <summary>Stamped on a live blob when a restore overwrites it, so repeat restores are idempotent.</summary>
    public const string RestoredFromETag = "restored_from_etag";
    public const string RestoredAt = "restored_at";
}

/// <summary>
/// Failure codes recorded against individual blobs that could not be copied.
/// </summary>
public static class StorageBackupFailureCodes
{
    public const string ListFailed = "ListFailed";
    public const string CopyFailed = "CopyFailed";
    public const string CopyTimeout = "CopyTimeout";
    public const string ContainerMissing = "ContainerMissing";
    public const string TooManyConsecutiveFailures = "TooManyConsecutiveFailures";
}

/// <summary>
/// What caused a run to be created.
/// </summary>
public static class StorageBackupTriggerSources
{
    public const string Admin = "Admin";
    public const string Recurring = "Recurring";
}
