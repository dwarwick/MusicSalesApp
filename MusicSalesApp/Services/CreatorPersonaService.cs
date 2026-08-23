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
    private readonly IImageVariantCoordinator _imageVariantCoordinator;
    private readonly string _customerServiceEmail;

    public CreatorPersonaService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOptions<AzureStorageOptions> options,
        ILogger<CreatorPersonaService> logger,
        IAdminNotificationService adminNotificationService,
        IEmailService emailService,
        IImageVariantCoordinator imageVariantCoordinator,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _adminNotificationService = adminNotificationService;
        _emailService = emailService;
        _imageVariantCoordinator = imageVariantCoordinator;
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
    /// <inheritdoc />
    public async Task<bool> PersonaNameExistsAsync(int creatorId, string name, int? excludePersonaId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.CreatorPersonas
            .AnyAsync(p => p.CreatorId == creatorId
                && (excludePersonaId == null || p.Id != excludePersonaId)
                && p.Name.ToLower() == trimmed.ToLower());
    }

    public async Task<CreatorPersona> CreatePersonaAsync(int creatorId, string name, string? bio, string? websiteUrl)
    {
        // Enforced here as well as in the page, because the page is not the only way in and a
        // duplicate name is silently wrong rather than loudly wrong - two personas with the
        // same name are indistinguishable everywhere a song credits one.
        if (await PersonaNameExistsAsync(creatorId, name))
        {
            throw new InvalidOperationException($"You already have a persona called '{name.Trim()}'.");
        }

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

        // Send notification emails and record user history
        await SendPersonaChangeNotificationAsync(context, persona, "Created");

        return persona;
    }

    /// <inheritdoc />
    public async Task<CreatorPersona> UpdatePersonaAsync(int personaId, int creatorId, string name, string? bio,
        string? websiteUrl, string? imageBlobPath, int? imageWidth, int? imageHeight, bool sendNotification = true,
        bool imageReplaced = false)
    {
        // Renaming onto another persona's name is the same defect as creating a duplicate.
        if (await PersonaNameExistsAsync(creatorId, name, excludePersonaId: personaId))
        {
            throw new InvalidOperationException($"You already have a persona called '{name.Trim()}'.");
        }

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

        // Every persona image change lands here - the direct upload and the cropped upload both
        // call this immediately after writing the blob - which makes it the one place the
        // renditions need refreshing from.
        //
        // The caller has to say so explicitly: a replacement image keeping the same file extension
        // lands on the identical deterministic blob path (creator-{id}/persona-{id}{ext}), so
        // comparing paths would miss it entirely. Editing only the name or bio must not trigger a
        // pointless download-decode-reencode of an unchanged image.
        var previousImageBlobPath = persona.ImageBlobPath;

        if (imageBlobPath != null)
        {
            persona.ImageBlobPath = imageBlobPath;
        }
        if (imageWidth.HasValue) persona.ImageWidth = imageWidth;
        if (imageHeight.HasValue) persona.ImageHeight = imageHeight;
        persona.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();
        _logger.LogInformation("Updated persona {PersonaId} for creator {CreatorId}", personaId, creatorId);

        if (imageReplaced)
        {
            await _imageVariantCoordinator.RefreshPersonaVariantsAsync(personaId, previousImageBlobPath);
        }

        // Send notification emails and record user history
        if (sendNotification)
        {
            await SendPersonaChangeNotificationAsync(context, persona, "Updated");
        }

        return persona;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePersonaAsync(int personaId, int creatorId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var persona = await context.CreatorPersonas
            .Include(p => p.Creator)
                .ThenInclude(c => c.User)
            .FirstOrDefaultAsync(p => p.Id == personaId && p.CreatorId == creatorId);
        if (persona == null)
        {
            _logger.LogWarning("Persona {PersonaId} not found for creator {CreatorId}", personaId, creatorId);
            return false;
        }

        // Capture details for notification before deleting
        var personaName = persona.Name;
        var personaBio = persona.Bio;
        var personaImageBlobPath = persona.ImageBlobPath;

        // Email deliberately gets the full-size master rather than a WebP rendition: Outlook on
        // Windows renders mail through Word, which cannot decode WebP, so a rendition would show as
        // a broken image for a large share of recipients.
        var personaImageUrl = !string.IsNullOrEmpty(personaImageBlobPath)
            ? GetPersonaImageSasUrl(personaImageBlobPath, TimeSpan.FromDays(7))
            : null;
        var creatorEmail = persona.Creator?.User?.Email;

        // Send notification emails BEFORE deleting the blob so the SAS URL resolves
        if (!string.IsNullOrEmpty(creatorEmail))
        {
            try
            {
                await _adminNotificationService.NotifyPersonaDeletedAsync(
                    creatorId, creatorEmail, personaName, personaBio, personaImageUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send persona deleted notification for persona {PersonaId}", personaId);
            }
        }

        // Delete the image from blob storage
        if (!string.IsNullOrEmpty(personaImageBlobPath))
        {
            await DeletePersonaImageFromStorageAsync(personaImageBlobPath, persona.ImageVariantWidths);
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
                await DeletePersonaImageFromStorageAsync(persona.ImageBlobPath, persona.ImageVariantWidths);
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
            if (!blobClient.CanGenerateSasUri)
            {
                _logger.LogWarning("Cannot generate SAS URI for persona image {BlobPath}: credential does not support SAS generation.", blobPath);
                return string.Empty;
            }

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
    public async Task<bool> IsPubliclyReadableImagePathAsync(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
            return false;

        // A rendition path is its master's path with ".w{width}.webp" appended, so the master is
        // recoverable by string alone and one lookup covers both - the same trick the song media
        // whitelist uses. No extra query, no new index.
        var lookupPath = ImageVariantPaths.TryParseVariant(blobPath, out var masterPath, out _)
            ? masterPath
            : blobPath;

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.CreatorPersonas
            .AsNoTracking()
            .AnyAsync(p => p.IsEnabled
                        && p.ImageBlobPath != null
                        && p.ImageBlobPath == lookupPath);
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenPersonaImageReadAsync(string blobPath)
    {
        if (_personaContainerClient == null || string.IsNullOrWhiteSpace(blobPath))
            return null;

        try
        {
            var blobClient = _personaContainerClient.GetBlobClient(blobPath);
            if (!await blobClient.ExistsAsync())
                return null;

            return await blobClient.OpenReadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open persona image {BlobPath} for reading", blobPath);
            return null;
        }
    }

    /// <inheritdoc />
    public string GetPersonaImageSasUrl(string blobPath, string? variantWidthsCsv, int displayWidthCssPx, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
            return string.Empty;

        // Persona avatars render at a handful of fixed sizes (20-200 CSS px), so there is nothing
        // for a srcset to choose between - just pick the rendition once, here. Double the CSS width
        // so the image is still sharp on a 2x display.
        var width = ImageVariantSizes.SelectAtLeast(variantWidthsCsv, displayWidthCssPx * 2);

        // No rendition is big enough - or none exist yet - so serve the master, which is what the
        // site did before renditions existed.
        var path = width.HasValue ? ImageVariantPaths.Variant(blobPath, width.Value) : blobPath;

        return GetPersonaImageSasUrl(path, lifetime);
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

    /// <inheritdoc />
    public async Task<Dictionary<int, int>> GetPersonaSongCountsAsync(IEnumerable<int> personaIds)
    {
        var ids = personaIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<int, int>();

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.SongMetadata
            .Where(sm => sm.PersonaId != null && ids.Contains(sm.PersonaId.Value) && sm.IsActive)
            .GroupBy(sm => sm.PersonaId!.Value)
            .Select(g => new { PersonaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PersonaId, x => x.Count);
    }

    private async Task SendPersonaChangeNotificationAsync(AppDbContext context, CreatorPersona persona, string action)
    {
        try
        {
            // Load creator + user if not already loaded
            if (persona.Creator == null)
            {
                await context.Entry(persona).Reference(p => p.Creator).LoadAsync();
            }
            if (persona.Creator?.User == null && persona.Creator != null)
            {
                await context.Entry(persona.Creator).Reference(c => c.User).LoadAsync();
            }

            var creatorEmail = persona.Creator?.User?.Email;
            if (string.IsNullOrEmpty(creatorEmail))
            {
                _logger.LogWarning("Cannot send persona {Action} notification: creator email not found for persona {PersonaId}", action, persona.Id);
                return;
            }

            string? personaImageUrl = null;
            if (!string.IsNullOrEmpty(persona.ImageBlobPath))
            {
                try
                {
                    personaImageUrl = GetPersonaImageSasUrl(persona.ImageBlobPath, TimeSpan.FromDays(7));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate image URL for persona {PersonaId}", persona.Id);
                }
            }

            if (action == "Created")
            {
                await _adminNotificationService.NotifyPersonaCreatedAsync(
                    persona.CreatorId, creatorEmail, persona.Name, persona.Bio, personaImageUrl);
            }
            else if (action == "Updated")
            {
                await _adminNotificationService.NotifyPersonaUpdatedAsync(
                    persona.CreatorId, creatorEmail, persona.Name, persona.Bio, personaImageUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send persona {Action} notification for persona {PersonaId}", action, persona.Id);
        }
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
                    var imageUrl = GetPersonaImageSasUrl(persona.ImageBlobPath, TimeSpan.FromDays(7));
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

    /// <summary>
    /// Removes a persona image and every pre-resized rendition derived from it.
    ///
    /// <para>
    /// The renditions have to go with the master: nothing else ever references them, and the backfill
    /// only walks rows that still exist, so anything left here is orphaned permanently. The whole
    /// ladder is attempted rather than only the recorded widths, because deleting a blob that is not
    /// there costs one call and returns false - whereas a row whose widths were never recorded, or a
    /// master too narrow for any ladder rung, would otherwise strand its renditions silently.
    /// </para>
    /// </summary>
    private async Task DeletePersonaImageFromStorageAsync(string blobPath, string? variantWidthsCsv = null)
    {
        if (_personaContainerClient == null || string.IsNullOrWhiteSpace(blobPath))
            return;

        var widths = new List<int>(ImageVariantSizes.Persona);
        foreach (var recorded in ImageVariantSizes.ParseCsv(variantWidthsCsv))
        {
            if (!widths.Contains(recorded))
                widths.Add(recorded);
        }

        var paths = new List<string> { blobPath };
        paths.AddRange(ImageVariantPaths.VariantsFor(blobPath, widths));

        foreach (var path in paths)
        {
            try
            {
                var blobClient = _personaContainerClient.GetBlobClient(path);
                await blobClient.DeleteIfExistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete persona image {BlobPath}", path);
            }
        }

        _logger.LogInformation("Deleted persona image {BlobPath} and its renditions", blobPath);
    }

    /// <inheritdoc />
    public Task DeletePersonaImageBlobAsync(string blobPath) =>
        DeletePersonaImageFromStorageAsync(blobPath);

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
