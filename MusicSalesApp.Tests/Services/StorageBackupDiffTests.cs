using System.Globalization;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class StorageBackupDiffTests
{
    private const string SourceETag = "0x8DCAAAA111";
    private static readonly DateTimeOffset BackedUpAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // ---------------- Backup ----------------

    [Test]
    public void DecideBackup_WhenBackupBlobMissing_Copies()
        => Assert.That(
            StorageBackupDiff.DecideBackup(Source(), backup: null, forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideBackup_WhenStampedETagMatches_Skips()
        => Assert.That(
            StorageBackupDiff.DecideBackup(Source(), Backup(SourceETag), forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Skip));

    [Test]
    public void DecideBackup_WhenStampedETagDiffers_Copies()
        => Assert.That(
            StorageBackupDiff.DecideBackup(Source(), Backup("0x8DCBBBB222"), forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideBackup_WhenStampedETagMissing_Copies()
        => Assert.That(
            StorageBackupDiff.DecideBackup(
                Source(),
                Snapshot(metadata: new Dictionary<string, string>()),
                forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideBackup_WhenStampedETagBlank_Copies()
        => Assert.That(
            StorageBackupDiff.DecideBackup(Source(), Backup("   "), forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideBackup_WhenETagMatchesButLengthDiffers_Copies()
        => Assert.That(
            StorageBackupDiff.DecideBackup(
                Source(length: 4096),
                Backup(SourceETag, length: 1024),
                forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideBackup_WhenCopyStillPending_SkipsThisRun()
        => Assert.That(
            StorageBackupDiff.DecideBackup(
                Source(),
                Backup("0xSTALE", copyStatus: StorageBlobCopyStatuses.Pending),
                forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.SkipCopyPending));

    [Test]
    public void DecideBackup_WhenCopyPending_PendingWinsOverForceFullCopy()
        => Assert.That(
            StorageBackupDiff.DecideBackup(
                Source(),
                Backup(SourceETag, copyStatus: StorageBlobCopyStatuses.Pending),
                forceFullCopy: true),
            Is.EqualTo(StorageBackupDecision.SkipCopyPending));

    [TestCase("Failed")]
    [TestCase("Aborted")]
    public void DecideBackup_WhenPreviousCopyDidNotSucceed_Copies(string copyStatus)
        => Assert.That(
            StorageBackupDiff.DecideBackup(
                Source(),
                Backup(SourceETag, copyStatus: copyStatus),
                forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideBackup_WhenForceFullCopy_CopiesEvenOnExactMatch()
        => Assert.That(
            StorageBackupDiff.DecideBackup(Source(), Backup(SourceETag), forceFullCopy: true),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideBackup_IgnoresWeakMarkerAndQuoteDifferencesInETagFormatting()
        => Assert.That(
            StorageBackupDiff.DecideBackup(
                Source(etag: SourceETag),
                Backup($"W/\"{SourceETag}\""),
                forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Skip));

    // ---------------- Restore ----------------

    [Test]
    public void DecideRestore_WhenLiveBlobMissing_Copies()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag), live: null, StorageRestoreScope.MissingOnly, overwriteNewerLive: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideRestore_MissingOnly_LeavesDifferingLiveBlobAlone()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag),
                Snapshot(etag: "0xDIFFERENT"),
                StorageRestoreScope.MissingOnly,
                overwriteNewerLive: true),
            Is.EqualTo(StorageBackupDecision.Skip));

    [Test]
    public void DecideRestore_WhenLiveStillHoldsTheVersionThatWasBackedUp_Skips()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag),
                Snapshot(etag: SourceETag),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: true),
            Is.EqualTo(StorageBackupDecision.Skip));

    [Test]
    public void DecideRestore_WhenLiveWasAlreadyRestoredFromThisBackup_Skips()
    {
        var backup = Backup(SourceETag) with { ETag = "0xBACKUPETAG" };
        var live = Snapshot(
            etag: "0xREWRITTEN",
            metadata: new Dictionary<string, string>
            {
                [StorageBackupMetadataKeys.RestoredFromETag] = "0xBACKUPETAG"
            });

        Assert.That(
            StorageBackupDiff.DecideRestore(
                backup, live, StorageRestoreScope.MissingAndDiffering, overwriteNewerLive: true),
            Is.EqualTo(StorageBackupDecision.Skip));
    }

    [Test]
    public void DecideRestore_WhenLengthAndMd5Match_Skips()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag, length: 2048, md5: "Q2hlY2s="),
                Snapshot(etag: "0xDIFFERENT", length: 2048, md5: "Q2hlY2s="),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: true),
            Is.EqualTo(StorageBackupDecision.Skip));

    [Test]
    public void DecideRestore_WhenMd5AbsentOnBothSidesAndETagsDiffer_Copies()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag, length: 2048, md5: null),
                Snapshot(etag: "0xDIFFERENT", length: 2048, md5: null),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: true),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideRestore_WhenLiveIsNewerAndOverwriteNewerDisabled_ReportsSkipNewerLive()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag),
                Snapshot(etag: "0xDIFFERENT", lastModified: BackedUpAt.AddHours(3)),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: false),
            Is.EqualTo(StorageBackupDecision.SkipNewerLive));

    [Test]
    public void DecideRestore_WhenLiveIsNewerAndOverwriteNewerEnabled_Copies()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag),
                Snapshot(etag: "0xDIFFERENT", lastModified: BackedUpAt.AddHours(3)),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: true),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideRestore_WhenLiveIsOlderThanTheBackup_Copies()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag),
                Snapshot(etag: "0xDIFFERENT", lastModified: BackedUpAt.AddHours(-3)),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: false),
            Is.EqualTo(StorageBackupDecision.Copy));

    [Test]
    public void DecideRestore_WhenBackupProvenanceTimestampIsUnparseable_CopiesToRepair()
    {
        var backup = Backup(SourceETag, sourceLastModified: "not-a-timestamp");

        Assert.That(
            StorageBackupDiff.DecideRestore(
                backup,
                Snapshot(etag: "0xDIFFERENT", lastModified: BackedUpAt.AddHours(3)),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: false),
            Is.EqualTo(StorageBackupDecision.Copy));
    }

    [Test]
    public void DecideRestore_WhenLiveCopyStillPending_SkipsThisRun()
        => Assert.That(
            StorageBackupDiff.DecideRestore(
                Backup(SourceETag),
                Snapshot(etag: "0xDIFFERENT", copyStatus: StorageBlobCopyStatuses.Pending),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: true),
            Is.EqualTo(StorageBackupDecision.SkipCopyPending));

    // ---------------- Metadata ----------------

    [Test]
    public void BuildBackupMetadata_StampsEveryProvenanceKey()
    {
        var utcNow = new DateTime(2026, 7, 26, 6, 45, 0, DateTimeKind.Utc);
        var metadata = StorageBackupDiff.BuildBackupMetadata(
            Source(md5: "Q2hlY2s="), "musiccontainer", runId: 42, utcNow);

        Assert.Multiple(() =>
        {
            Assert.That(metadata[StorageBackupMetadataKeys.SourceETag], Is.EqualTo(SourceETag));
            Assert.That(metadata[StorageBackupMetadataKeys.SourceLength], Is.EqualTo("1024"));
            Assert.That(metadata[StorageBackupMetadataKeys.SourceContainer], Is.EqualTo("musiccontainer"));
            Assert.That(metadata[StorageBackupMetadataKeys.BackupRunId], Is.EqualTo("42"));
            Assert.That(metadata[StorageBackupMetadataKeys.SourceMd5], Is.EqualTo("Q2hlY2s="));
            Assert.That(
                DateTimeOffset.Parse(
                    metadata[StorageBackupMetadataKeys.SourceLastModified],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Is.EqualTo(BackedUpAt));
        });
    }

    [Test]
    public void BuildBackupMetadata_OmitsMd5WhenSourceHasNone()
        => Assert.That(
            StorageBackupDiff.BuildBackupMetadata(Source(md5: null), "musiccontainer", 1, DateTime.UtcNow)
                .ContainsKey(StorageBackupMetadataKeys.SourceMd5),
            Is.False);

    [Test]
    public void BuildBackupMetadata_RoundTripsThroughDecideBackupAsSkip()
    {
        var source = Source();
        var metadata = StorageBackupDiff.BuildBackupMetadata(
            source, "musiccontainer", runId: 7, DateTime.UtcNow);
        var backup = Snapshot(etag: "0xDESTINATION", length: source.Length, metadata: metadata);

        Assert.That(
            StorageBackupDiff.DecideBackup(source, backup, forceFullCopy: false),
            Is.EqualTo(StorageBackupDecision.Skip));
    }

    [Test]
    public void BuildRestoreMetadata_RecordsTheBackupETagItCameFrom()
    {
        var backup = Backup(SourceETag) with { ETag = "0xBACKUPETAG" };
        var metadata = StorageBackupDiff.BuildRestoreMetadata(backup, DateTime.UtcNow);

        Assert.That(
            metadata[StorageBackupMetadataKeys.RestoredFromETag], Is.EqualTo("0xBACKUPETAG"));
    }

    [Test]
    public void GetMetadata_IsCaseInsensitiveAndNullTolerant()
    {
        var snapshot = Snapshot(metadata: new Dictionary<string, string> { ["SRC_ETAG"] = "abc" });

        Assert.Multiple(() =>
        {
            Assert.That(
                StorageBackupDiff.GetMetadata(snapshot, StorageBackupMetadataKeys.SourceETag),
                Is.EqualTo("abc"));
            Assert.That(
                StorageBackupDiff.GetMetadata(Snapshot(metadata: null), StorageBackupMetadataKeys.SourceETag),
                Is.Null);
        });
    }

    // ---------------- Builders ----------------

    private static StorageBlobSnapshot Source(
        string etag = SourceETag,
        long length = 1024,
        string md5 = null)
        => new(
            "song/song.mp3",
            etag,
            length,
            BackedUpAt,
            md5,
            StorageBlobCopyStatuses.Success,
            new Dictionary<string, string>());

    private static StorageBlobSnapshot Backup(
        string stampedSourceETag,
        long length = 1024,
        string md5 = null,
        string copyStatus = StorageBlobCopyStatuses.Success,
        string sourceLastModified = null)
    {
        var metadata = new Dictionary<string, string>
        {
            [StorageBackupMetadataKeys.SourceETag] = stampedSourceETag,
            [StorageBackupMetadataKeys.SourceLastModified] =
                sourceLastModified ?? BackedUpAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };
        return Snapshot(etag: "0xDESTINATION", length: length, md5: md5, copyStatus: copyStatus, metadata: metadata);
    }

    private static StorageBlobSnapshot Snapshot(
        string etag = "0xDESTINATION",
        long length = 1024,
        DateTimeOffset? lastModified = null,
        string md5 = null,
        string copyStatus = StorageBlobCopyStatuses.Success,
        IReadOnlyDictionary<string, string> metadata = null)
        => new(
            "song/song.mp3",
            etag,
            length,
            lastModified ?? BackedUpAt,
            md5,
            copyStatus,
            metadata);
}
