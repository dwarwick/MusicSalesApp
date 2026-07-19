namespace MusicSalesApp.Common.Helpers;

public static class MediaIntegrityConstants
{
    public const string QuarantineReason =
        "Media integrity audit: the stored playback audio could not be decoded. Re-upload a valid audio file to restore this song.";

    public const string RecoveryReason =
        "Media integrity recovery: a validated replacement audio file was uploaded and the song was restored.";
}

public static class MediaIntegrityNotificationTypes
{
    public const string AdminCompletion = "AdminCompletion";
}

public enum MediaAuditMode
{
    ReportOnly,
    RepairSafeMetadata,
    QuarantineConfirmedFailures
}

public enum MediaAuditOutcome
{
    Healthy,
    MetadataRepairable,
    NamingWarning,
    OriginalSourceMissing,
    ConfirmedUnplayable,
    Inconclusive
}

public enum MediaAuditRunStatus
{
    Queued,
    Running,
    Completed,
    Failed
}
