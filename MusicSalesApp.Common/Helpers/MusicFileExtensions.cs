using System.Linq;

namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Shared constants for valid audio and cover art file extensions,
/// used across upload, validation, and file-matching services.
/// </summary>
public static class MusicFileExtensions
{
    /// <summary>
    /// File extensions recognized as valid audio files (lower-case, including the dot).
    /// </summary>
    public static readonly string[] ValidAudioExtensions =
        { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };

    /// <summary>
    /// File extensions recognized as valid cover art / image files (lower-case, including the dot).
    /// </summary>
    public static readonly string[] ValidCoverArtExtensions = { ".jpeg", ".jpg", ".png" };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="fileName"/>'s extension
    /// (case-insensitive) is in <see cref="ValidAudioExtensions"/>.
    /// </summary>
    public static bool IsAudioFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ValidAudioExtensions.Contains(ext);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="fileName"/>'s extension
    /// (case-insensitive) is in <see cref="ValidCoverArtExtensions"/>.
    /// </summary>
    public static bool IsCoverArtFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ValidCoverArtExtensions.Contains(ext);
    }
}
