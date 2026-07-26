using System.Globalization;

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// A provider-neutral view of one blob, built from a container listing. Kept free of any
/// Azure SDK type so the backup/restore decision rules can be unit tested without mocking
/// <c>BlobServiceClient</c> or <c>Pageable&lt;BlobItem&gt;</c>, which are effectively unmockable.
/// </summary>
public sealed record StorageBlobSnapshot(
    string Name,
    string ETag,
    long Length,
    DateTimeOffset? LastModified,
    string ContentMd5Base64,
    string CopyStatus,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags = null);

/// <summary>
/// Azure's blob copy-status values, as strings so <see cref="StorageBlobSnapshot"/> stays SDK-free.
/// </summary>
public static class StorageBlobCopyStatuses
{
    public const string Pending = "Pending";
    public const string Success = "Success";
    public const string Aborted = "Aborted";
    public const string Failed = "Failed";
}

/// <summary>
/// The outcome of a per-blob decision.
/// </summary>
public enum StorageBackupDecision
{
    /// <summary>Copy the blob.</summary>
    Copy = 0,

    /// <summary>Destination already holds this exact version.</summary>
    Skip = 1,

    /// <summary>A server-side copy from an earlier run is still finishing; leave it alone this run.</summary>
    SkipCopyPending = 2,

    /// <summary>The live blob is newer than the backup and the run was told not to overwrite newer files.</summary>
    SkipNewerLive = 3
}

/// <summary>
/// The change-detection rules for storage backup and restore.
/// <para>
/// Backup keys off the source blob's ETag, which Azure rotates on every content write and which
/// comes back free of charge in a container listing. The ETag observed at copy time is stamped into
/// the backup blob's metadata, so a later run needs only two container listings and an in-memory
/// diff — no per-blob HEAD requests.
/// </para>
/// </summary>
public static class StorageBackupDiff
{
    /// <summary>
    /// Decides whether a source blob needs copying into the backup container.
    /// </summary>
    /// <param name="source">The live blob. Required.</param>
    /// <param name="backup">The corresponding backup blob, or null when it does not exist yet.</param>
    /// <param name="forceFullCopy">When true, re-copy even blobs that appear unchanged.</param>
    public static StorageBackupDecision DecideBackup(
        StorageBlobSnapshot source,
        StorageBlobSnapshot backup,
        bool forceFullCopy)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (backup == null)
        {
            return StorageBackupDecision.Copy;
        }

        // A copy started by a previous run that was killed mid-flight is still completing
        // server-side. Azure rejects a second copy onto the same destination, and the next
        // run will see the finished result, so leave it alone.
        if (IsCopyStatus(backup, StorageBlobCopyStatuses.Pending))
        {
            return StorageBackupDecision.SkipCopyPending;
        }

        if (forceFullCopy)
        {
            return StorageBackupDecision.Copy;
        }

        if (IsCopyStatus(backup, StorageBlobCopyStatuses.Failed)
            || IsCopyStatus(backup, StorageBlobCopyStatuses.Aborted))
        {
            return StorageBackupDecision.Copy;
        }

        var stampedSourceETag = GetMetadata(backup, StorageBackupMetadataKeys.SourceETag);
        if (string.IsNullOrWhiteSpace(stampedSourceETag))
        {
            return StorageBackupDecision.Copy;
        }

        if (!EtagsMatch(stampedSourceETag, source.ETag))
        {
            return StorageBackupDecision.Copy;
        }

        // Defence against a torn copy that nonetheless carried metadata.
        if (backup.Length != source.Length)
        {
            return StorageBackupDecision.Copy;
        }

        return StorageBackupDecision.Skip;
    }

    /// <summary>
    /// Decides whether a backup blob needs copying back over the live container.
    /// </summary>
    /// <param name="backup">The backup blob. Required.</param>
    /// <param name="live">The corresponding live blob, or null when it is missing.</param>
    /// <param name="scope">Whether to repair only gaps, or also replace differing blobs.</param>
    /// <param name="overwriteNewerLive">
    /// When false, a live blob modified after the backup was taken is left alone and reported
    /// as <see cref="StorageBackupDecision.SkipNewerLive"/>.
    /// </param>
    public static StorageBackupDecision DecideRestore(
        StorageBlobSnapshot backup,
        StorageBlobSnapshot live,
        StorageRestoreScope scope,
        bool overwriteNewerLive)
    {
        ArgumentNullException.ThrowIfNull(backup);

        if (live == null)
        {
            return StorageBackupDecision.Copy;
        }

        if (IsCopyStatus(live, StorageBlobCopyStatuses.Pending))
        {
            return StorageBackupDecision.SkipCopyPending;
        }

        if (scope == StorageRestoreScope.MissingOnly)
        {
            return StorageBackupDecision.Skip;
        }

        // The live blob is still the exact version this backup was taken from.
        var backedUpFromETag = GetMetadata(backup, StorageBackupMetadataKeys.SourceETag);
        if (!string.IsNullOrWhiteSpace(backedUpFromETag) && EtagsMatch(backedUpFromETag, live.ETag))
        {
            return StorageBackupDecision.Skip;
        }

        // A previous restore already wrote this backup version over the live blob.
        var restoredFromETag = GetMetadata(live, StorageBackupMetadataKeys.RestoredFromETag);
        if (!string.IsNullOrWhiteSpace(restoredFromETag) && EtagsMatch(restoredFromETag, backup.ETag))
        {
            return StorageBackupDecision.Skip;
        }

        // Content is provably identical.
        if (live.Length == backup.Length
            && !string.IsNullOrWhiteSpace(live.ContentMd5Base64)
            && !string.IsNullOrWhiteSpace(backup.ContentMd5Base64)
            && string.Equals(live.ContentMd5Base64, backup.ContentMd5Base64, StringComparison.Ordinal))
        {
            return StorageBackupDecision.Skip;
        }

        if (!overwriteNewerLive
            && live.LastModified.HasValue
            && TryParseTimestamp(GetMetadata(backup, StorageBackupMetadataKeys.SourceLastModified), out var backedUpAt)
            && live.LastModified.Value > backedUpAt)
        {
            return StorageBackupDecision.SkipNewerLive;
        }

        // Unparseable or absent provenance falls through to Copy: the point of a restore is to repair.
        return StorageBackupDecision.Copy;
    }

    /// <summary>
    /// Builds the metadata stamped onto a backup blob at copy time. This is what makes the next
    /// run's diff a pure listing comparison.
    /// </summary>
    public static Dictionary<string, string> BuildBackupMetadata(
        StorageBlobSnapshot source,
        string sourceContainerName,
        int runId,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(source);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorageBackupMetadataKeys.SourceETag] = source.ETag ?? string.Empty,
            [StorageBackupMetadataKeys.SourceLength] = source.Length.ToString(CultureInfo.InvariantCulture),
            [StorageBackupMetadataKeys.SourceContainer] = sourceContainerName ?? string.Empty,
            [StorageBackupMetadataKeys.BackupRunId] = runId.ToString(CultureInfo.InvariantCulture),
            [StorageBackupMetadataKeys.BackupAt] = FormatTimestamp(utcNow)
        };

        if (source.LastModified.HasValue)
        {
            metadata[StorageBackupMetadataKeys.SourceLastModified] =
                FormatTimestamp(source.LastModified.Value.UtcDateTime);
        }

        // Content-MD5 is only populated when the original upload took the single-shot path,
        // so it is recorded opportunistically and never relied upon as the change signal.
        if (!string.IsNullOrWhiteSpace(source.ContentMd5Base64))
        {
            metadata[StorageBackupMetadataKeys.SourceMd5] = source.ContentMd5Base64;
        }

        return metadata;
    }

    /// <summary>
    /// Builds the metadata stamped onto a live blob when a restore overwrites it, which makes
    /// repeat restores idempotent even for blobs that carry no Content-MD5.
    /// </summary>
    public static Dictionary<string, string> BuildRestoreMetadata(
        StorageBlobSnapshot backup,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(backup);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorageBackupMetadataKeys.RestoredFromETag] = backup.ETag ?? string.Empty,
            [StorageBackupMetadataKeys.RestoredAt] = FormatTimestamp(utcNow)
        };
    }

    /// <summary>
    /// Reads a metadata value, tolerating a null dictionary and Azure's case-insensitive key handling.
    /// </summary>
    public static string GetMetadata(StorageBlobSnapshot snapshot, string key)
    {
        if (snapshot?.Metadata == null || snapshot.Metadata.Count == 0)
        {
            return null;
        }

        if (snapshot.Metadata.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var pair in snapshot.Metadata)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static bool IsCopyStatus(StorageBlobSnapshot snapshot, string status)
        => string.Equals(snapshot.CopyStatus, status, StringComparison.OrdinalIgnoreCase);

    private static bool EtagsMatch(string left, string right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(NormalizeETag(left), NormalizeETag(right), StringComparison.Ordinal);

    /// <summary>
    /// Strips the weak-comparison marker and surrounding quotes so a value written by one Azure
    /// SDK formatting overload still compares equal to one written by another.
    /// </summary>
    private static string NormalizeETag(string etag)
    {
        var value = etag.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }
        return value.Trim('"');
    }

    private static string FormatTimestamp(DateTime utc)
        => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryParseTimestamp(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
               && DateTimeOffset.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                   out parsed);
    }
}
