using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public class FileMatchingService : IFileMatchingService
{
    /// <inheritdoc />
    public Task<FileMatchingResult> MatchFilesAsync(
        IEnumerable<string> audioFileNames,
        IEnumerable<string> imageFileNames)
    {
        var audioList = audioFileNames?.ToList() ?? [];
        var imageList = imageFileNames?.ToList() ?? [];

        if (audioList.Count == 0)
        {
            return Task.FromResult(new FileMatchingResult { UnmatchedImageFiles = imageList });
        }

        var result = new FileMatchingResult();

        // First filename wins a collision. Two images normalizing to the same name is a creator
        // mistake, and picking arbitrarily is better than pairing one song with both.
        var imageByBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in imageList)
        {
            var key = FileNameMatching.ToNormalizedName(image);
            if (!string.IsNullOrEmpty(key))
            {
                imageByBaseName.TryAdd(key, image);
            }
        }

        var matchedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var audio in audioList)
        {
            var normalized = FileNameMatching.ToNormalizedName(audio);

            string matchedImage = null;
            if (imageByBaseName.TryGetValue(normalized, out var candidate) && matchedImages.Add(candidate))
            {
                matchedImage = candidate;
            }

            result.Pairs.Add(new FilePair
            {
                AudioFileName = audio,
                ImageFileName = matchedImage,
                NormalizedName = normalized
            });
        }

        result.UnmatchedImageFiles = imageList.Where(image => !matchedImages.Contains(image)).ToList();
        return Task.FromResult(result);
    }
}
