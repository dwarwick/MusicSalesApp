namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating embeddings using OpenAI
/// </summary>
public interface IOpenAIEmbeddingService
{
    /// <summary>
    /// Generate an embedding vector for the given text
    /// </summary>
    /// <param name="text">The text to generate an embedding for</param>
    /// <returns>A float array representing the embedding vector, or null if generation failed</returns>
    Task<float[]> GenerateEmbeddingAsync(string text);

    /// <summary>
    /// Generate embeddings for multiple texts in batch
    /// </summary>
    /// <param name="texts">The texts to generate embeddings for</param>
    /// <returns>A list of float arrays representing the embedding vectors</returns>
    Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts);

    /// <summary>
    /// Check if the OpenAI service is configured and available
    /// </summary>
    bool IsConfigured { get; }
}
