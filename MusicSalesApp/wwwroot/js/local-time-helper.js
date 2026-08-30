// Formats a UTC instant in the VIEWER's timezone.
//
// Server-side formatting cannot do this job here. UserTimeZoneDisplayHelper reads
// ApplicationUser.TimeZoneId, and the surfaces this is used on - the home page's "Most Streamed"
// section - are shown to anonymous visitors who have no user record at all, so every one of them
// would be told the time in UTC. The browser is the only party that knows.

/**
 * @param {string} isoUtc ISO-8601 UTC instant, e.g. "2026-08-29T02:00:00Z".
 * @returns {string} e.g. "08/29/2026 at 2:00 AM", or "" when the input is unusable.
 */
export function formatUtcInLocalTime(isoUtc) {
    if (!isoUtc) {
        return '';
    }

    const instant = new Date(isoUtc);
    if (Number.isNaN(instant.getTime())) {
        return '';
    }

    // Undefined locale rather than a hardcoded one, so a reader outside the US gets their own
    // day/month order instead of the American one.
    const datePart = instant.toLocaleDateString(undefined, {
        year: 'numeric',
        month: '2-digit',
        day: '2-digit'
    });
    const timePart = instant.toLocaleTimeString(undefined, {
        hour: 'numeric',
        minute: '2-digit'
    });

    return `${datePart} at ${timePart}`;
}
