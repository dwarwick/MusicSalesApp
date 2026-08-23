using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers
{
    /// <summary>
    /// Serves persona avatars to the public pages behind a stable, versioned URL.
    ///
    /// <para>
    /// This exists so persona images can be cached at all. They were previously addressed with a
    /// direct Azure SAS URL minted per render, which changed the query string every time and so
    /// never hit the browser cache - one full re-download per avatar per page load. Cover art has
    /// always been proxied this way through <see cref="MusicController"/>; personas simply live in
    /// a different blob container, which is the only reason they need their own endpoint.
    /// </para>
    /// </summary>
    [Route("api/persona-art")]
    [ApiController]
    [AllowAnonymous]
    public class PersonaArtController : ControllerBase
    {
        private readonly ICreatorPersonaService _personaService;

        public PersonaArtController(ICreatorPersonaService personaService)
        {
            _personaService = personaService;
        }

        [HttpGet("{*blobPath}")]
        public async Task<IActionResult> Get(string blobPath)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                return BadRequest();

            // The path arrives from the caller, so it is checked against the database before any
            // blob is touched: it must belong to a persona that is currently enabled. A disabled
            // persona's avatar is not public, and guessing a path must not be a way around that.
            if (!await _personaService.IsPubliclyReadableImagePathAsync(blobPath))
                return NotFound();

            var stream = await _personaService.OpenPersonaImageReadAsync(blobPath);
            var contentTypeSource = blobPath;

            if ((stream == null || stream.Length == 0)
                && ImageVariantPaths.TryParseVariant(blobPath, out var masterPath, out _))
            {
                // A rendition can be legitimately absent: mid-backfill, or restored from a backup
                // taken before the backfill ran. Serve the full-size master rather than 404ing, so
                // the feature fails soft - the same choice the song media endpoint makes.
                stream = await _personaService.OpenPersonaImageReadAsync(masterPath);
                contentTypeSource = masterPath;
            }

            if (stream == null || stream.Length == 0)
                return NotFound();

            // The URL carries ?v={ImageVariantVersion}, which is incremented whenever renditions are
            // regenerated, so a replaced image gets a new URL rather than waiting out this header.
            Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";

            return File(stream, ContentTypeFor(contentTypeSource), enableRangeProcessing: true);
        }

        private static string ContentTypeFor(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".webp" => ImageVariantPaths.VariantContentType,
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => "application/octet-stream"
            };
        }
    }
}
