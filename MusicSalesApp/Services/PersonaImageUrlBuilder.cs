#nullable enable
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Builds server-proxied URLs for persona avatars.
///
/// <para>
/// Persona images used to be addressed with a direct Azure SAS URL. A SAS is minted per call with
/// an expiry computed from the current time, so every render produced a different <c>se</c> and
/// <c>sig</c> - a different URL, and therefore a guaranteed browser cache miss. On the library
/// page, which renders one avatar per card, that meant re-downloading every persona image on every
/// page load however unchanged the underlying blob was.
/// </para>
///
/// <para>
/// The proxy route is stable and versioned instead, exactly as cover art already is: the endpoint
/// serves an immutable year-long cache header, and <see cref="Models.CreatorPersona.ImageVariantVersion"/>
/// - which is already incremented whenever renditions are regenerated - is emitted as <c>?v=</c>
/// so a replaced image is not served from a stale cache.
/// </para>
/// </summary>
public interface IPersonaImageUrlBuilder
{
    /// <summary>
    /// A single proxied URL for the rendition best suited to a known fixed display size, falling
    /// back to the full-size blob when nothing is large enough or no renditions exist. Returns null
    /// when there is no image, or when the path fails encoding.
    /// </summary>
    string? BuildProxy(string? imageBlobPath, string? variantWidthsCsv, int displayWidthCssPx, int version);
}

/// <inheritdoc />
public sealed class PersonaImageUrlBuilder : IPersonaImageUrlBuilder
{
    private const string PersonaArtRoute = "api/persona-art";

    /// <inheritdoc />
    public string? BuildProxy(string? imageBlobPath, string? variantWidthsCsv, int displayWidthCssPx, int version)
    {
        if (string.IsNullOrWhiteSpace(imageBlobPath))
            return null;

        // Persona avatars render at a handful of fixed sizes (20-200 CSS px), so there is nothing
        // for a srcset to choose between - just pick the rendition once, here. Double the CSS width
        // so the image is still sharp on a 2x display. Same selection the SAS path made, so the
        // switch to the proxy does not change which blob a surface asks for.
        var width = ImageVariantSizes.SelectAtLeast(variantWidthsCsv, displayWidthCssPx * 2);
        var path = width.HasValue ? ImageVariantPaths.Variant(imageBlobPath, width.Value) : imageBlobPath;

        var encoded = CoverArtUrlBuilder.SafeEncodePath(path);
        if (string.IsNullOrEmpty(encoded))
            return null;

        return $"{PersonaArtRoute}/{encoded}?v={version}";
    }
}
