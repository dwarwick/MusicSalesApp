using OpenAI;
using OpenAI.Embeddings;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating embeddings using OpenAI's text-embedding-3-small model
/// </summary>
public class OpenAIEmbeddingService : IOpenAIEmbeddingService
{
    private readonly ILogger<OpenAIEmbeddingService> _logger;
    private readonly string _apiKey;
    private const string EmbeddingModel = "text-embedding-3-small";
    private const int EmbeddingDimensions = 384; // Using smaller dimensions for efficiency

    public OpenAIEmbeddingService(IConfiguration configuration, ILogger<OpenAIEmbeddingService> logger)
    {
        _logger = logger;
        _apiKey = configuration["OpenAI:ApiKey"] ?? string.Empty;
    }

    /// <inheritdoc/>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey) && _apiKey != "__REPLACE_WITH_OPENAI_API_KEY__";

    /// <inheritdoc/>
    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("OpenAI API key is not configured. Cannot generate embedding.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Cannot generate embedding for empty text.");
            return null;
        }

        try
        {
            var client = new OpenAIClient(_apiKey);
            var embeddingClient = client.GetEmbeddingClient(EmbeddingModel);

            var options = new EmbeddingGenerationOptions
            {
                Dimensions = EmbeddingDimensions
            };

            var response = await embeddingClient.GenerateEmbeddingAsync(text, options);
            var embedding = response.Value;

            return embedding.ToFloats().ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for text: {TextPreview}", 
                text.Length > 50 ? text.Substring(0, 50) + "..." : text);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("OpenAI API key is not configured. Cannot generate embeddings.");
            return new List<float[]>();
        }

        var textList = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (!textList.Any())
        {
            _logger.LogWarning("No valid texts provided for embedding generation.");
            return new List<float[]>();
        }

        try
        {
            var client = new OpenAIClient(_apiKey);
            var embeddingClient = client.GetEmbeddingClient(EmbeddingModel);

            var options = new EmbeddingGenerationOptions
            {
                Dimensions = EmbeddingDimensions
            };

            var response = await embeddingClient.GenerateEmbeddingsAsync(textList, options);
            
            return response.Value.Select(e => e.ToFloats().ToArray()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embeddings for {Count} texts", textList.Count);
            return new List<float[]>();
        }
    }
}
