#nullable enable
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>One rendition of an image, and the URL that serves it.</summary>
public sealed record CoverArtVariantUrl(int Width, string Url);

/// <summary>
/// Everything the <c>CoverArt</c> component needs to render one image: the full-size URL it always
/// falls back to, and the renditions a browser may pick from instead.
/// </summary>
public sealed record CoverArtSource(
    string? BaseUrl,
    IReadOnlyList<CoverArtVariantUrl> Variants)
{
    /// <summary>No image at all - the component renders nothing.</summary>
    public static readonly CoverArtSource None = new(null, Array.Empty<CoverArtVariantUrl>());

    public bool HasImage => !string.IsNullOrEmpty(BaseUrl);

    /// <summary>
    /// False before the backfill has run, or when generation failed. The component then emits a
    /// plain img tag identical to what the site rendered before renditions existed.
    /// </summary>
    public bool HasVariants => Variants.Count > 0;
}

/// <summary>
/// Builds the URL set for a piece of cover art under either delivery mechanism.
///
/// <para>
/// This replaces the <c>$"api/music/{SafeEncodePath(...)}"</c> literal that was copied across the
/// players, the library page and the management grids, so a change to how artwork is addressed
/// happens in one place.
/// </para>
/// </summary>
public interface ICoverArtUrlBuilder
{
    /// <summary>
    /// Server-proxied URLs, used by the public pages. The endpoint already serves media with a
    /// long-lived immutable cache header, hence the version query parameter.
    /// </summary>
    CoverArtSource BuildProxy(string? imageBlobPath, string? variantWidthsCsv, int version);

    /// <summary>
    /// Direct Azure SAS URLs, used by the admin and creator grids, which read blobs the public
    /// media whitelist deliberately will not serve (disabled or inactive songs).
    /// </summary>
    CoverArtSource BuildSas(string? imageBlobPath, string? variantWidthsCsv, int version, TimeSpan lifetime);

    /// <summary>
    /// A single SAS URL for the rendition best suited to a known fixed display size, falling back
    /// to the full-size blob when nothing is large enough or no renditions exist.
    ///
    /// <para>
    /// For surfaces that render at one fixed size - the management grids and edit dialogs - where a
    /// full candidate list would buy nothing over simply picking the right rendition once.
    /// </para>
    /// </summary>
    string? BuildSasForDisplayWidth(
        string? imageBlobPath, string? variantWidthsCsv, int displayWidthCssPx, TimeSpan lifetime);
}

/// <inheritdoc />
public sealed class CoverArtUrlBuilder : ICoverArtUrlBuilder
{
    private const string MediaRoute = "api/music";

    private readonly IAzureStorageService _storageService;
    private readonly ILogger<CoverArtUrlBuilder> _logger;

    public CoverArtUrlBuilder(IAzureStorageService storageService, ILogger<CoverArtUrlBuilder> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    /// <inheritdoc />
    public CoverArtSource BuildProxy(string? imageBlobPath, string? variantWidthsCsv, int version)
    {
        if (string.IsNullOrWhiteSpace(imageBlobPath))
            return CoverArtSource.None;

        var baseUrl = ProxyUrl(imageBlobPath, version);
        if (baseUrl == null)
            return CoverArtSource.None;

        var variants = new List<CoverArtVariantUrl>();
        foreach (var width in ImageVariantSizes.ParseCsv(variantWidthsCsv))
        {
            var url = ProxyUrl(ImageVariantPaths.Variant(imageBlobPath, width), version);
            if (url != null)
                variants.Add(new CoverArtVariantUrl(width, url));
        }

        return new CoverArtSource(baseUrl, variants);
    }

    /// <inheritdoc />
    public CoverArtSource BuildSas(string? imageBlobPath, string? variantWidthsCsv, int version, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(imageBlobPath))
            return CoverArtSource.None;

        var baseUrl = SasUrl(imageBlobPath, lifetime);
        if (baseUrl == null)
            return CoverArtSource.None;

        // One SAS per rendition. Each is a local HMAC over the blob name - no network call - so a
        // fifty-row management grid costs a couple of hundred cheap signatures.
        var variants = new List<CoverArtVariantUrl>();
        foreach (var width in ImageVariantSizes.ParseCsv(variantWidthsCsv))
        {
            var url = SasUrl(ImageVariantPaths.Variant(imageBlobPath, width), lifetime);
            if (url != null)
                variants.Add(new CoverArtVariantUrl(width, url));
        }

        // A SAS URL already carries a unique signature per issue, so it needs no version parameter;
        // the query string differs on every page load regardless.
        _ = version;

        return new CoverArtSource(baseUrl, variants);
    }

    /// <inheritdoc />
    public string? BuildSasForDisplayWidth(
        string? imageBlobPath, string? variantWidthsCsv, int displayWidthCssPx, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(imageBlobPath))
            return null;

        // Double the CSS width so the image stays sharp on a 2x display.
        var width = ImageVariantSizes.SelectAtLeast(variantWidthsCsv, displayWidthCssPx * 2);
        var path = width.HasValue ? ImageVariantPaths.Variant(imageBlobPath, width.Value) : imageBlobPath;

        return SasUrl(path, lifetime);
    }

    private static string? ProxyUrl(string blobPath, int version)
    {
        var encoded = SafeEncodePath(blobPath);
        if (string.IsNullOrEmpty(encoded))
            return null;

        // Cover art under the GUID scheme lives at a fixed path that a re-crop overwrites in place,
        // and the media endpoint serves it "immutable" for a year. Without this the browser would
        // keep showing the pre-crop image until the cache expired.
        return $"{MediaRoute}/{encoded}?v={version}";
    }

    private string? SasUrl(string blobPath, TimeSpan lifetime)
    {
        try
        {
            return _storageService.GetReadSasUri(blobPath, lifetime).ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mint a read SAS for {BlobPath}", blobPath);
            return null;
        }
    }

    /// <summary>
    /// Percent-encodes each path segment while leaving the separators intact, and refuses anything
    /// containing traversal characters. Consolidated here from three identical private copies in
    /// the creator, admin and persona pages.
    /// </summary>
    internal static string SafeEncodePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        if (filePath.Contains("..", StringComparison.Ordinal) || filePath.Contains('~'))
            return string.Empty;

        return string.Join("/", filePath.Split('/').Select(Uri.EscapeDataString));
    }
}
