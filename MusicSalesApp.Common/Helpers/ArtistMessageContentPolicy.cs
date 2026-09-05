using System.Text;
using System.Text.RegularExpressions;

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Validates the free text a creator may send to one of their followers.
///
/// <para>
/// The follow feature's core promise is that a creator never learns how to reach a listener
/// outside StreamTunes. A short message field is the one place a creator can type arbitrary
/// text that a listener will read, so it is the one place that promise can be broken - by the
/// creator handing over their OWN contact details and inviting a reply off-platform. That is
/// what this class exists to stop; it is not a profanity filter.
/// </para>
///
/// <para>
/// This lives in Common so the Blazor dialog, the API controller and the mobile client all
/// apply identical rules. The service layer is the enforcement point: a client-side check is
/// a courtesy, never the guard.
/// </para>
/// </summary>
public static class ArtistMessageContentPolicy
{
    /// <summary>Maximum length of a message, measured after normalisation.</summary>
    public const int MaxLength = 200;

    private const RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // A bare "name@host" anywhere in the text.
    private static readonly Regex EmailPattern =
        new(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+", Opts, MatchTimeout);

    // "dave at gmail dot com", "dave (at) gmail [dot] com", "dave AT gmail DOT com".
    private static readonly Regex ObfuscatedAtPattern =
        new(@"[\(\[\{]?\bat\b[\)\]\}]?", Opts, MatchTimeout);

    private static readonly Regex ObfuscatedDotPattern =
        new(@"[\(\[\{]?\bd[o0]t\b[\)\]\}]?", Opts, MatchTimeout);

    private static readonly Regex UrlSchemePattern =
        new(@"\b(?:https?|ftp)\s*:\s*/\s*/", Opts, MatchTimeout);

    private static readonly Regex WwwPattern =
        new(@"\bwww\s*\.", Opts, MatchTimeout);

    // "example.com", "my-band.co.uk" - a label followed by a known TLD.
    //
    // The dot must be tight against both labels. Allowing whitespace around it turns ordinary
    // prose into a rejection: "Thanks. me too" and "Love it. Its great" both read as a domain
    // once a space is permitted, because "me" and "it" are real TLDs. Requiring "word.tld" is
    // what a typed domain actually looks like, and the spaced-out evasion it gives up is
    // covered by SpacedDomainPattern below.
    private static readonly Regex DomainPattern = new(
        @"\b[\p{L}\p{N}][\p{L}\p{N}\-]*\.(?:com|net|org|io|co|me|ly|app|fm|tv|xyz|link|page|site|shop|store|info|biz|uk|ca|au|de|nl|dk|nz|gg|to|cc|club|live|online|music|band|art|dev|ai)\b",
        Opts, MatchTimeout);

    // The deliberate "mysite . com" evasion. Restricted to the four TLDs that are never English
    // words, so a sentence boundary followed by a short word cannot trip it.
    private static readonly Regex SpacedDomainPattern = new(
        @"\b[\p{L}\p{N}][\p{L}\p{N}\-]*\s+\.\s*(?:com|net|org|io)\b|\b[\p{L}\p{N}][\p{L}\p{N}\-]*\.\s+(?:com|net|org|io)\b",
        Opts, MatchTimeout);

    // A social handle: @ followed by a plausible username, where the @ is not part of an email.
    private static readonly Regex HandlePattern =
        new(@"(?<![\p{L}\p{N}._%+\-])@[\p{L}\p{N}._\-]{2,}", Opts, MatchTimeout);

    private static readonly Regex SolicitationPattern = new(
        @"\b(?:e-?mail|d\.?m|text|call|phone|message|contact|reach|find|add|hit|ping)\s+(?:me|us)\b",
        Opts, MatchTimeout);

    private static readonly Regex PlatformPattern = new(
        @"\b(?:instagram|insta|ig|facebook|fb|tiktok|tik\s*tok|twitter|snapchat|snap|whats\s*app|telegram|discord|venmo|cash\s*app|paypal|patreon|onlyfans|only\s*fans|linktree|link\s*tree|ko-?fi|bandcamp|soundcloud|youtube|substack|twitch|reddit|gmail|hotmail|outlook|yahoo|proton\s*mail|icloud)\b",
        Opts, MatchTimeout);

    /// <summary>
    /// Characters used to split a blocked token so a naive matcher misses it - the standard
    /// evasion for this kind of filter. They are removed before any pattern runs, so a
    /// zero-width-split "gmail.com" is tested as "gmail.com".
    /// </summary>
    /// <remarks>
    /// Held as code points rather than char literals on purpose: written as literals these are
    /// invisible in the source file, so a later edit can delete or mangle one without anything
    /// looking wrong on screen.
    /// </remarks>
    private static readonly int[] InvisibleCodePoints =
    [
        0x200B, // zero width space
        0x200C, // zero width non-joiner
        0x200D, // zero width joiner
        0x2060, // word joiner
        0xFEFF, // zero width no-break space
        0x00AD, // soft hyphen
        0x180E, // Mongolian vowel separator
    ];

    /// <summary>
    /// Normalises and validates a creator-authored message.
    /// </summary>
    /// <param name="text">The raw text as typed.</param>
    /// <param name="normalized">
    /// The text to persist when the result is true: invisible characters stripped, whitespace
    /// runs collapsed to single spaces, trimmed. Empty string when the result is false.
    /// </param>
    /// <param name="rejectionReason">
    /// A short, user-facing explanation when the result is false; empty when true. It is
    /// deliberately specific ("Messages cannot contain links") so the sender can fix the text
    /// rather than guess.
    /// </param>
    /// <returns>True when the message may be sent.</returns>
    public static bool TryValidate(string text, out string normalized, out string rejectionReason)
    {
        normalized = string.Empty;
        rejectionReason = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            rejectionReason = "Enter a message before sending.";
            return false;
        }

        var candidate = Normalize(text);

        if (candidate.Length == 0)
        {
            rejectionReason = "Enter a message before sending.";
            return false;
        }

        if (candidate.Length > MaxLength)
        {
            rejectionReason = $"Messages are limited to {MaxLength} characters.";
            return false;
        }

        // Every check below runs against the NORMALISED text, so an evasion that relied on
        // invisible characters or padded whitespace has already been undone.
        if (EmailPattern.IsMatch(candidate))
        {
            rejectionReason = "Messages cannot contain an email address.";
            return false;
        }

        if (ObfuscatedAtPattern.IsMatch(candidate) && ObfuscatedDotPattern.IsMatch(candidate))
        {
            rejectionReason = "Messages cannot contain an email address.";
            return false;
        }

        if (UrlSchemePattern.IsMatch(candidate) || WwwPattern.IsMatch(candidate)
            || DomainPattern.IsMatch(candidate) || SpacedDomainPattern.IsMatch(candidate))
        {
            rejectionReason = "Messages cannot contain links or web addresses.";
            return false;
        }

        if (ContainsPhoneNumber(candidate))
        {
            rejectionReason = "Messages cannot contain a phone number.";
            return false;
        }

        if (HandlePattern.IsMatch(candidate))
        {
            rejectionReason = "Messages cannot contain social media handles.";
            return false;
        }

        if (PlatformPattern.IsMatch(candidate))
        {
            rejectionReason = "Messages cannot name other platforms or contact services.";
            return false;
        }

        if (SolicitationPattern.IsMatch(candidate))
        {
            rejectionReason = "Messages cannot ask a listener to contact you elsewhere.";
            return false;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>
    /// Strips invisible characters, turns any other whitespace or control character into a
    /// single space, and trims. Exposed so a live character counter in the UI measures exactly
    /// what the validator will measure.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (Array.IndexOf(InvisibleCodePoints, (int)ch) >= 0)
            {
                continue;
            }

            // Control characters (including newlines and tabs) become a single space rather
            // than being dropped, so a NUL between two words cannot glue them together.
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the text carries a run of seven or more digits once the separators a person
    /// would type inside a phone number are ignored. Seven is the shortest real subscriber
    /// number, and counting across separators is what catches "555 123 4567".
    /// </summary>
    private static bool ContainsPhoneNumber(string candidate)
    {
        var run = 0;

        foreach (var ch in candidate)
        {
            if (char.IsDigit(ch))
            {
                run++;
                if (run >= 7)
                {
                    return true;
                }

                continue;
            }

            // Separators a person types inside a number do not break the run; anything else does.
            if (ch is ' ' or '-' or '.' or '(' or ')' or '+' or '/')
            {
                continue;
            }

            run = 0;
        }

        return false;
    }
}
