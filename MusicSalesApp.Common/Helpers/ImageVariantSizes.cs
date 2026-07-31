using System.Globalization;

#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// The rendition widths generated for cover art and persona images, and the CSV encoding used to
/// record which of them actually exist for a given image.
///
/// <para>
/// The widths are derived from what the two apps really display. Web sizes are CSS pixels scaled by
/// device pixel ratio; mobile sizes are device-independent units scaled by display density (up to
/// 4x on Android, 3x on iOS):
/// </para>
///
/// <list type="bullet">
/// <item><c>128</c> — the web playlist track thumbnail (40 CSS px) at every DPR, and the small
/// persona avatars (40/60 CSS px) at 2x.</item>
/// <item><c>320</c> — <b>the rendition mobile always caches.</b> Covers the app's 48-DIP track rows
/// and 36-DIP mini player outright, and the web player artwork at 2x.</item>
/// <item><c>640</c> — <b>the rendition mobile caches when budget allows.</b> Covers the app's
/// 150-DIP song cards and 180-DIP player hero, and web card art at 2x on desktop.</item>
/// <item><c>1024</c> — the phone-width single-column library grid, which renders card art at up to
/// ~520 CSS px and therefore wants ~1040 device pixels at 2x.</item>
/// </list>
///
/// <para>
/// Persona images stop at 640 because nothing displays one larger than 200 CSS px / 120 DIP.
/// </para>
/// </summary>
public static class ImageVariantSizes
{
    /// <summary>Rendition widths generated for song cover art, ascending.</summary>
    public static readonly IReadOnlyList<int> CoverArt = new[] { 128, 320, 640, 1024 };

    /// <summary>Rendition widths generated for creator persona images, ascending.</summary>
    public static readonly IReadOnlyList<int> Persona = new[] { 128, 320, 640 };

    /// <summary>
    /// The rendition the mobile app caches unconditionally. Small enough that a full library's
    /// artwork fits the image cache budget, and large enough to stand in for the hero when that
    /// rendition is missing or the budget is exhausted.
    /// </summary>
    public const int MobileThumbWidth = 320;

    /// <summary>
    /// The rendition the mobile app caches on a best-effort basis, for the song cards and the
    /// player hero. Falls back to <see cref="MobileThumbWidth"/> when absent.
    /// </summary>
    public const int MobileHeroWidth = 640;

    private const char Separator = ',';

    /// <summary>Encodes a set of generated widths for storage, e.g. <c>"128,320,640"</c>.</summary>
    public static string ToCsv(IEnumerable<int>? widths)
    {
        if (widths == null)
            return string.Empty;

        var parts = new List<string>();
        foreach (var width in widths)
        {
            if (width > 0)
                parts.Add(width.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(Separator, parts);
    }

    /// <summary>
    /// Decodes a stored width set. Malformed entries are skipped rather than thrown on: this value
    /// is only ever an optimisation hint, and a parse failure must degrade to "serve the master"
    /// rather than break the page.
    /// </summary>
    public static IReadOnlyList<int> ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Array.Empty<int>();

        var widths = new List<int>();
        foreach (var part in csv.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                && width > 0
                && !widths.Contains(width))
            {
                widths.Add(width);
            }
        }

        return widths;
    }

    /// <summary>Whether a stored width set includes a particular rendition.</summary>
    public static bool CsvContains(string? csv, int width)
    {
        if (width <= 0 || string.IsNullOrWhiteSpace(csv))
            return false;

        foreach (var candidate in ParseCsv(csv))
        {
            if (candidate == width)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The smallest recorded rendition at least <paramref name="requiredWidth"/> wide, or
    /// <see langword="null"/> when the set is empty or every rendition is too small — in which case
    /// the caller serves the master.
    /// </summary>
    public static int? SelectAtLeast(string? csv, int requiredWidth)
    {
        int? best = null;
        foreach (var candidate in ParseCsv(csv))
        {
            if (candidate >= requiredWidth && (best == null || candidate < best))
                best = candidate;
        }

        return best;
    }

    /// <summary>Whether <paramref name="width"/> is a width this application generates for cover art.</summary>
    public static bool IsKnownCoverArtWidth(int width) => CoverArt.Contains(width);

    /// <summary>Whether <paramref name="width"/> is a width this application generates for persona images.</summary>
    public static bool IsKnownPersonaWidth(int width) => Persona.Contains(width);
}
