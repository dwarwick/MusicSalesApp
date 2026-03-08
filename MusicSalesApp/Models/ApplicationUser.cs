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

    // Email preferences - user opt-in to receive new song notification emails
    public bool ReceiveNewSongEmails { get; set; } = false;
}
