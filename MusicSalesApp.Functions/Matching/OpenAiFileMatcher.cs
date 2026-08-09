#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace MusicSalesApp.Functions.Matching;

/// <summary>
/// Pairs a batch of cover-art images with the audio files uploaded alongside them, using vision OCR
/// to read the text off each image and then one pairing call over the whole batch.
///
/// <para>
/// This used to run on the Blazor circuit, in chunks of four, because that bounded how many image
/// byte arrays sat in memory on a shared-hosting web server. Chunking also capped how well it could
/// match: the pairing prompt only ever saw four images, so it could not notice that image 7 suited
/// audio 2 better than image 3 did, while its own one-image-per-song rules only make sense across
/// the whole batch. Here the images stream from staging one at a time and the pairing call sees
/// everything at once.
/// </para>
/// </summary>
public interface IOpenAiFileMatcher
{
    /// <summary>True when an API key is configured. False means the caller must fall back.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Reads the text off each image and returns a pairing.
    /// </summary>
    /// <param name="readText">
    /// Loads one image's bytes. Called at most <see cref="MaxOcrImages"/> times, concurrently.
    /// </param>
    /// <param name="onImageRead">Invoked as each image's text comes back, for progress.</param>
    Task<CoverArtMatchResult> MatchAsync(
        CoverArtMatchRequest request,
        Func<CoverArtMatchCandidate, CancellationToken, Task<byte[]?>> readImage,
        Action<int> onImageRead,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exact base-name pairing, used when the models are unavailable or fail. Deterministic, free,
    /// and the behaviour the site had before any of this was wired in.
    /// </summary>
    static CoverArtMatchResult FallbackMatch(CoverArtMatchRequest request)
    {
        var pairs = new List<CoverArtMatchPair>();
        var matchedImages = new HashSet<int>();

        var imagesByBaseName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in request.Images ?? [])
        {
            var key = FileNameMatching.ToNormalizedName(image.FileName);
            if (!string.IsNullOrEmpty(key))
                imagesByBaseName.TryAdd(key, image.Index);
        }

        var audioNames = request.AudioFileNames ?? [];
        for (var audioIndex = 0; audioIndex < audioNames.Count; audioIndex++)
        {
            var normalized = FileNameMatching.ToNormalizedName(audioNames[audioIndex]);

            int? imageIndex = null;
            if (imagesByBaseName.TryGetValue(normalized, out var candidate)
                && matchedImages.Add(candidate))
            {
                imageIndex = candidate;
            }

            pairs.Add(new CoverArtMatchPair
            {
                AudioIndex = audioIndex,
                ImageIndex = imageIndex,
                NormalizedName = normalized
            });
        }

        return new CoverArtMatchResult
        {
            BatchId = request.BatchId,
            CreatorId = request.CreatorId,
            Pairs = pairs,
            UnmatchedImageIndexes = (request.Images ?? [])
                .Where(image => !matchedImages.Contains(image.Index))
                .Select(image => image.Index)
                .ToList(),
            UsedFallback = true
        };
    }

    /// <summary>
    /// How many images get a vision call. Chunking used to bound this phase's duration as a side
    /// effect of bounding its memory; with chunking gone the bound has to be explicit, or a 50-image
    /// batch could outrun the page's wait and, at the extreme, the invocation ceiling. The remainder
    /// still match on filename, which is how most batches pair anyway.
    /// </summary>
    const int MaxOcrImages = 20;

    /// <summary>
    /// Vision calls are pure network I/O, so several run at once. Twenty images become about five
    /// rounds instead of twenty.
    /// </summary>
    const int MaxConcurrentOcrCalls = 4;
}

/// <inheritdoc />
public sealed class OpenAiFileMatcher : IOpenAiFileMatcher
{
    private const string ChatModel = "gpt-4.1-mini";
    private const string VisionModel = "gpt-5-mini";

    /// <summary>The value the sample settings ship with, so an un-provisioned environment degrades.</summary>
    private const string PlaceholderApiKey = "__REPLACE_WITH_OPENAI_API_KEY__";

    private readonly string _apiKey;
    private readonly ILogger<OpenAiFileMatcher> _logger;

    public OpenAiFileMatcher(IConfiguration configuration, ILogger<OpenAiFileMatcher> logger)
    {
        _apiKey = configuration["OpenAI:ApiKey"] ?? string.Empty;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey) && _apiKey != PlaceholderApiKey;

    /// <inheritdoc />
    public async Task<CoverArtMatchResult> MatchAsync(
        CoverArtMatchRequest request,
        Func<CoverArtMatchCandidate, CancellationToken, Task<byte[]?>> readImage,
        Action<int> onImageRead,
        CancellationToken cancellationToken = default)
    {
        var images = request.Images ?? [];
        var extractedText = await ReadImageTextAsync(images, readImage, onImageRead, cancellationToken);

        var client = new OpenAIClient(_apiKey);
        var chatClient = client.GetChatClient(ChatModel);

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(
                "You are a file-matching assistant. Your job is to match music audio files with their "
                + "corresponding cover art image files based on filename similarity. "
                + "Underscores and hyphens in filenames should be treated as spaces. "
                + "You should recognize similar words, slight misspellings, abbreviations, and infer intent. "
                + "Each audio file can be matched to at most one image file, and each image file to at most one audio file. "
                + "You must respond ONLY with valid JSON and nothing else."),
            ChatMessage.CreateUserMessage(BuildPrompt(request, extractedText))
        };

        var response = await chatClient.CompleteChatAsync(
            messages,
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            cancellationToken);

        var json = response.Value.Content[0].Text;
        _logger.LogInformation("Cover-art pairing response for batch {BatchId}: {Response}", request.BatchId, json);

        return ParseResponse(json, request);
    }

    /// <summary>
    /// One vision call per image, bounded and run a few at a time. A failure on any single image is
    /// not fatal — that image simply matches on its filename, which is the common case anyway.
    /// </summary>
    private async Task<Dictionary<int, string>> ReadImageTextAsync(
        IReadOnlyList<CoverArtMatchCandidate> images,
        Func<CoverArtMatchCandidate, CancellationToken, Task<byte[]?>> readImage,
        Action<int> onImageRead,
        CancellationToken cancellationToken)
    {
        var extracted = new Dictionary<int, string>();
        var considered = images.Take(IOpenAiFileMatcher.MaxOcrImages).ToList();

        if (considered.Count < images.Count)
        {
            // Never silently. A batch where some images were matched on filename alone can pair
            // worse than one where all were read, and that must be visible in the log.
            _logger.LogWarning(
                "Batch {BatchId} has {Total} images; only the first {Limit} will have their text read. "
                + "The remainder match on filename alone.",
                images.Count,
                images.Count,
                IOpenAiFileMatcher.MaxOcrImages);
        }

        using var gate = new SemaphoreSlim(IOpenAiFileMatcher.MaxConcurrentOcrCalls);
        var completed = 0;

        var reads = considered.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var bytes = await readImage(candidate, cancellationToken);
                if (bytes is null || bytes.Length == 0)
                    return;

                var text = await ExtractTextAsync(bytes, candidate.ContentType, cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                    return;

                lock (extracted)
                {
                    extracted[candidate.Index] = text;
                }
            }
            finally
            {
                gate.Release();
                onImageRead(Interlocked.Increment(ref completed));
            }
        });

        await Task.WhenAll(reads);
        return extracted;
    }

    private async Task<string> ExtractTextAsync(
        byte[] imageBytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = new OpenAIClient(_apiKey);
            var responseClient = client.GetResponsesClient();

            var result = await responseClient.CreateResponseAsync(
                new CreateResponseOptions
                {
                    Model = VisionModel,
                    InputItems =
                    {
                        ResponseItem.CreateUserMessageItem(
                        [
                            ResponseContentPart.CreateInputTextPart(
                                "Extract all readable text from this image. "
                                + "If it is album art or a music cover, focus on the song title or artist name. "
                                + "Preserve line breaks. Output ONLY the text, nothing else."),
                            ResponseContentPart.CreateInputImagePart(
                                BinaryData.FromBytes(imageBytes),
                                contentType ?? "image/jpeg")
                        ])
                    }
                },
                cancellationToken);

            return result.Value.GetOutputText()?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreadable image must not cost the whole batch its pairing.
            _logger.LogWarning(ex, "Vision OCR failed for one image; it will match on filename alone.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Indices rather than filenames throughout, so the model never has to reproduce a name verbatim
    /// — apostrophes, ampersands and non-ASCII in creator filenames all used to come back subtly
    /// altered and fail to match anything.
    /// </summary>
    private static string BuildPrompt(CoverArtMatchRequest request, Dictionary<int, string> extractedText)
    {
        var audioLines = string.Join(
            "\n",
            (request.AudioFileNames ?? []).Select((name, index) => $"  {index}: \"{name}\""));

        var imageLines = string.Join(
            "\n",
            (request.Images ?? []).Select(image =>
            {
                var annotation = extractedText.TryGetValue(image.Index, out var text)
                    && !string.IsNullOrWhiteSpace(text)
                        ? $" (content: {text})"
                        : string.Empty;
                return $"  {image.Index}: \"{image.FileName}\"{annotation}";
            }));

        // The opening sentence is load-bearing. This used to say "match each audio file with the most
        // similar image file", which reads as an assignment problem: given three songs and three
        // images with two obvious pairs, the model dutifully handed the leftover image to the
        // leftover song. It produced exactly that in testing - a headshot named "david.JPG" paired
        // with "All Around Me" on no evidence whatsoever. Leaving things unmatched has to be stated
        // as the correct answer, not offered as a fallback near the end of a list of rules.
        return $@"Decide which of these image files, if any, are the cover art for these audio files.

This is NOT an assignment problem. There may be more images than songs, more songs than images, or
images that belong to neither. Matching nothing is a perfectly good answer.

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

Only match on positive evidence. A pair is a match when the image's name or its extracted content
names the song - not when the two merely happen to be the last ones left.

- NEVER match by elimination. That one audio file and one image file are both still unpaired is not
  evidence that they belong together. If they were the only two files in the batch and their names
  had nothing to do with each other, you would not pair them; being last changes nothing.
- If you would struggle to explain to the creator why an image belongs to a song, it does not.
- An audio file with no good match must be included with null image_index.
- Every image file you did not match must be listed in unmatched_image_indices.

A wrong match is worse than no match. The creator is shown the unmatched images and can pair them by
hand in seconds, but a confident wrong pairing looks correct and gets published.

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
    /// Every index is bounds-checked and every claim is enforced one-to-one, so a malformed or
    /// hostile response can only ever produce a worse pairing, never an out-of-range one.
    /// </summary>
    private CoverArtMatchResult ParseResponse(string json, CoverArtMatchRequest request)
    {
        var audioNames = request.AudioFileNames ?? [];
        var validImageIndexes = (request.Images ?? []).Select(image => image.Index).ToHashSet();

        var parsed = JsonSerializer.Deserialize<MatchResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (parsed is null)
        {
            _logger.LogWarning("Cover-art pairing returned nothing usable; falling back to exact matching.");
            return IOpenAiFileMatcher.FallbackMatch(request);
        }

        var pairs = new List<CoverArtMatchPair>();
        var claimedAudio = new HashSet<int>();
        var claimedImages = new HashSet<int>();

        foreach (var pair in parsed.Pairs ?? [])
        {
            if (pair.AudioIndex < 0 || pair.AudioIndex >= audioNames.Count)
                continue;
            if (!claimedAudio.Add(pair.AudioIndex))
                continue;

            int? imageIndex = null;
            if (pair.ImageIndex is { } candidate
                && validImageIndexes.Contains(candidate)
                && claimedImages.Add(candidate))
            {
                imageIndex = candidate;
            }

            pairs.Add(new CoverArtMatchPair
            {
                AudioIndex = pair.AudioIndex,
                ImageIndex = imageIndex,
                NormalizedName = string.IsNullOrWhiteSpace(pair.NormalizedName)
                    ? FileNameMatching.ToNormalizedName(audioNames[pair.AudioIndex])
                    : pair.NormalizedName.Trim()
            });
        }

        // Anything the model left out still needs a row, or the page would silently drop a song the
        // creator selected.
        for (var audioIndex = 0; audioIndex < audioNames.Count; audioIndex++)
        {
            if (claimedAudio.Contains(audioIndex))
                continue;

            pairs.Add(new CoverArtMatchPair
            {
                AudioIndex = audioIndex,
                ImageIndex = null,
                NormalizedName = FileNameMatching.ToNormalizedName(audioNames[audioIndex])
            });
        }

        return new CoverArtMatchResult
        {
            BatchId = request.BatchId,
            CreatorId = request.CreatorId,
            Pairs = pairs.OrderBy(pair => pair.AudioIndex).ToList(),
            UnmatchedImageIndexes = validImageIndexes
                .Where(index => !claimedImages.Contains(index))
                .OrderBy(index => index)
                .ToList(),
            UsedFallback = false
        };
    }

    private sealed class MatchResponse
    {
        [JsonPropertyName("pairs")]
        public List<ResponsePair> Pairs { get; set; } = [];
    }

    private sealed class ResponsePair
    {
        [JsonPropertyName("audio_index")]
        public int AudioIndex { get; set; }

        /// <summary>Null means no image match for this audio file.</summary>
        [JsonPropertyName("image_index")]
        public int? ImageIndex { get; set; }

        [JsonPropertyName("normalized_name")]
        public string NormalizedName { get; set; } = string.Empty;
    }
}
