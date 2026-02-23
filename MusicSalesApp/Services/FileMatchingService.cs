using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace MusicSalesApp.Services;

/// <summary>
/// Uses OpenAI Chat Completions to intelligently match audio files with cover art image files
/// based on filename similarity. Falls back to exact base-name matching if OpenAI is unavailable.
/// When image data is provided, uses OpenAI Vision OCR to extract text from the image and uses
/// that for matching instead of (or in addition to) the filename.
/// </summary>
public class FileMatchingService : IFileMatchingService
{
    private readonly ILogger<FileMatchingService> _logger;
    private readonly string _apiKey;
    private const string ChatModel = "gpt-4.1-mini";
    private const string VisionModel = "gpt-5-mini";

    private const string MasteredSuffix = "_mastered";

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
                var imageDisplayNames = await ResolveImageDisplayNamesAsync(imageList, imageData);
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
    /// Builds a display-name map for each image file using OCR.
    /// For every image that has bytes available in <paramref name="imageData"/>, attempts to extract
    /// readable text via OpenAI Vision and uses that text in the matching prompt instead of the filename.
    /// Images without available bytes are matched on their filename alone.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveImageDisplayNamesAsync(
        List<string> imageFiles,
        IReadOnlyDictionary<string, (byte[] Data, string ContentType)> imageData)
    {
        // Maps original image filename → OCR-extracted text (only set when OCR succeeds)
        var ocrNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (imageData == null)
            return ocrNames;

        foreach (var imageFile in imageFiles)
        {
            if (!imageData.TryGetValue(imageFile, out var data))
                continue;

            _logger.LogInformation("Attempting OCR on image '{FileName}'.", imageFile);
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
    /// Builds the prompt for OpenAI to match audio and image files.
    /// Uses numeric indices in the response format so OpenAI never has to reproduce filenames
    /// verbatim — this avoids encoding/escaping issues with special characters like apostrophes and ampersands.
    /// <paramref name="ocrNames"/> maps image filenames to OCR-extracted text
    /// (only populated when OCR succeeded for that file).
    /// </summary>
    private static string BuildPrompt(
        List<string> audioFiles,
        List<string> imageFiles,
        Dictionary<string, string> ocrNames)
    {
        // Numbered list of audio files
        var audioLines = string.Join("\n", audioFiles.Select((f, i) => $"  {i}: \"{f}\""));

        // Numbered list of image files; append OCR content annotation when available
        var imageLines = string.Join("\n", imageFiles.Select((f, i) =>
        {
            var annotation = ocrNames.TryGetValue(f, out var ocrText) && !string.IsNullOrWhiteSpace(ocrText)
                ? $" (content: {ocrText})"
                : string.Empty;
            return $"  {i}: \"{f}\"{annotation}";
        }));

        return $@"Match each audio file with the most similar image file based on their names and content.

Audio files (refer to these by their numeric index):
{audioLines}

Image files (refer to these by their numeric index):
{imageLines}

Rules:
- Treat underscores and hyphens as spaces when comparing names.
- Ignore file extensions when matching (e.g., ""dark_night.mp3"" can match ""DarkNight.jpg"").
- Recognize similar words, abbreviations, and slight misspellings.
- Ignore a ""_mastered"" suffix in audio filenames (e.g., ""dark_night_mastered.mp3"" is the same song as ""dark_night.mp3"").
- For image files with a ""(content: ...)"" annotation, use that content text instead of the filename for matching.
- Each audio file can match at most one image file.
- Each image file can match at most one audio file.
- For each match, provide a clean normalized song name with proper capitalization and single spaces between words.
- If an audio file has no good match, include it with null image_index.
- List the indices of any image files that could not be matched in unmatched_image_indices.

Respond with JSON using the numeric indices (NOT the filenames):
{{
  ""pairs"": [
    {{
      ""audio_index"": 0,
      ""image_index"": 1,
      ""normalized_name"": ""Song Name Here""
    }}
  ],
  ""unmatched_image_indices"": [2]
}}";
    }

    /// <summary>
    /// Parses the OpenAI JSON response (index-based) into a <see cref="FileMatchingResult"/>.
    /// OpenAI returns numeric indices into the audio/image arrays instead of filenames,
    /// which avoids any encoding/escaping ambiguity for special characters.
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
            var matchedAudioIndices = new HashSet<int>();
            var matchedImageIndices = new HashSet<int>();

            foreach (var pair in parsed.Pairs ?? new List<OpenAiPair>())
            {
                // Skip pairs where audio_index is out of range or already used
                if (pair.AudioIndex < 0 || pair.AudioIndex >= audioFiles.Count)
                    continue;
                if (matchedAudioIndices.Contains(pair.AudioIndex))
                    continue;

                matchedAudioIndices.Add(pair.AudioIndex);

                string imageFileName = null;
                if (pair.ImageIndex.HasValue)
                {
                    var imgIdx = pair.ImageIndex.Value;
                    if (imgIdx >= 0 && imgIdx < imageFiles.Count && !matchedImageIndices.Contains(imgIdx))
                    {
                        imageFileName = imageFiles[imgIdx];
                        matchedImageIndices.Add(imgIdx);
                    }
                }

                var normalizedName = string.IsNullOrWhiteSpace(pair.NormalizedName)
                    ? NormalizeBaseName(GetBaseNameWithoutExtension(audioFiles[pair.AudioIndex]))
                    : pair.NormalizedName.Trim();

                result.Pairs.Add(new FilePair
                {
                    AudioFileName = audioFiles[pair.AudioIndex],
                    ImageFileName = imageFileName,
                    NormalizedName = normalizedName
                });
            }

            // Add any audio files that OpenAI didn't include in the pairs
            for (int i = 0; i < audioFiles.Count; i++)
            {
                if (!matchedAudioIndices.Contains(i))
                {
                    result.Pairs.Add(new FilePair
                    {
                        AudioFileName = audioFiles[i],
                        ImageFileName = null,
                        NormalizedName = NormalizeBaseName(GetBaseNameWithoutExtension(audioFiles[i]))
                    });
                }
            }

            // Unmatched images: any image not assigned to a pair
            for (int i = 0; i < imageFiles.Count; i++)
            {
                if (!matchedImageIndices.Contains(i))
                    result.UnmatchedImageFiles.Add(imageFiles[i]);
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

    // DTO models for deserializing OpenAI index-based response
    private class OpenAiMatchResponse
    {
        [JsonPropertyName("pairs")]
        public List<OpenAiPair> Pairs { get; set; } = new();

        [JsonPropertyName("unmatched_image_indices")]
        public List<int> UnmatchedImageIndices { get; set; } = new();
    }

    private class OpenAiPair
    {
        [JsonPropertyName("audio_index")]
        public int AudioIndex { get; set; }

        /// <summary>Nullable - null means no image match for this audio file.</summary>
        [JsonPropertyName("image_index")]
        public int? ImageIndex { get; set; }

        [JsonPropertyName("normalized_name")]
        public string NormalizedName { get; set; }
    }
}
