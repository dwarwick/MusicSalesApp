namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for UserHistory event types recorded via IAdminNotificationService.
/// Always use these constants instead of inline strings to avoid mismatches between
/// writers (RecordUserHistoryAsync) and readers (LINQ queries).
/// </summary>
public static class UserHistoryEventTypes
{
    public const string Registration = "Registration";
    public const string EmailConfirmed = "EmailConfirmed";
    public const string TaxFormCompleted = "TaxFormCompleted";
    public const string CreatorStatusGained = "CreatorStatusGained";
    public const string CreatorStatusLost = "CreatorStatusLost";
    public const string UploadCompleted = "UploadCompleted";
    public const string SongRenamed = "SongRenamed";
    public const string SongArtUpdated = "SongArtUpdated";
}
