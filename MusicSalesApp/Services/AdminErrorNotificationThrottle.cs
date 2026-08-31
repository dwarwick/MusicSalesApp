#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// One error worth emailing: either a first occurrence, or a follow-up summarising the repeats
/// that were suppressed during a throttle window.
/// </summary>
public record AdminErrorNotification(
    AdminErrorNotice Notice,
    int SuppressedCount,
    bool IsFollowUp);

/// <summary>
/// Decides which error notices become emails.
///
/// Pure and synchronous by design - the throttling rules are the part worth testing directly,
/// separate from channels, timers and SMTP.
/// </summary>
public class AdminErrorNotificationThrottle
{
    private sealed class SignatureState
    {
        public DateTimeOffset LastEmailedUtc { get; set; }
        public int SuppressedCount { get; set; }
        public AdminErrorNotice? LatestNotice { get; set; }
    }

    private readonly Dictionary<string, SignatureState> _states = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public AdminErrorNotificationThrottle(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>
    /// Records an occurrence. Returns the notification to send now, or null when the signature is
    /// inside its throttle window and the occurrence was folded into the running count.
    /// </summary>
    public AdminErrorNotification? Admit(AdminErrorNotice notice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (!_states.TryGetValue(notice.Signature, out var state))
        {
            _states[notice.Signature] = new SignatureState
            {
                LastEmailedUtc = now,
                SuppressedCount = 0,
                LatestNotice = notice
            };

            return new AdminErrorNotification(notice, SuppressedCount: 0, IsFollowUp: false);
        }

        state.LatestNotice = notice;

        if (now - state.LastEmailedUtc < _window)
        {
            state.SuppressedCount++;
            return null;
        }

        var suppressed = state.SuppressedCount;
        state.SuppressedCount = 0;
        state.LastEmailedUtc = now;
        return new AdminErrorNotification(notice, suppressed, IsFollowUp: false);
    }

    /// <summary>
    /// Records that a notification was admitted but never delivered.
    ///
    /// <para>
    /// The window is deliberately still burnt - retrying a 30-second SMTP send per occurrence
    /// while the mail host is down would back up the drain loop until it dropped genuine notices.
    /// But the signature is left owing a follow-up, so the alert resurfaces once rather than
    /// vanishing. Without this, a one-off Fatal that failed to send was pruned an hour later and
    /// lost for good: the pipeline's own blind spot, in the shape it was built to prevent.
    /// </para>
    /// </summary>
    public void MarkDeliveryFailed(AdminErrorNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_states.TryGetValue(notification.Notice.Signature, out var state))
        {
            state.SuppressedCount = Math.Max(state.SuppressedCount, 1) + notification.SuppressedCount;
        }
    }

    /// <summary>
    /// Returns summaries for signatures whose window has elapsed with repeats still uncounted, so
    /// a fault that stops recurring is still reported rather than sitting in the counter forever.
    /// Also prunes signatures that are no longer throttling anything.
    /// </summary>
    public IReadOnlyList<AdminErrorNotification> CollectDueFollowUps(DateTimeOffset now)
    {
        List<AdminErrorNotification>? due = null;
        List<string>? expired = null;

        foreach (var (signature, state) in _states)
        {
            if (now - state.LastEmailedUtc < _window)
            {
                continue;
            }

            if (state.SuppressedCount > 0 && state.LatestNotice != null)
            {
                due ??= [];
                due.Add(new AdminErrorNotification(
                    state.LatestNotice,
                    state.SuppressedCount,
                    IsFollowUp: true));

                state.SuppressedCount = 0;
                state.LastEmailedUtc = now;
                continue;
            }

            // Nothing suppressed and the window has passed, so this entry no longer changes any
            // decision: the next occurrence would email immediately either way. Drop it so the
            // dictionary tracks only what is actively being throttled.
            expired ??= [];
            expired.Add(signature);
        }

        if (expired != null)
        {
            foreach (var signature in expired)
            {
                _states.Remove(signature);
            }
        }

        return due ?? (IReadOnlyList<AdminErrorNotification>)[];
    }
}
