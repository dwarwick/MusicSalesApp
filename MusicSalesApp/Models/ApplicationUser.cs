using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

public class ApplicationUser : IdentityUser<int>
{
    // Track when the last verification email was sent
    public DateTime? LastVerificationEmailSent { get; set; }

    // User's preferred theme (Light or Dark)
    [MaxLength(10)]
    public string Theme { get; set; } = "Light";

    // Account suspension
    public bool IsSuspended { get; set; } = false;
    public DateTime? SuspendedAt { get; set; }

    // Subscription block (set automatically when a chargeback cancels a subscription)
    public bool IsSubscriptionBlocked { get; set; } = false;
    public DateTime? SubscriptionBlockedAt { get; set; }

    // Tip block (set automatically when a tip chargeback is received)
    public bool IsTipBlocked { get; set; } = false;
    public DateTime? TipBlockedAt { get; set; }

    // Email preferences - user opt-in to receive new song notification emails
    public bool ReceiveNewSongEmails { get; set; } = false;

    // Follow-feature email preferences. These default to TRUE where ReceiveNewSongEmails defaults
    // to false, and the difference is deliberate: the site-wide digest is unsolicited mail about
    // the whole catalogue, whereas these only ever fire for an artist the listener chose to
    // follow. Following is the opt-in. Both are still switchable at /manage-account, and a
    // per-artist mute lives on ArtistFollower for going quiet without unfollowing.
    //
    // There is no in-app equivalent of these flags: the notification row IS the in-app
    // notification, and the per-artist mute already suppresses it.
    public bool ReceiveArtistReleaseEmails { get; set; } = true;

    public bool ReceiveArtistMessageEmails { get; set; } = true;

    // The push counterparts. Separate from the email flags because the channels are genuinely
    // different: a listener may well want a phone alert for a new release but no mail about it,
    // and collapsing the two would force one choice on both.
    public bool ReceiveArtistReleasePush { get; set; } = true;

    public bool ReceiveArtistMessagePush { get; set; } = true;

    // Last known browser timezone from the user, stored as an IANA timezone ID.
    [MaxLength(100)]
    public string TimeZoneId { get; set; }

    // Sign in with Apple refresh token, captured at sign-in purely so the account-deletion path
    // can call Apple's revoke endpoint - Apple requires that of any app offering Sign in with
    // Apple. Deliberately NOT protected with ASP.NET Data Protection: that key ring is excluded
    // from backup because everything it protects is transient, and this token has to survive for
    // the life of the account. Null for every user who did not sign in with Apple.
    [MaxLength(512)]
    public string AppleRefreshToken { get; set; }
}
