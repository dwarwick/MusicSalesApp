namespace MusicSalesApp.Services;

/// <summary>
/// Pairs audio files with the cover art uploaded beside them, by exact base-name comparison.
///
/// <para>
/// <b>This is the fallback path.</b> The real matching — vision OCR to read the text off each cover,
/// then one model call that pairs the whole batch — runs in the Azure Function, where the images are
/// already staged and the calls do not occupy a Blazor circuit. This is what the upload page uses
/// when that is unavailable: no queue configured, no answer inside the deadline, or an outright
/// failure.
/// </para>
///
/// <para>
/// It stays here rather than moving with the rest because a fallback that lives in the thing it is a
/// fallback for is no fallback at all. It is deterministic, free, and needs nothing configured.
/// </para>
/// </summary>
public interface IFileMatchingService
{
    /// <summary>
    /// Pairs each audio file with an image whose normalized base name matches exactly, ignoring
    /// extensions, case, underscore/hyphen spacing and a trailing <c>_mastered</c>.
    /// </summary>
    /// <param name="audioFileNames">Original audio filenames, e.g. <c>dark_night_mastered.mp3</c>.</param>
    /// <param name="imageFileNames">Original image filenames, e.g. <c>DarkNight.jpg</c>.</param>
    /// <returns>
    /// Every audio file, paired or not, plus the images that matched nothing. Audio files with no
    /// match are included with a null <see cref="FilePair.ImageFileName"/> rather than dropped.
    /// </returns>
    Task<FileMatchingResult> MatchFilesAsync(
        IEnumerable<string> audioFileNames,
        IEnumerable<string> imageFileNames);
}


/// <summary>
/// Result of a file matching operation, from either the Function or the local fallback.
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
