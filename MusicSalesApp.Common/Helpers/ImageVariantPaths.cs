using System.Globalization;

#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Builds and recognizes the pre-resized WebP renditions generated for cover art and persona
/// images.
///
/// A rendition is the complete base blob path with <c>.w{width}.webp</c> appended:
///
/// <code>
/// {guid}/{guid}-coverart.jpg     → {guid}/{guid}-coverart.jpg.w320.webp
/// Night Drive/Night Drive.png    → Night Drive/Night Drive.png.w320.webp
/// creator-12/persona-7.png       → creator-12/persona-7.png.w128.webp
/// </code>
///
/// The doubled extension is deliberate. Reverse recognition has to recover the base path
/// <em>exactly</em>, and a name like <c>{guid}-coverart-320.webp</c> cannot say whether the master
/// is <c>.jpg</c> or <c>.png</c>. Keeping the whole base path as a verbatim prefix makes the
/// transform a pure append/strip that behaves identically for GUID-scheme art, legacy name-based
/// art (see <see cref="SongMediaPaths"/>) and persona images — one code path, no scheme branch,
/// and <c>GetByBlobPathAsync</c> keeps working unchanged because the base path is still spelled
/// out in full.
/// </summary>
public static class ImageVariantPaths
{
    /// <summary>The extension every rendition carries.</summary>
    public const string VariantExtension = ".webp";

    /// <summary>The content type every rendition is stored and served with.</summary>
    public const string VariantContentType = "image/webp";

    /// <summary>Separates the base path from the rendition width, e.g. <c>cover.jpg</c> + <c>.w320</c>.</summary>
    private const string WidthMarker = ".w";

    /// <summary>The widest rendition the parser will accept, as a sanity bound on hostile input.</summary>
    private const int MaxPlausibleWidth = 10000;

    /// <summary>Forward derivation: base blob path plus a width gives the rendition's blob path.</summary>
    /// <exception cref="ArgumentException">The base path is blank or the width is not positive.</exception>
    public static string Variant(string baseBlobPath, int width)
    {
        if (string.IsNullOrWhiteSpace(baseBlobPath))
            throw new ArgumentException("A base blob path is required.", nameof(baseBlobPath));

        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");

        var normalized = Normalize(baseBlobPath);
        return string.Concat(
            normalized,
            WidthMarker,
            width.ToString(CultureInfo.InvariantCulture),
            VariantExtension);
    }

    /// <summary>
    /// Reverse recognition: splits a rendition path back into the base path it was derived from and
    /// the width it was generated at. Returns <see langword="false"/> for anything else, including a
    /// blob that merely happens to be a <c>.webp</c>.
    /// </summary>
    public static bool TryParseVariant(string? blobPath, out string baseBlobPath, out int width)
    {
        baseBlobPath = string.Empty;
        width = 0;

        if (string.IsNullOrWhiteSpace(blobPath))
            return false;

        // Defence in depth. The callers that matter derive this path from a database value, but the
        // public media whitelist feeds us whatever arrived on the wire.
        if (blobPath.Contains("..", StringComparison.Ordinal) || blobPath.Contains('~'))
            return false;

        var normalized = Normalize(blobPath);

        if (!normalized.EndsWith(VariantExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        var withoutExtension = normalized[..^VariantExtension.Length];

        var markerIndex = withoutExtension.LastIndexOf(WidthMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
            return false;

        var digits = withoutExtension[(markerIndex + WidthMarker.Length)..];
        if (digits.Length == 0 || digits.Length > 5)
            return false;

        // Reject a leading zero so a single rendition cannot be addressed as both "w320" and
        // "w0320" — two cache entries and two whitelist decisions for one blob.
        if (digits[0] == '0')
            return false;

        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWidth))
            return false;

        if (parsedWidth <= 0 || parsedWidth > MaxPlausibleWidth)
            return false;

        var parsedBase = withoutExtension[..markerIndex];
        if (string.IsNullOrWhiteSpace(parsedBase))
            return false;

        baseBlobPath = parsedBase;
        width = parsedWidth;
        return true;
    }

    /// <summary>Whether <paramref name="blobPath"/> names a rendition rather than a master.</summary>
    public static bool IsVariantPath(string? blobPath) => TryParseVariant(blobPath, out _, out _);

    /// <summary>Every rendition path for a base path, in the order the widths were supplied.</summary>
    public static IReadOnlyList<string> VariantsFor(string baseBlobPath, IEnumerable<int> widths)
    {
        if (string.IsNullOrWhiteSpace(baseBlobPath) || widths == null)
            return Array.Empty<string>();

        var paths = new List<string>();
        foreach (var width in widths)
        {
            if (width > 0)
                paths.Add(Variant(baseBlobPath, width));
        }

        return paths;
    }

    private static string Normalize(string blobPath)
        => blobPath.Replace('\\', '/').TrimStart('/');
}
