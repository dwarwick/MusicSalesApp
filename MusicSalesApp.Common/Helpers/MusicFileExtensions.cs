using System.Collections.ObjectModel;
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

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AudioMimeTypes =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [".mp3"] = new[] { "audio/mpeg" },
                [".wav"] = new[] { "audio/wav", "audio/wave", "audio/x-wav" },
                [".flac"] = new[] { "audio/flac" },
                [".ogg"] = new[] { "audio/ogg" },
                [".m4a"] = new[] { "audio/mp4", "audio/x-m4a" },
                [".aac"] = new[] { "audio/aac" },
                [".wma"] = new[] { "audio/x-ms-wma" }
            });

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> CoverArtMimeTypes =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [".jpg"] = new[] { "image/jpeg" },
                [".jpeg"] = new[] { "image/jpeg" },
                [".png"] = new[] { "image/png" }
            });

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

    public static string GetAudioContentType(string fileNameOrExtension)
        => GetContentType(fileNameOrExtension, AudioMimeTypes, "application/octet-stream");

    public static string GetCoverArtContentType(string fileNameOrExtension)
        => GetContentType(fileNameOrExtension, CoverArtMimeTypes, "application/octet-stream");

    private static string GetContentType(
        string fileNameOrExtension,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mappings,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExtension))
            return fallback;

        var extension = fileNameOrExtension.StartsWith('.')
            ? fileNameOrExtension
            : System.IO.Path.GetExtension(fileNameOrExtension);

        return mappings.TryGetValue(extension, out var mimeTypes) && mimeTypes.Count > 0
            ? mimeTypes[0]
            : fallback;
    }
}
