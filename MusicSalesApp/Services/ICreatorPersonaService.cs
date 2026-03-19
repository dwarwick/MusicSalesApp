#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for managing creator personas.
/// </summary>
public interface ICreatorPersonaService
{
    /// <summary>
    /// Gets all personas belonging to a creator.
    /// </summary>
    Task<List<CreatorPersona>> GetPersonasByCreatorIdAsync(int creatorId);

    /// <summary>
    /// Gets a persona by its ID, verifying it belongs to the specified creator.
    /// </summary>
    Task<CreatorPersona?> GetPersonaByIdAsync(int personaId, int creatorId);

    /// <summary>
    /// Creates a new persona for a creator.
    /// </summary>
    Task<CreatorPersona> CreatePersonaAsync(int creatorId, string name, string? bio, string? websiteUrl);

    /// <summary>
    /// Updates an existing persona. Only the creator who owns it can update it.
    /// </summary>
    Task<CreatorPersona> UpdatePersonaAsync(int personaId, int creatorId, string name, string? bio, string? websiteUrl,
        string? imageBlobPath, int? imageWidth, int? imageHeight);

    /// <summary>
    /// Deletes a persona and its associated image from blob storage.
    /// Only the creator who owns it can delete it.
    /// </summary>
    Task<bool> DeletePersonaAsync(int personaId, int creatorId);

    /// <summary>
    /// Deletes all personas belonging to a creator, including their images from blob storage.
    /// Used when a creator stops being a creator or closes their account.
    /// </summary>
    Task<int> DeleteAllPersonasForCreatorAsync(int creatorId);

    /// <summary>
    /// Generates a SAS URL for a persona image blob.
    /// </summary>
    string GetPersonaImageSasUrl(string blobPath, TimeSpan lifetime);

    /// <summary>
    /// Uploads a persona profile image and returns the blob path.
    /// </summary>
    Task<string> UploadPersonaImageAsync(int personaId, int creatorId, Stream imageStream, string contentType, string fileExtension);

    /// <summary>
    /// Gets the number of songs linked to a persona.
    /// </summary>
    Task<int> GetPersonaSongCountAsync(int personaId);
}
