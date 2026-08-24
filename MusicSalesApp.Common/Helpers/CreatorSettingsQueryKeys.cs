namespace MusicSalesApp.Common.Helpers;

public static class CreatorSettingsQueryKeys
{
    // creator_activated and creator_deactivated used to live here. They were replaced
    // by Creator.ActivationAnnouncedAt / DeactivationAnnouncedAt, because a URL that
    // triggers analytics and a history row can be replayed by anyone who reads it.
    public const string CreatorOnboarding = "creator_onboarding";
    public const string TrackingId = "tracking_id";
}
