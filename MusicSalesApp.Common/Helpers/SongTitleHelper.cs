namespace MusicSalesApp.Common.Helpers;

#nullable enable

public static class SongTitleHelper
{
    public static string GetEffectiveTitle(string? songTitle, params string?[] blobPaths)
    {
        if (!string.IsNullOrWhiteSpace(songTitle))
            return songTitle.Trim();

        foreach (var blobPath in blobPaths)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                continue;

            var fileName = Path.GetFileName(blobPath.Replace('\\', '/'));
            var derivedTitle = Path.GetFileNameWithoutExtension(fileName);
            if (!string.IsNullOrWhiteSpace(derivedTitle))
                return MediaFileNameRules.ToSongTitleFromBaseName(derivedTitle);
        }

        return string.Empty;
    }
}
