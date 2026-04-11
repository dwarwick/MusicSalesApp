using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Models;

/// <summary>
/// Stores 6-digit verification codes sent to mobile app users for
/// email verification and password reset flows.
/// </summary>
public class MobileVerificationCode
{
    public int Id { get; set; }

    /// <summary>FK to ApplicationUser.</summary>
    public int UserId { get; set; }

    /// <summary>The 6-digit code (zero-padded, e.g. "042817").</summary>
    [MaxLength(6)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// The purpose of this code. Use constants from <see cref="MobileVerificationPurpose"/>.
    /// </summary>
    [MaxLength(30)]
    public string Purpose { get; set; } = MobileVerificationPurpose.EmailVerification;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
