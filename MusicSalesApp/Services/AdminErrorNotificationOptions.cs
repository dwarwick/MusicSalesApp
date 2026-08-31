#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// Configuration for the admin error notification pipeline, bound from the
/// <see cref="SectionName"/> section.
/// </summary>
public class AdminErrorNotificationOptions
{
    public const string SectionName = "AdminErrorNotifications";

    /// <summary>
    /// Fallback recipient when neither this section nor EmailSettings:AdminEmail supplies one.
    /// </summary>
    public const string DefaultToEmail = "admin@streamtunes.net";

    public bool Enabled { get; set; } = true;

    public string? ToEmail { get; set; }

    /// <summary>
    /// How long a distinct error signature stays suppressed after an email is sent for it.
    /// Repeats inside the window are counted and reported once, so a stuck loop produces one
    /// follow-up rather than thousands of messages - which is the failure mode that trains an
    /// admin to ignore the folder.
    /// </summary>
    public int ThrottleWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Bounded queue depth. Logging never blocks on a full queue; the overflow is counted and
    /// reported in the next email instead.
    /// </summary>
    public int QueueCapacity { get; set; } = 512;

    /// <summary>
    /// Log categories that must never trigger a notification.
    ///
    /// The pipeline cannot email about its own failures without recursing, and it cannot email
    /// about the email service being down at all. Anything logged by these categories is dropped
    /// before it reaches the queue.
    /// </summary>
    public List<string> ExcludedCategoryPrefixes { get; set; } =
    [
        // A prefix, deliberately: it covers the dispatcher, sink, queue and throttle at once.
        // Only the namespace half can be derived, so that half is.
        $"{typeof(AdminErrorNotificationOptions).Namespace}.AdminErrorNotification",
        typeof(EmailService).FullName!
    ];

    public TimeSpan ThrottleWindow => TimeSpan.FromMinutes(Math.Max(1, ThrottleWindowMinutes));
}
