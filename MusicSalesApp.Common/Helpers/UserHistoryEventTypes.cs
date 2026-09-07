namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for UserHistory event types recorded via IAdminNotificationService.
/// Always use these constants instead of inline strings to avoid mismatches between
/// writers (RecordUserHistoryAsync) and readers (LINQ queries).
/// </summary>
public static class UserHistoryEventTypes
{
    public const string AdminMessageAcknowledged = "AdminMessageAcknowledged";
    public const string Registration = "Registration";
    public const string EmailConfirmed = "EmailConfirmed";
    public const string TaxFormCompleted = "TaxFormCompleted";
    public const string CreatorStatusGained = "CreatorStatusGained";
    public const string CreatorStatusLost = "CreatorStatusLost";
    public const string UploadCompleted = "UploadCompleted";
    public const string SongRenamed = "SongRenamed";
    public const string SongArtUpdated = "SongArtUpdated";
    public const string LyricsAdded = "LyricsAdded";
    public const string LyricsPublished = "LyricsPublished";
    public const string ChargebackReceived = "ChargebackReceived";
    public const string PersonaEnabled = "PersonaEnabled";
    public const string PersonaDisabled = "PersonaDisabled";
    public const string PersonaCreated = "PersonaCreated";
    public const string PersonaUpdated = "PersonaUpdated";
    public const string PersonaDeleted = "PersonaDeleted";
    public const string CreatorLandingViewed = "CreatorLandingViewed";
    public const string CreatorRegisterClicked = "CreatorRegisterClicked";
    public const string CreatorAccountRegistered = "CreatorAccountRegistered";
    public const string CreatorSettingsViewed = "CreatorSettingsViewed";
    public const string CreatorSignupStarted = "CreatorSignupStarted";
    public const string CreatorPayoutRequirementsAcknowledged = "CreatorPayoutRequirementsAcknowledged";
    public const string CreatorTaxFormLoaded = "CreatorTaxFormLoaded";
    public const string CreatorTaxFormCompletedOrReturned = "CreatorTaxFormCompletedOrReturned";
    public const string CreatorActivated = "CreatorActivated";
    public const string SubscriberCtaViewed = "SubscriberCtaViewed";
    public const string SubscriberRegisterClicked = "SubscriberRegisterClicked";
    public const string SubscriberLoginClicked = "SubscriberLoginClicked";
    public const string SubscriberSubscribeClicked = "SubscriberSubscribeClicked";
    public const string SubscriberManageAccountViewed = "SubscriberManageAccountViewed";
    public const string ArtistFollowed = "ArtistFollowed";
    public const string ArtistUnfollowed = "ArtistUnfollowed";
    public const string ArtistBlocked = "ArtistBlocked";
    public const string ArtistUnblocked = "ArtistUnblocked";
    public const string ArtistThankYouSent = "ArtistThankYouSent";
    public const string ArtistMessageReported = "ArtistMessageReported";
}
