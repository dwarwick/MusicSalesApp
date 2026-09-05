#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

/// <summary>
/// One device a user can receive push notifications on.
/// </summary>
/// <remarks>
/// <para>
/// A row is a (device, user) pairing rather than a device: phones get handed over and accounts get
/// signed out, and a token left attached to the previous account would deliver one person's
/// notifications to another. Registering a token that already exists therefore REASSIGNS it rather
/// than creating a second row, which is why <see cref="Token"/> is uniquely indexed.
/// </para>
/// <para>
/// Tokens rot on their own - an app reinstall, a restore to a new phone, or FCM simply rotating one
/// - and the services never tell you in advance. <see cref="IsActive"/> is cleared when a send is
/// rejected as unregistered, which is the only reliable signal either platform gives.
/// </para>
/// </remarks>
public class PushDeviceToken
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public ApplicationUser User { get; set; } = null!;

    /// <summary>
    /// One of <see cref="PushPlatforms"/>. Decides which transport delivers to this token.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// The FCM registration token, or the APNs device token as lowercase hex.
    /// </summary>
    /// <remarks>
    /// 512 rather than 256: FCM tokens have grown past 160 characters historically and the service
    /// documents no maximum, so the column is sized well clear of what is seen in practice. A
    /// truncated token fails silently forever, which is the worst way for this to break.
    /// </remarks>
    [Required]
    [MaxLength(512)]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// A stable per-install identifier from the client, when it can supply one.
    /// </summary>
    /// <remarks>
    /// Lets a token rotation update the existing row instead of leaving the old token behind as a
    /// second, permanently dead registration. Optional because it is a best-effort value on the
    /// client - the token itself is the identity that matters.
    /// </remarks>
    [MaxLength(128)]
    public string? DeviceId { get; set; }

    [Required]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last time the client re-registered this token. A device that has not checked in for a long
    /// time is a candidate for pruning, though nothing prunes on age today - a rejected send is a
    /// far more reliable signal than silence.
    /// </summary>
    [Required]
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public bool IsActive { get; set; } = true;

    public DateTime? DeactivatedAtUtc { get; set; }

    /// <summary>
    /// Why the token stopped being used - the rejection reason from the platform, or a sign-out.
    /// Kept because "why did this user stop getting notifications" is otherwise unanswerable.
    /// </summary>
    [MaxLength(100)]
    public string? DeactivationReason { get; set; }
}
