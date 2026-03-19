#nullable enable
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing creator personas.
/// </summary>
public class CreatorPersonaService : ICreatorPersonaService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<CreatorPersonaService> _logger;
    private readonly BlobContainerClient _personaContainerClient;

    public CreatorPersonaService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOptions<AzureStorageOptions> options,
        ILogger<CreatorPersonaService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;

        var opts = options.Value;
        if (!string.IsNullOrWhiteSpace(opts.StorageAccountConnectionString) &&
            !string.IsNullOrWhiteSpace(opts.PersonaImageContainerName))
        {
            _personaContainerClient = new BlobContainerClient(
                opts.StorageAccountConnectionString,
                opts.PersonaImageContainerName);
        }
        else
        {
            // Fallback – container won't be usable, but service still loads
            _personaContainerClient = null!;
            _logger.LogWarning("Persona image container is not configured. Persona image operations will fail.");
        }
    }

    /// <inheritdoc />
    public async Task<List<CreatorPersona>> GetPersonasByCreatorIdAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.CreatorPersonas
            .Where(p => p.CreatorId == creatorId)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<CreatorPersona?> GetPersonaByIdAsync(int personaId, int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.CreatorPersonas
            .FirstOrDefaultAsync(p => p.Id == personaId && p.CreatorId == creatorId);
    }

    /// <inheritdoc />
    public async Task<CreatorPersona> CreatePersonaAsync(int creatorId, string name, string? bio, string? websiteUrl)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var persona = new CreatorPersona
        {
            CreatorId = creatorId,
            Name = name,
            Bio = bio,
            WebsiteUrl = websiteUrl,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.CreatorPersonas.Add(persona);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created persona {PersonaId} for creator {CreatorId}", persona.Id, creatorId);
        return persona;
    }

    /// <inheritdoc />
    public async Task<CreatorPersona> UpdatePersonaAsync(int personaId, int creatorId, string name, string? bio,
        string? websiteUrl, string? imageBlobPath, int? imageWidth, int? imageHeight)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var persona = await context.CreatorPersonas
            .FirstOrDefaultAsync(p => p.Id == personaId && p.CreatorId == creatorId);
        if (persona == null)
        {
            throw new InvalidOperationException($"Persona {personaId} not found for creator {creatorId}");
        }

        persona.Name = name;
        persona.Bio = bio;
        persona.WebsiteUrl = websiteUrl;
        if (imageBlobPath != null)
        {
            persona.ImageBlobPath = imageBlobPath;
        }
        if (imageWidth.HasValue) persona.ImageWidth = imageWidth;
        if (imageHeight.HasValue) persona.ImageHeight = imageHeight;
        persona.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();
        _logger.LogInformation("Updated persona {PersonaId} for creator {CreatorId}", personaId, creatorId);
        return persona;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePersonaAsync(int personaId, int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var persona = await context.CreatorPersonas
            .FirstOrDefaultAsync(p => p.Id == personaId && p.CreatorId == creatorId);
        if (persona == null)
        {
            _logger.LogWarning("Persona {PersonaId} not found for creator {CreatorId}", personaId, creatorId);
            return false;
        }

        // Delete the image from blob storage
        if (!string.IsNullOrEmpty(persona.ImageBlobPath))
        {
            await DeletePersonaImageFromStorageAsync(persona.ImageBlobPath);
        }

        // Clear PersonaId from any songs that reference this persona
        var linkedSongs = await context.SongMetadata
            .Where(sm => sm.PersonaId == personaId)
            .ToListAsync();
        foreach (var song in linkedSongs)
        {
            song.PersonaId = null;
        }

        context.CreatorPersonas.Remove(persona);
        await context.SaveChangesAsync();

        _logger.LogInformation("Deleted persona {PersonaId} for creator {CreatorId}", personaId, creatorId);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllPersonasForCreatorAsync(int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var personas = await context.CreatorPersonas
            .Where(p => p.CreatorId == creatorId)
            .ToListAsync();

        if (personas.Count == 0)
            return 0;

        // Delete all persona images from blob storage
        foreach (var persona in personas)
        {
            if (!string.IsNullOrEmpty(persona.ImageBlobPath))
            {
                await DeletePersonaImageFromStorageAsync(persona.ImageBlobPath);
            }
        }

        // Clear PersonaId from linked songs
        var personaIds = personas.Select(p => p.Id).ToList();
        var linkedSongs = await context.SongMetadata
            .Where(sm => sm.PersonaId != null && personaIds.Contains(sm.PersonaId.Value))
            .ToListAsync();
        foreach (var song in linkedSongs)
        {
            song.PersonaId = null;
        }

        context.CreatorPersonas.RemoveRange(personas);
        await context.SaveChangesAsync();

        _logger.LogInformation("Deleted {Count} personas for creator {CreatorId}", personas.Count, creatorId);
        return personas.Count;
    }

    /// <inheritdoc />
    public string GetPersonaImageSasUrl(string blobPath, TimeSpan lifetime)
    {
        if (_personaContainerClient == null || string.IsNullOrWhiteSpace(blobPath))
            return string.Empty;

        try
        {
            var blobClient = _personaContainerClient.GetBlobClient(blobPath);
            var expiresOn = DateTimeOffset.UtcNow.Add(lifetime);
            var sasBuilder = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
            {
                BlobContainerName = _personaContainerClient.Name,
                BlobName = blobPath,
                Resource = "b"
            };

            var sasUri = blobClient.GenerateSasUri(sasBuilder);
            return sasUri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate SAS URL for persona image {BlobPath}", blobPath);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<string> UploadPersonaImageAsync(int personaId, int creatorId, Stream imageStream,
        string contentType, string fileExtension)
    {
        if (_personaContainerClient == null)
            throw new InvalidOperationException("Persona image container is not configured.");

        await EnsurePersonaContainerExistsAsync();

        var blobName = $"creator-{creatorId}/persona-{personaId}{fileExtension}";
        var blobClient = _personaContainerClient.GetBlobClient(blobName);

        if (imageStream.CanSeek) imageStream.Position = 0;
        var headers = new BlobHttpHeaders { ContentType = contentType };
        await blobClient.UploadAsync(imageStream, new BlobUploadOptions { HttpHeaders = headers });

        _logger.LogInformation("Uploaded persona image {BlobName}", blobName);
        return blobName;
    }

    /// <inheritdoc />
    public async Task<int> GetPersonaSongCountAsync(int personaId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.SongMetadata
            .CountAsync(sm => sm.PersonaId == personaId && sm.IsActive);
    }

    private async Task DeletePersonaImageFromStorageAsync(string blobPath)
    {
        if (_personaContainerClient == null || string.IsNullOrWhiteSpace(blobPath))
            return;

        try
        {
            var blobClient = _personaContainerClient.GetBlobClient(blobPath);
            await blobClient.DeleteIfExistsAsync();
            _logger.LogInformation("Deleted persona image {BlobPath}", blobPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete persona image {BlobPath}", blobPath);
        }
    }

    private async Task EnsurePersonaContainerExistsAsync()
    {
        if (_personaContainerClient == null) return;
        try
        {
            await _personaContainerClient.CreateIfNotExistsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure persona container exists");
        }
    }
}
