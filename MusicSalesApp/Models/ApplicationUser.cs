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

    // Follow-feature notification preferences, all four OFF by default - the same posture as
    // ReceiveNewSongEmails beside them. Following an artist is consent to the in-app record, which
    // is the row itself and has no switch; it is not consent to be mailed or to have a phone buzz.
    // Each channel is asked for separately at /manage-account, and a per-artist mute lives on
    // ArtistFollower for going quiet without unfollowing.
    public bool ReceiveArtistReleaseEmails { get; set; }

    public bool ReceiveArtistMessageEmails { get; set; }

    // The push counterparts. Separate from the email flags because the channels are genuinely
    // different: a listener may well want a phone alert for a new release but no mail about it,
    // and collapsing the two would force one choice on both.
    public bool ReceiveArtistReleasePush { get; set; }

    public bool ReceiveArtistMessagePush { get; set; }

    // How often the push channel may interrupt, as an ArtistPushFrequency. Instant (0) is the
    // default and is what everyone had before this column existed, so an un-migrated row and an
    // untouched preference mean the same thing. Enforced in ArtistPushDispatchService: anything
    // other than Instant holds this listener's pending rows until the oldest has waited a full
    // window, then sends one summary. It governs BOTH releases and artist messages - the two
    // booleans above still decide whether each kind is sent at all.
    public int ArtistPushFrequency { get; set; }

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
