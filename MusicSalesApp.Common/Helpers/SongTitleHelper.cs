namespace MusicSalesApp.Common.Helpers;

#nullable enable

/// <summary>
/// Song title rules and derivation.
///
/// Titles used to double as filenames, so they were restricted to a character whitelist. Now that
/// storage paths come from a GUID (see <see cref="SongMediaPaths"/>), a title is just display text:
/// it must be present and fit the column, and nothing more.
/// </summary>
public static class SongTitleHelper
{
    public const int MaxTitleLength = 200;

    /// <summary>
    /// The title suggested for a newly uploaded file. Creators can edit it before uploading, so this
    /// is a convenience rather than a rule. Underscores become spaces because that is how titles
    /// were derived historically, and creators' existing filenames follow that habit.
    /// </summary>
    public static string FromFileName(string? fileName)
        => Path.GetFileNameWithoutExtension(fileName ?? string.Empty)
            .Replace('_', ' ')
            .Trim();

    public static IReadOnlyList<string> GetTitleValidationErrors(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new[] { "A song title is required." };

        var trimmed = title.Trim();
        return trimmed.Length > MaxTitleLength
            ? new[] { $"The song title is {trimmed.Length} characters long; the maximum is {MaxTitleLength}." }
            : Array.Empty<string>();
    }

    public static bool IsValidTitle(string? title) => GetTitleValidationErrors(title).Count == 0;

    /// <summary>
    /// The title to display for a song, falling back to the filename for legacy songs that predate
    /// the stored-title requirement.
    ///
    /// GUID-scheme paths are skipped: deriving from one would surface a raw GUID as a song title.
    /// Those songs always carry a real title (enforced by <c>UpsertValidatedUploadAsync</c> and the
    /// <c>CK_SongMetadata_AudioRequiresTitle</c> constraint), so the fallback is not needed for them.
    /// </summary>
    public static string GetEffectiveTitle(string? songTitle, params string?[] blobPaths)
    {
        if (!string.IsNullOrWhiteSpace(songTitle))
            return songTitle.Trim();

        foreach (var blobPath in blobPaths)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                continue;

            if (SongMediaPaths.IsGuidScheme(blobPath))
                continue;

            var derivedTitle = FromFileName(blobPath.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(derivedTitle))
                return derivedTitle;
        }

        return string.Empty;
    }
}
