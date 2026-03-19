#nullable enable
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Helpers;
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
    private readonly BlobContainerClient? _personaContainerClient;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly IEmailService _emailService;
    private readonly string _customerServiceEmail;

    public CreatorPersonaService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOptions<AzureStorageOptions> options,
        ILogger<CreatorPersonaService> logger,
        IAdminNotificationService adminNotificationService,
        IEmailService emailService,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _adminNotificationService = adminNotificationService;
        _emailService = emailService;
        _customerServiceEmail = configuration["EmailSettings:CustomerServiceEmail"] ?? string.Empty;

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
            _personaContainerClient = null;
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
    public async Task<List<CreatorPersona>> GetAllPersonasAdminAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.CreatorPersonas
            .Include(p => p.Creator)
                .ThenInclude(c => c.User)
            .OrderBy(p => p.Creator.User!.Email)
            .ThenBy(p => p.Name)
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
            IsEnabled = true,
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
    public async Task<bool> DisablePersonaAsync(int personaId, int adminUserId, string reason, string baseUrl)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            var persona = await context.CreatorPersonas
                .Include(p => p.Creator)
                    .ThenInclude(c => c.User)
                .FirstOrDefaultAsync(p => p.Id == personaId);

            if (persona == null)
            {
                _logger.LogWarning("Persona {PersonaId} not found for disable", personaId);
                return false;
            }

            persona.IsEnabled = false;
            persona.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation("Persona {PersonaId} disabled by admin {AdminUserId}. Reason: {Reason}",
                personaId, adminUserId, reason);

            // Record user history for the creator
            var creatorUserId = persona.Creator?.UserId;
            var creatorEmail = persona.Creator?.User?.Email;
            if (creatorUserId.HasValue && !string.IsNullOrEmpty(creatorEmail))
            {
                try
                {
                    await _adminNotificationService.RecordUserHistoryAsync(
                        creatorUserId.Value,
                        creatorEmail,
                        UserHistoryEventTypes.PersonaDisabled,
                        $"Persona '{persona.Name}' disabled by admin. Reason: {reason}",
                        oldValue: "Enabled",
                        newValue: "Disabled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to record user history for persona disable {PersonaId}", personaId);
                }
            }

            // Send notification email
            if (!string.IsNullOrEmpty(creatorEmail))
            {
                await SendPersonaStatusEmailAsync(persona, isEnabled: false, reason, baseUrl, creatorEmail);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disabling persona {PersonaId}", personaId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnablePersonaAsync(int personaId, int adminUserId, string reason, string baseUrl)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            var persona = await context.CreatorPersonas
                .Include(p => p.Creator)
                    .ThenInclude(c => c.User)
                .FirstOrDefaultAsync(p => p.Id == personaId);

            if (persona == null)
            {
                _logger.LogWarning("Persona {PersonaId} not found for enable", personaId);
                return false;
            }

            persona.IsEnabled = true;
            persona.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation("Persona {PersonaId} enabled by admin {AdminUserId}. Reason: {Reason}",
                personaId, adminUserId, reason);

            // Record user history for the creator
            var creatorUserId = persona.Creator?.UserId;
            var creatorEmail = persona.Creator?.User?.Email;
            if (creatorUserId.HasValue && !string.IsNullOrEmpty(creatorEmail))
            {
                try
                {
                    await _adminNotificationService.RecordUserHistoryAsync(
                        creatorUserId.Value,
                        creatorEmail,
                        UserHistoryEventTypes.PersonaEnabled,
                        $"Persona '{persona.Name}' re-enabled by admin. Reason: {reason}",
                        oldValue: "Disabled",
                        newValue: "Enabled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to record user history for persona enable {PersonaId}", personaId);
                }
            }

            // Send notification email
            if (!string.IsNullOrEmpty(creatorEmail))
            {
                await SendPersonaStatusEmailAsync(persona, isEnabled: true, reason, baseUrl, creatorEmail);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enabling persona {PersonaId}", personaId);
            throw;
        }
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

    private async Task SendPersonaStatusEmailAsync(CreatorPersona persona, bool isEnabled, string reason,
        string baseUrl, string creatorEmail)
    {
        try
        {
            var logoUrl = _emailService.GetLogoUrl();
            var action = isEnabled ? "Re-Enabled" : "Disabled";
            var accentColor = isEnabled ? "#28a745" : "#dc3545";
            var subject = $"Your Persona Has Been {action} - {persona.Name}";

            string personaImageHtml = string.Empty;
            if (!string.IsNullOrEmpty(persona.ImageBlobPath))
            {
                try
                {
                    var imageUrl = GetPersonaImageSasUrl(persona.ImageBlobPath, TimeSpan.FromDays(1));
                    if (!string.IsNullOrEmpty(imageUrl))
                        personaImageHtml = $"<div style='text-align:center;margin:20px 0;'><img src='{imageUrl}' alt='Persona image' style='max-width:150px;border-radius:50%;'/></div>";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate image URL for persona {PersonaId}", persona.Id);
                }
            }

            var whatItMeans = isEnabled
                ? "<ul><li>Your persona is now visible on the site</li><li>Songs linked to this persona will display it as the artist name</li></ul>"
                : "<ul><li>Your persona will no longer appear on the site</li><li>Songs linked to this persona will fall back to your default artist name</li></ul>";

            var body = $@"
<div style='text-align:center;margin-bottom:20px;'>
    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width:150px;height:auto;'/>
</div>
<h2>Persona {action}</h2>
<p>Your persona <strong>{persona.Name}</strong> has been {action.ToLower()} on StreamTunes.</p>
{personaImageHtml}
<h3>Reason</h3>
<p style='background-color:#f8f9fa;padding:15px;border-radius:5px;border-left:4px solid {accentColor};'>{reason}</p>
<h3>What This Means</h3>
{whatItMeans}
<h3>Questions?</h3>
<p>If you have any questions, contact our customer service team at <a href='mailto:{_customerServiceEmail}'>{_customerServiceEmail}</a>.</p>
<p style='color:#999;font-size:12px;margin-top:30px;'>
    <a href='{baseUrl.TrimEnd('/')}/manage-account' style='color:#666;text-decoration:underline;'>Manage your email preferences</a>
</p>";

            await _emailService.SendEmailAsync(creatorEmail, subject, body);
            _logger.LogInformation("Sent persona {Action} notification email to {Email} for persona {PersonaId}",
                action, creatorEmail, persona.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send persona status email for persona {PersonaId}", persona.Id);
        }
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
