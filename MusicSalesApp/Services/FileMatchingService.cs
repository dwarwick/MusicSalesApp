using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace MusicSalesApp.Services;

/// <summary>
/// Uses OpenAI Chat Completions to intelligently match audio files with cover art image files
/// based on filename similarity. Falls back to exact base-name matching if OpenAI is unavailable.
/// </summary>
public class FileMatchingService : IFileMatchingService
{
    private readonly ILogger<FileMatchingService> _logger;
    private readonly string _apiKey;
    private const string ChatModel = "gpt-4o-mini";

    private static readonly string[] ValidAudioExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
    private static readonly string[] ValidImageExtensions = { ".jpeg", ".jpg", ".png" };
    private const string MasteredSuffix = "_mastered";

    public FileMatchingService(IConfiguration configuration, ILogger<FileMatchingService> logger)
    {
        _logger = logger;
        _apiKey = configuration["OpenAI:ApiKey"] ?? string.Empty;
    }

    private bool IsConfigured => !string.IsNullOrEmpty(_apiKey) && _apiKey != "__REPLACE_WITH_OPENAI_API_KEY__";

    /// <inheritdoc/>
    public async Task<FileMatchingResult> MatchFilesAsync(
        IEnumerable<string> audioFileNames,
        IEnumerable<string> imageFileNames)
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
                return await MatchWithOpenAiAsync(audioList, imageList);
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
    /// Calls OpenAI to intelligently pair audio and image filenames.
    /// </summary>
    private async Task<FileMatchingResult> MatchWithOpenAiAsync(List<string> audioFiles, List<string> imageFiles)
    {
        var client = new OpenAIClient(_apiKey);
        var chatClient = client.GetChatClient(ChatModel);

        var prompt = BuildPrompt(audioFiles, imageFiles);

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
    /// </summary>
    private static string BuildPrompt(List<string> audioFiles, List<string> imageFiles)
    {
        var audioJson = JsonSerializer.Serialize(audioFiles);
        var imageJson = JsonSerializer.Serialize(imageFiles);

        return $@"Match each audio file with the most similar image file based on their filenames.

Audio files: {audioJson}
Image files: {imageJson}

Rules:
- Treat underscores and hyphens as spaces when comparing names.
- Ignore file extensions when matching (e.g., ""dark_night.mp3"" can match ""DarkNight.jpg"").
- Recognize similar words, abbreviations, and slight misspellings.
- Ignore a ""_mastered"" suffix in audio filenames (e.g., ""dark_night_mastered.mp3"" is the same song as ""dark_night.mp3"").
- Each audio file can match at most one image file.
- Each image file can match at most one audio file.
- For each match, provide a clean normalized song name with proper capitalization and single spaces between words.
- If an audio file has no good match in the image list, include it with null image_file.
- List any image files that could not be matched in unmatched_images.

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
