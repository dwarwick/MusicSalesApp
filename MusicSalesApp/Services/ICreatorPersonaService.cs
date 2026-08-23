#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service interface for managing creator personas.
/// </summary>
public interface ICreatorPersonaService
{
    /// <summary>
    /// Gets all personas belonging to a creator (including disabled ones, for the creator's own management view).
    /// </summary>
    Task<List<CreatorPersona>> GetPersonasByCreatorIdAsync(int creatorId);

    /// <summary>
    /// Gets all personas across all creators, including creator info, for the admin management view.
    /// </summary>
    Task<List<CreatorPersona>> GetAllPersonasAdminAsync();

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
    /// <param name="sendNotification">When true (default), sends email notifications and records user history. Set to false for internal updates like dimension checks.</param>
    /// <param name="imageReplaced">
    /// True when the caller has just written new image bytes, which triggers a rebuild of the
    /// pre-resized renditions. It cannot be inferred from <paramref name="imageBlobPath"/>: a
    /// replacement keeping the same file extension lands on the identical deterministic path.
    /// </param>
    Task<CreatorPersona> UpdatePersonaAsync(int personaId, int creatorId, string name, string? bio, string? websiteUrl,
        string? imageBlobPath, int? imageWidth, int? imageHeight, bool sendNotification = true,
        bool imageReplaced = false);

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
    /// Disables a persona (admin action). Records user history and sends email to the creator.
    /// </summary>
    Task<bool> DisablePersonaAsync(int personaId, int adminUserId, string reason, string baseUrl);

    /// <summary>
    /// Re-enables a previously disabled persona (admin action). Records user history and sends email to the creator.
    /// </summary>
    Task<bool> EnablePersonaAsync(int personaId, int adminUserId, string reason, string baseUrl);

    /// <summary>
    /// Generates a SAS URL for a persona image blob.
    /// </summary>
    string GetPersonaImageSasUrl(string blobPath, TimeSpan lifetime);

    /// <summary>
    /// Generates a SAS URL for the pre-resized rendition best suited to a known display size,
    /// falling back to the full-size blob when no rendition is large enough or none exist yet.
    /// </summary>
    /// <param name="variantWidthsCsv">The persona's recorded rendition widths.</param>
    /// <param name="displayWidthCssPx">
    /// How wide the avatar renders in CSS pixels. The chosen rendition is at least twice this, so it
    /// stays sharp on a high-density display.
    /// </param>
    string GetPersonaImageSasUrl(string blobPath, string? variantWidthsCsv, int displayWidthCssPx, TimeSpan lifetime);

    /// <summary>
    /// Whether <paramref name="blobPath"/> may be served to anyone over the public persona-art
    /// endpoint - that is, it is the image (or a rendition of the image) of an enabled persona.
    ///
    /// <para>
    /// The gate is the persona's IsEnabled flag, never the shape of the path. This is the persona
    /// equivalent of the media whitelist <c>MusicController</c> applies to song blobs, and exists
    /// for the same reason: the endpoint takes a caller-supplied path.
    /// </para>
    /// </summary>
    Task<bool> IsPubliclyReadableImagePathAsync(string blobPath);

    /// <summary>
    /// Opens a persona image blob for reading, or null if it does not exist. Persona images live in
    /// their own container, so <see cref="IAzureStorageService"/> - which is bound to the media
    /// container - cannot reach them.
    /// </summary>
    Task<Stream?> OpenPersonaImageReadAsync(string blobPath);

    /// <summary>
    /// Uploads a persona profile image and returns the blob path.
    /// </summary>
    Task<string> UploadPersonaImageAsync(int personaId, int creatorId, Stream imageStream, string contentType, string fileExtension);

    /// <summary>
    /// Gets the number of songs linked to a persona.
    /// </summary>
    Task<int> GetPersonaSongCountAsync(int personaId);

    /// <summary>
    /// Gets song counts for multiple personas in a single query (avoids N+1).
    /// Returns a dictionary of personaId → count. Personas with no songs are omitted (count = 0).
    /// </summary>
    Task<Dictionary<int, int>> GetPersonaSongCountsAsync(IEnumerable<int> personaIds);

    /// <summary>
    /// Deletes a persona image blob directly from storage.
    /// Used to clean up orphaned blobs (e.g. a cropped image that was uploaded but
    /// the persona save was subsequently cancelled).
    /// </summary>
    Task DeletePersonaImageBlobAsync(string blobPath);
}
