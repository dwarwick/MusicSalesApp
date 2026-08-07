#nullable enable

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Turning a creator's filename into something comparable, and into the title a song is seeded with.
///
/// <para>
/// Shared because cover-art matching happens in two places that must agree. The Azure Function does
/// it with a vision-and-pairing model; the web app does it with exact base-name comparison whenever
/// that is unavailable. If the two normalised names differently, the same upload would arrive with a
/// different title depending on which path ran — so both call this.
/// </para>
///
/// <para>
/// Pure string work with no dependencies, which is what lets it live in a library the MAUI app also
/// references.
/// </para>
/// </summary>
public static class FileNameMatching
{
    /// <summary>
    /// Mastering tools routinely append this, and creators do not think of it as part of the song's
    /// name — so <c>dark_night_mastered.wav</c> and <c>dark_night.jpg</c> have to pair up.
    /// </summary>
    private const string MasteredSuffix = "_mastered";

    /// <summary>The filename without its extension, and without a trailing <c>_mastered</c>.</summary>
    public static string GetBaseNameWithoutExtension(string? fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (baseName.EndsWith(MasteredSuffix, StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^MasteredSuffix.Length];
        return baseName;
    }

    /// <summary>
    /// Converts a raw base name — where underscores and hyphens stand in for spaces — into a clean
    /// title-cased name. Lowercased first, so an ALL-CAPS filename does not stay shouting.
    /// </summary>
    public static string NormalizeBaseName(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return string.Empty;

        var spaced = baseName.Replace('_', ' ').Replace('-', ' ');

        while (spaced.Contains("  ", StringComparison.Ordinal))
            spaced = spaced.Replace("  ", " ", StringComparison.Ordinal);

        spaced = spaced.Trim();
        if (spaced.Length == 0)
            return string.Empty;

        var words = spaced.ToLowerInvariant().Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Length == 0)
                continue;
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        }

        return string.Join(' ', words);
    }

    /// <summary>Both steps at once: the usual way callers turn a filename into a candidate title.</summary>
    public static string ToNormalizedName(string? fileName)
        => NormalizeBaseName(GetBaseNameWithoutExtension(fileName));
}
