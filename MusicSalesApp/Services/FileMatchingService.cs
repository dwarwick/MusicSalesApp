using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace MusicSalesApp.Services;

/// <summary>
/// Uses OpenAI Chat Completions to intelligently match audio files with cover art image files
/// based on filename similarity. Falls back to exact base-name matching if OpenAI is unavailable.
/// When image filenames look like GUIDs or other nonsense, uses OpenAI Vision OCR to extract
/// text from the image and uses that for matching instead.
/// </summary>
public class FileMatchingService : IFileMatchingService
{
    private readonly ILogger<FileMatchingService> _logger;
    private readonly string _apiKey;
    private const string ChatModel = "gpt-4.1-mini";
    private const string VisionModel = "gpt-4.1-mini";

    private const string MasteredSuffix = "_mastered";

    // Detects GUID-like patterns: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx (with or without separators)
    private static readonly Regex GuidPattern = new Regex(
        @"^[0-9a-f]{8}[-_]?[0-9a-f]{4}[-_]?[0-9a-f]{4}[-_]?[0-9a-f]{4}[-_]?[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Detects purely hex strings (16+ chars, e.g. MD5 / SHA hashes)
    private static readonly Regex HexOnlyPattern = new Regex(
        @"^[0-9a-f]{16,}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Detects purely numeric names (6+ digits, e.g. IMG_20240101)
    private static readonly Regex AllNumericPattern = new Regex(
        @"^[0-9]{6,}$",
        RegexOptions.Compiled);

    public FileMatchingService(IConfiguration configuration, ILogger<FileMatchingService> logger)
    {
        _logger = logger;
        _apiKey = configuration["OpenAI:ApiKey"] ?? string.Empty;
    }

    private bool IsConfigured => !string.IsNullOrEmpty(_apiKey) && _apiKey != "__REPLACE_WITH_OPENAI_API_KEY__";

    /// <inheritdoc/>
    public Task<FileMatchingResult> MatchFilesAsync(
        IEnumerable<string> audioFileNames,
        IEnumerable<string> imageFileNames)
        => MatchFilesAsync(audioFileNames, imageFileNames, null);

    /// <inheritdoc/>
    public async Task<FileMatchingResult> MatchFilesAsync(
        IEnumerable<string> audioFileNames,
        IEnumerable<string> imageFileNames,
        IReadOnlyDictionary<string, (byte[] Data, string ContentType)> imageData)
    {
        var audioList = audioFileNames.ToList();
        var imageList = imageFileNames.ToList();

        if (!audioList.Any())
            return new FileMatchingResult { UnmatchedImageFiles = imageList };

        if (!imageList.Any())
        {
            return new FileMatchingResult
            {
                Pairs = audioList.Select(a => new FilePair
                {
                    AudioFileName = a,
                    ImageFileName = null,
                    NormalizedName = NormalizeBaseName(GetBaseNameWithoutExtension(a))
                }).ToList()
            };
        }

        if (IsConfigured)
        {
            try
            {
                // Resolve display names for images: use OCR text for nonsense filenames
                // (but only when the audio side doesn't also have nonsense names)
                var audioAllNonsense = audioList.All(a => IsNonsenseFilename(a));
                var imageDisplayNames = await ResolveImageDisplayNamesAsync(imageList, imageData, audioAllNonsense);
                return await MatchWithOpenAiAsync(audioList, imageList, imageDisplayNames);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI file matching failed; falling back to exact base-name matching.");
            }
        }
        else
        {
            _logger.LogInformation("OpenAI not configured; using exact base-name matching for file pairing.");
        }

        return FallbackExactMatch(audioList, imageList);
    }

    /// <summary>
    /// Builds a display-name map for each image file.
    /// Only stores an entry when OCR successfully extracts text from a nonsense filename.
    /// Images with normal filenames are not included (the filename itself is used for matching).
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveImageDisplayNamesAsync(
        List<string> imageFiles,
        IReadOnlyDictionary<string, (byte[] Data, string ContentType)> imageData,
        bool audioAllNonsense)
    {
        // Maps original image filename → OCR-extracted text (only set when OCR succeeds)
        var ocrNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var imageFile in imageFiles)
        {
            // Use OCR when:
            // 1. This image has a nonsense filename AND
            // 2. The audio files don't also have nonsense names (meaning audio names carry real info) AND
            // 3. We have actual image bytes to analyze
            if (!audioAllNonsense && IsNonsenseFilename(imageFile)
                && imageData != null && imageData.TryGetValue(imageFile, out var data))
            {
                _logger.LogInformation("Image '{FileName}' has a nonsense filename; attempting OCR.", imageFile);
                var ocrText = await ExtractTextFromImageAsync(data.Data, data.ContentType);
                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    _logger.LogInformation("OCR extracted text for '{FileName}': {Text}", imageFile, ocrText);
                    ocrNames[imageFile] = ocrText;
                }
                else
                {
                    _logger.LogWarning("OCR returned no text for '{FileName}'; using filename.", imageFile);
                }
            }
        }

        return ocrNames;
    }

    /// <summary>
    /// Extracts readable text from an image using OpenAI Vision.
    /// Focuses on song title or artist name for album art.
    /// </summary>
    private async Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType)
    {
        try
        {
            var client = new OpenAIClient(_apiKey);
            var responseClient = client.GetOpenAIResponseClient(VisionModel);

            var clientResult = await responseClient.CreateResponseAsync(
            [
                ResponseItem.CreateUserMessageItem(
                [
                    ResponseContentPart.CreateInputTextPart(
                        "Extract all readable text from this image. " +
                        "If it is album art or a music cover, focus on the song title or artist name. " +
                        "Preserve line breaks. Output ONLY the text, nothing else."
                    ),
                    ResponseContentPart.CreateInputImagePart(
                        BinaryData.FromBytes(imageBytes),
                        contentType
                    )
                ])
            ]);

            return clientResult.Value.GetOutputText()?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract text from image via vision OCR.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Calls OpenAI to intelligently pair audio and image filenames.
    /// <paramref name="imageDisplayNames"/> maps each original image filename to the name
    /// (or OCR-extracted text) that should be used for matching.
    /// </summary>
    private async Task<FileMatchingResult> MatchWithOpenAiAsync(
        List<string> audioFiles,
        List<string> imageFiles,
        Dictionary<string, string> ocrNames)
    {
        var client = new OpenAIClient(_apiKey);
        var chatClient = client.GetChatClient(ChatModel);

        var prompt = BuildPrompt(audioFiles, imageFiles, ocrNames);

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(
                "You are a file-matching assistant. Your job is to match music audio files with their " +
                "corresponding cover art image files based on filename similarity. " +
                "Underscores and hyphens in filenames should be treated as spaces. " +
                "You should recognize similar words, slight misspellings, abbreviations, and infer intent. " +
                "Each audio file can be matched to at most one image file, and each image file to at most one audio file. " +
                "You must respond ONLY with valid JSON and nothing else."),
            ChatMessage.CreateUserMessage(prompt)
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        var response = await chatClient.CompleteChatAsync(messages, options);
        var jsonText = response.Value.Content[0].Text;

        _logger.LogInformation("OpenAI file matching response: {Response}", jsonText);

        return ParseOpenAiResponse(jsonText, audioFiles, imageFiles);
    }

    /// <summary>
    /// Builds the prompt for OpenAI to match audio and image filenames.
    /// <paramref name="ocrNames"/> maps image filenames to OCR-extracted text
    /// (only populated when OCR succeeded for that file).
    /// </summary>
    private static string BuildPrompt(
        List<string> audioFiles,
        List<string> imageFiles,
        Dictionary<string, string> ocrNames)
    {
        var audioJson = JsonSerializer.Serialize(audioFiles);

        // For OCR-enriched images, show "original.jpg (content: OCR text)" so OpenAI can match on content.
        // For normal filenames, just include the filename as-is.
        var imageEntries = imageFiles.Select(f =>
            ocrNames.TryGetValue(f, out var ocrText) && !string.IsNullOrWhiteSpace(ocrText)
                ? $"{f} (content: {ocrText})"
                : f
        ).ToList();
        var imageJson = JsonSerializer.Serialize(imageEntries);

        return $@"Match each audio file with the most similar image file based on their filenames.

Audio files: {audioJson}
Image files: {imageJson}

Rules:
- Treat underscores and hyphens as spaces when comparing names.
- Ignore file extensions when matching (e.g., ""dark_night.mp3"" can match ""DarkNight.jpg"").
- Recognize similar words, abbreviations, and slight misspellings.
- Ignore a ""_mastered"" suffix in audio filenames (e.g., ""dark_night_mastered.mp3"" is the same song as ""dark_night.mp3"").
- For image files that have a ""(content: ...)"" annotation, use that content text instead of the filename for matching.
- Each audio file can match at most one image file.
- Each image file can match at most one audio file.
- For each match, provide a clean normalized song name with proper capitalization and single spaces between words.
- If an audio file has no good match in the image list, include it with null image_file.
- List any image files that could not be matched in unmatched_images.
- Always use the original filename (not the content annotation) in your JSON response.

Respond with JSON in this exact format:
{{
  ""pairs"": [
    {{
      ""audio_file"": ""original_audio_filename.mp3"",
      ""image_file"": ""original_image_filename.jpg"",
      ""normalized_name"": ""Song Name Here""
    }}
  ],
  ""unmatched_images"": [""unmatched_image.jpg""]
}}";
    }

    /// <summary>
    /// Parses the OpenAI JSON response into a <see cref="FileMatchingResult"/>.
    /// Falls back to exact matching if parsing fails.
    /// </summary>
    private FileMatchingResult ParseOpenAiResponse(string jsonText, List<string> audioFiles, List<string> imageFiles)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OpenAiMatchResponse>(jsonText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed == null)
            {
                _logger.LogWarning("OpenAI returned null response; falling back to exact match.");
                return FallbackExactMatch(audioFiles, imageFiles);
            }

            var result = new FileMatchingResult();
            var matchedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in parsed.Pairs ?? new List<OpenAiPair>())
            {
                if (string.IsNullOrWhiteSpace(pair.AudioFile))
                    continue;

                // Verify the audio file is in our original list
                var originalAudio = audioFiles.FirstOrDefault(a =>
                    string.Equals(a, pair.AudioFile, StringComparison.OrdinalIgnoreCase));
                if (originalAudio == null)
                    continue;

                string originalImage = null;
                if (!string.IsNullOrWhiteSpace(pair.ImageFile))
                {
                    // Verify the image file is in our original list and not already matched
                    originalImage = imageFiles.FirstOrDefault(i =>
                        string.Equals(i, pair.ImageFile, StringComparison.OrdinalIgnoreCase)
                        && !matchedImages.Contains(i));
                    if (originalImage != null)
                        matchedImages.Add(originalImage);
                }

                var normalizedName = string.IsNullOrWhiteSpace(pair.NormalizedName)
                    ? NormalizeBaseName(GetBaseNameWithoutExtension(originalAudio))
                    : pair.NormalizedName.Trim();

                result.Pairs.Add(new FilePair
                {
                    AudioFileName = originalAudio,
                    ImageFileName = originalImage,
                    NormalizedName = normalizedName
                });
            }

            // Add any audio files that OpenAI didn't include in the pairs
            var matchedAudio = result.Pairs.Select(p => p.AudioFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var audio in audioFiles.Where(a => !matchedAudio.Contains(a)))
            {
                result.Pairs.Add(new FilePair
                {
                    AudioFileName = audio,
                    ImageFileName = null,
                    NormalizedName = NormalizeBaseName(GetBaseNameWithoutExtension(audio))
                });
            }

            // Unmatched images
            result.UnmatchedImageFiles = imageFiles
                .Where(i => !matchedImages.Contains(i))
                .ToList();

            // Also add any unmatched images returned by OpenAI
            foreach (var unmatchedImage in parsed.UnmatchedImages ?? new List<string>())
            {
                var original = imageFiles.FirstOrDefault(i =>
                    string.Equals(i, unmatchedImage, StringComparison.OrdinalIgnoreCase)
                    && !matchedImages.Contains(i));
                if (original != null && !result.UnmatchedImageFiles.Contains(original, StringComparer.OrdinalIgnoreCase))
                    result.UnmatchedImageFiles.Add(original);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse OpenAI response; falling back to exact match. Response: {Response}", jsonText);
            return FallbackExactMatch(audioFiles, imageFiles);
        }
    }

    /// <summary>
    /// Exact base-name fallback: pairs files only if normalized base names match exactly (case-insensitive).
    /// </summary>
    private FileMatchingResult FallbackExactMatch(List<string> audioFiles, List<string> imageFiles)
    {
        var result = new FileMatchingResult();
        var imageByBaseName = imageFiles
            .GroupBy(f => NormalizeBaseName(GetBaseNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matchedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var audio in audioFiles)
        {
            var audioBase = NormalizeBaseName(GetBaseNameWithoutExtension(audio));
            imageByBaseName.TryGetValue(audioBase, out var matchedImage);
            if (matchedImage != null)
                matchedImages.Add(matchedImage);

            result.Pairs.Add(new FilePair
            {
                AudioFileName = audio,
                ImageFileName = matchedImage,
                NormalizedName = audioBase
            });
        }

        result.UnmatchedImageFiles = imageFiles.Where(i => !matchedImages.Contains(i)).ToList();
        return result;
    }

    /// <summary>
    /// Returns true if the filename base name looks like a GUID, hex hash, or purely numeric sequence
    /// that carries no real song-name information.
    /// </summary>
    internal static bool IsNonsenseFilename(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
            return false;

        // Check full GUID pattern (with or without dashes)
        if (GuidPattern.IsMatch(baseName))
            return true;

        // Strip separators and check for all-hex or all-numeric
        var stripped = baseName.Replace("-", "").Replace("_", "").Replace(" ", "");
        if (HexOnlyPattern.IsMatch(stripped))
            return true;
        if (AllNumericPattern.IsMatch(stripped))
            return true;

        return false;
    }

    /// <summary>
    /// Returns the filename without extension, also stripping the _mastered suffix.
    /// </summary>
    private static string GetBaseNameWithoutExtension(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (baseName.EndsWith(MasteredSuffix, StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^MasteredSuffix.Length];
        return baseName;
    }

    /// <summary>
    /// Converts a raw base name (underscores/hyphens as separators) to a clean name
    /// with single spaces and title casing.
    /// </summary>
    internal static string NormalizeBaseName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return string.Empty;

        // Replace underscores and hyphens with spaces
        var spaced = baseName.Replace('_', ' ').Replace('-', ' ');

        // Collapse multiple spaces
        while (spaced.Contains("  "))
            spaced = spaced.Replace("  ", " ");

        spaced = spaced.Trim();

        // Title-case each word (lowercase first to handle ALL-CAPS filenames)
        if (string.IsNullOrEmpty(spaced))
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

    // DTO models for deserializing OpenAI response
    private class OpenAiMatchResponse
    {
        [JsonPropertyName("pairs")]
        public List<OpenAiPair> Pairs { get; set; } = new();

        [JsonPropertyName("unmatched_images")]
        public List<string> UnmatchedImages { get; set; } = new();
    }

    private class OpenAiPair
    {
        [JsonPropertyName("audio_file")]
        public string AudioFile { get; set; }

        [JsonPropertyName("image_file")]
        public string ImageFile { get; set; }

        [JsonPropertyName("normalized_name")]
        public string NormalizedName { get; set; }
    }
}
