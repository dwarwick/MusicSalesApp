namespace MusicSalesApp.Services;

/// <summary>
/// Service for matching audio files with cover art image files using AI.
/// </summary>
public interface IFileMatchingService
{
    /// <summary>
    /// Uses AI to match audio files with their corresponding cover art image files
    /// based on filename similarity, even when the names are not identical.
    /// </summary>
    /// <param name="audioFileNames">Original filenames of the audio files (e.g., "dark_night_mastered.mp3").</param>
    /// <param name="imageFileNames">Original filenames of the image files (e.g., "DarkNight.jpg").</param>
    /// <returns>
    /// A <see cref="FileMatchingResult"/> containing matched pairs (each with a normalized clean name)
    /// and any image files that could not be matched to an audio file.
    /// </returns>
    Task<FileMatchingResult> MatchFilesAsync(
        IEnumerable<string> audioFileNames,
        IEnumerable<string> imageFileNames);

    /// <summary>
    /// Uses AI to match audio files with their corresponding cover art image files.
    /// When image filenames look like GUIDs or other nonsense text (and the audio files do not),
    /// the image bytes are used to extract text via vision OCR for better matching.
    /// </summary>
    /// <param name="audioFileNames">Original filenames of the audio files.</param>
    /// <param name="imageFileNames">Original filenames of the image files.</param>
    /// <param name="imageData">
    /// Optional map of image filename → (raw bytes, MIME content type).
    /// Provide this to enable vision-based OCR fallback for nonsense filenames.
    /// </param>
    /// <returns>
    /// A <see cref="FileMatchingResult"/> containing matched pairs and unmatched image files.
    /// </returns>
    Task<FileMatchingResult> MatchFilesAsync(
        IEnumerable<string> audioFileNames,
        IEnumerable<string> imageFileNames,
        IReadOnlyDictionary<string, (byte[] Data, string ContentType)> imageData);
}


/// <summary>
/// Result of the AI file matching operation.
/// </summary>
public class FileMatchingResult
{
    /// <summary>
    /// Matched audio/image pairs, each with a normalized clean name to use for storage.
    /// Audio files with no matching image are also included here with ImageFileName = null.
    /// </summary>
    public List<FilePair> Pairs { get; set; } = new();

    /// <summary>
    /// Image files that could not be matched to any audio file.
    /// </summary>
    public List<string> UnmatchedImageFiles { get; set; } = new();
}

/// <summary>
/// A matched (or unmatched) audio/image pair.
/// </summary>
public class FilePair
{
    /// <summary>Original audio filename (e.g., "dark_night_mastered.mp3").</summary>
    public string AudioFileName { get; set; } = string.Empty;

    /// <summary>Original image filename, or null if no image was matched.</summary>
    public string ImageFileName { get; set; }

    /// <summary>
    /// Clean normalized name to use for both files (e.g., "Dark Night").
    /// Words separated by single spaces, proper title casing.
    /// </summary>
    public string NormalizedName { get; set; } = string.Empty;
}
