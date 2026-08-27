#nullable enable
using Microsoft.AspNetCore.Http;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Builds the manifest URL a player should be pointed at.
///
/// <para>
/// Exists so that the several places handing out a playable URL — the mobile catalogue mapper, the
/// three Blazor players, the creator's timing editor — all mint the same shape of URL with the same
/// entitlement decision baked into it. Before this, each of those sites called
/// <c>GetReadSasUri</c> itself with its own lifetime and its own idea of who was allowed what, which
/// is how one of them ended up handing 24-hour SAS URLs to anonymous callers.
/// </para>
/// </summary>
public interface IHlsStreamUrlFactory
{
    /// <summary>
    /// The manifest URL for a song, or null when the song has no package yet — during the rollout
    /// that is most of the catalogue, and callers fall back to whatever they served before.
    /// </summary>
    /// <param name="song">The song. Only its id and <c>HlsStreamId</c> are read.</param>
    /// <param name="userId">The listener, or null when anonymous.</param>
    /// <param name="hasFullAccess">
    /// Whether this listener may hear the whole song. Decided by the caller, which already knows —
    /// re-deriving it here would mean a subscription lookup per song in a catalogue listing.
    /// </param>
    string? BuildManifestUrl(SongMetadata song, int? userId, bool hasFullAccess);
}

/// <inheritdoc />
public sealed class HlsStreamUrlFactory : IHlsStreamUrlFactory
{
    /// <summary>
    /// Route template for a manifest. Kept beside the factory rather than in the controller so the
    /// producer and the consumer of this URL cannot drift apart silently — a mismatch would show up
    /// only as a 404 at playback time.
    /// </summary>
    public const string ManifestRouteFormat = "api/stream/{0}/index.m3u8";

    /// <summary>Route template for a content key.</summary>
    public const string KeyRouteFormat = "api/stream/{0}/key";

    private readonly IHlsStreamTokenService _tokens;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HlsStreamUrlFactory(IHlsStreamTokenService tokens, IHttpContextAccessor httpContextAccessor)
    {
        _tokens = tokens;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string? BuildManifestUrl(SongMetadata song, int? userId, bool hasFullAccess)
    {
        if (song?.HlsStreamId == null)
        {
            return null;
        }

        var token = _tokens.Issue(new HlsStreamTokenPayload(
            song.Id,
            HlsTokenKind.Manifest,
            userId,
            hasFullAccess));

        var path = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            ManifestRouteFormat,
            song.Id);

        // Absolute where a request is in scope, because the mobile app has no base URL to resolve
        // against - it is handed fully-formed URLs today and must keep being. The relative form is
        // only for the Blazor players, which are same-origin anyway.
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null)
        {
            return $"/{path}?t={Uri.EscapeDataString(token)}";
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}/{path}?t={Uri.EscapeDataString(token)}";
    }
}
