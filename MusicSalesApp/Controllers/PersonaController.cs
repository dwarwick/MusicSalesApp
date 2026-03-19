using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// API controller for creator persona image operations.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PersonaController : ControllerBase
{
    private readonly ICreatorPersonaService _personaService;
    private readonly ICreatorService _creatorService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly BlobContainerClient _personaContainerClient;
    private readonly ILogger<PersonaController> _logger;

    public PersonaController(
        ICreatorPersonaService personaService,
        ICreatorService creatorService,
        UserManager<ApplicationUser> userManager,
        IOptions<AzureStorageOptions> options,
        ILogger<PersonaController> logger)
    {
        _personaService = personaService;
        _creatorService = creatorService;
        _userManager = userManager;
        _logger = logger;

        var opts = options.Value;
        if (!string.IsNullOrWhiteSpace(opts.StorageAccountConnectionString) &&
            !string.IsNullOrWhiteSpace(opts.PersonaImageContainerName))
        {
            _personaContainerClient = new BlobContainerClient(
                opts.StorageAccountConnectionString,
                opts.PersonaImageContainerName);
        }
    }

    /// <summary>
    /// Returns the creator ID for the current user, or null if not a creator.
    /// </summary>
    private async Task<int?> GetCallerCreatorIdAsync()
    {
        var appUser = await _userManager.GetUserAsync(User);
        if (appUser == null) return null;
        return await _creatorService.GetCreatorIdForUserAsync(appUser.Id);
    }

    /// <summary>
    /// Returns true when <paramref name="blobPath"/> resides inside the creator's own
    /// blob namespace (<c>creator-{creatorId}/…</c>).
    /// </summary>
    private static bool BlobPathBelongsToCreator(string blobPath, int creatorId)
        => blobPath.StartsWith($"creator-{creatorId}/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serves a persona image as a same-origin proxy so the browser crop tool can load it
    /// without CORS/tainted-canvas issues.
    /// Only the owning creator may fetch their own persona images through this endpoint.
    /// </summary>
    [HttpGet("image/{*blobPath}")]
    public async Task<IActionResult> GetPersonaImage(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
            return BadRequest();

        if (blobPath.Contains("..") || blobPath.Contains("~"))
            return BadRequest();

        // Enforce that the requested blob belongs to the authenticated creator.
        var creatorId = await GetCallerCreatorIdAsync();
        if (creatorId == null)
            return Forbid();

        if (!BlobPathBelongsToCreator(blobPath, creatorId.Value))
            return Forbid();

        if (_personaContainerClient == null)
            return StatusCode(503, "Persona image storage is not configured.");

        try
        {
            var blobClient = _personaContainerClient.GetBlobClient(blobPath);
            if (!await blobClient.ExistsAsync())
                return NotFound();

            var download = await blobClient.DownloadAsync();
            var ext = Path.GetExtension(blobPath).ToLowerInvariant();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => "image/jpeg"
            };
            Response.Headers["Cache-Control"] = "public,max-age=3600";
            return File(download.Value.Content, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving persona image {BlobPath}", blobPath);
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Accepts a cropped persona image upload (as a binary blob from canvas) and stores it
    /// in the persona images Azure Blob container.
    /// The <paramref name="blobPath"/> must reside inside the calling creator's own namespace.
    /// </summary>
    [HttpPost("upload-cropped-image")]
    public async Task<IActionResult> UploadCroppedPersonaImage([FromQuery] string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
            return BadRequest(new { error = "blobPath is required" });

        if (blobPath.Contains("..") || blobPath.Contains("~"))
            return BadRequest(new { error = "Invalid blobPath" });

        // Enforce that the target blob belongs to the authenticated creator.
        var creatorId = await GetCallerCreatorIdAsync();
        if (creatorId == null)
            return Forbid();

        if (!BlobPathBelongsToCreator(blobPath, creatorId.Value))
            return Forbid();

        if (Request.ContentLength == null || Request.ContentLength == 0)
            return BadRequest(new { error = "No image data provided" });

        if (Request.ContentLength > 10 * 1024 * 1024)
            return BadRequest(new { error = "Image too large" });

        if (_personaContainerClient == null)
            return StatusCode(503, "Persona image storage is not configured.");

        try
        {
            // Ensure the container exists
            await _personaContainerClient.CreateIfNotExistsAsync();

            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            ms.Position = 0;

            var blobClient = _personaContainerClient.GetBlobClient(blobPath);
            var headers = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = "image/png" };
            await blobClient.UploadAsync(ms, new Azure.Storage.Blobs.Models.BlobUploadOptions { HttpHeaders = headers });

            _logger.LogInformation("Uploaded cropped persona image to {BlobPath}", blobPath);
            return Ok(new { blobPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading cropped persona image to {BlobPath}", blobPath);
            return StatusCode(500, new { error = "Upload failed" });
        }
    }
}
