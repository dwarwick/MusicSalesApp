#nullable enable
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// Serves encrypted HLS: the per-listener manifest, and the content key it points at.
///
/// <para>
/// These two endpoints are the entire security boundary for audio, and it has two halves doing
/// different jobs. <see cref="GetKey"/> guards the AES-128 content key behind a token this app
/// minted seconds earlier for one song. <see cref="GetManifest"/> guards <em>reach</em>: it stamps a
/// read SAS onto each segment it names and onto no others, so a listener can fetch exactly what
/// they were given.
/// </para>
///
/// <para>
/// Both halves are load-bearing, because neither works alone. A song has exactly one content key
/// and every segment is encrypted with it, so the key cannot tell a preview listener from a
/// subscriber — both must be able to decrypt what they receive. Preview enforcement is therefore
/// entirely a question of which segments are reachable, and lives in the manifest rather than here.
/// </para>
///
/// <para>
/// Both actions are <c>[AllowAnonymous]</c> with explicit schemes listed. The schemes matter even
/// for an anonymous action: without them the MAUI app's bearer token is ignored, because the app's
/// default authenticate scheme is the Identity cookie — the same trap already documented on
/// <c>MusicController.RecordStream</c>. An anonymous listener is a real case here, since they are
/// entitled to a preview.
/// </para>
/// </summary>
[Route("api/stream")]
[ApiController]
public class StreamController : ControllerBase
{
    private const string AuthenticationSchemes = "Identity.Application,Bearer";

    private readonly ISongMetadataService _songMetadataService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IHlsManifestBuilder _manifestBuilder;
    private readonly IHlsStreamTokenService _tokens;
    private readonly IHlsContentKeyProtector _keyProtector;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IOptions<HlsOptions> _options;
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        ISongMetadataService songMetadataService,
        ISubscriptionService subscriptionService,
        IHlsManifestBuilder manifestBuilder,
        IHlsStreamTokenService tokens,
        IHlsContentKeyProtector keyProtector,
        UserManager<ApplicationUser> userManager,
        IOptions<HlsOptions> options,
        ILogger<StreamController> logger)
    {
        _songMetadataService = songMetadataService;
        _subscriptionService = subscriptionService;
        _manifestBuilder = manifestBuilder;
        _tokens = tokens;
        _keyProtector = keyProtector;
        _userManager = userManager;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// The manifest for one song, tailored to one listener.
    ///
    /// <para>
    /// Accepts either a manifest token minted by <see cref="IHlsStreamUrlFactory"/> or an ordinary
    /// authenticated session. Both are needed: the token form is what lets a native player fetch a
    /// manifest without custom header plumbing, and the session form is what lets the Blazor players
    /// request one directly with the cookie the browser already sends.
    /// </para>
    /// </summary>
    [HttpGet("{songMetadataId:int}/index.m3u8")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes)]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetManifest(int songMetadataId, [FromQuery(Name = "t")] string? token)
    {
        var song = await _songMetadataService.GetByIdAsync(songMetadataId);
        if (song is not { IsActive: true, IsEnabled: true })
        {
            return NotFound();
        }

        if (song.HlsStreamId is not { } streamId)
        {
            // The song exists but has not been packaged. Not something the listener caused and not
            // something a retry fixes, so it is a plain 404 - the caller should not have been given
            // this URL at all, and during the rollout that is most of the catalogue.
            return NotFound();
        }

        bool hasFullAccess;
        int? userId;

        if (_tokens.TryValidate(token, songMetadataId, HlsTokenKind.Manifest, out var payload))
        {
            // Entitlement was decided when the token was minted. Re-deriving it here would let a
            // subscription that lapsed since then cut off a song mid-play.
            hasFullAccess = payload.HasFullAccess;
            userId = payload.UserId;
        }
        else
        {
            var user = await _userManager.GetUserAsync(User);
            userId = user?.Id;
            hasFullAccess = user != null
                && await _subscriptionService.HasActiveSubscriptionAsync(user.Id);
        }

        // A fresh key token per manifest, never the caller's. The manifest token may be hours old;
        // this one has to be seconds old, because it is the one that opens the key.
        var keyToken = _tokens.Issue(new HlsStreamTokenPayload(
            songMetadataId,
            HlsTokenKind.Key,
            userId,
            hasFullAccess));

        var keyUri = BuildKeyUri(songMetadataId, keyToken);

        var previewLimit = hasFullAccess
            ? (TimeSpan?)null
            : _options.Value?.PreviewDuration ?? TimeSpan.FromSeconds(60);

        var manifest = await _manifestBuilder.BuildAsync(
            new HlsManifestRequest(streamId, keyUri, previewLimit),
            HttpContext.RequestAborted);

        if (manifest == null)
        {
            // The row says this song has a package and storage disagrees. 503, not 404: the song is
            // real and should play, something is wrong on our side, and a 404 would send whoever
            // investigates looking for a missing song rather than a missing package.
            _logger.LogError(
                "Song {SongMetadataId} has HlsStreamId {StreamId} but no manifest in storage. "
                + "Run the HLS repair backfill - this is what a partial restore looks like.",
                songMetadataId,
                streamId);

            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Content(manifest, HlsPackagePaths.ManifestContentType);
    }

    /// <summary>
    /// The 16-byte AES-128 content key.
    ///
    /// <para>
    /// The checks are, in order: an <c>Origin</c>/<c>Referer</c> the site recognises, when any are
    /// configured; a valid unexpired key token issued for <em>this</em> song; and the song still
    /// being published.
    /// </para>
    ///
    /// <para>
    /// Note what is <b>not</b> checked: the token's <c>HasFullAccess</c>. A preview listener is
    /// entitled to this key and cannot play their preview without it, and there is no narrower key
    /// to give them — one key encrypts the whole song. Their preview is enforced by the segments
    /// their manifest named and signed, per the class remarks. Adding an entitlement check here
    /// would break previews without closing anything.
    /// </para>
    /// </summary>
    [HttpGet("{songMetadataId:int}/key")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes)]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetKey(int songMetadataId, [FromQuery(Name = "t")] string? token)
    {
        if (!IsOriginAllowed())
        {
            // Defence in depth over the token, never instead of it: anything that is not a browser
            // can send whatever Origin it likes. It costs nothing and stops the simplest hotlinking.
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Validity only; the payload's entitlement is deliberately not consulted here. See the
        // remarks above - the key is the same for every listener of this song by construction, and
        // what separates them is which segments they hold a SAS for.
        if (!_tokens.TryValidate(token, songMetadataId, HlsTokenKind.Key, out _))
        {
            return Unauthorized();
        }

        var song = await _songMetadataService.GetByIdAsync(songMetadataId);
        if (song is not { IsActive: true, IsEnabled: true } || string.IsNullOrWhiteSpace(song.HlsKeyProtected))
        {
            return NotFound();
        }

        byte[] contentKey;
        try
        {
            contentKey = _keyProtector.Unprotect(songMetadataId, song.HlsKeyProtected);
        }
        catch (CryptographicException ex)
        {
            // The stored key will not unwrap: the wrapping key was rotated without re-wrapping the
            // rows, or the row was edited. No listener can fix that and a retry will not help.
            _logger.LogError(
                ex,
                "Could not unwrap the content key for song {SongMetadataId}. "
                + "Check Hls:ContentKeyWrappingKey against what the rows were wrapped with.",
                songMetadataId);

            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        SetExactOriginHeader();

        return File(contentKey, "application/octet-stream");
    }

    /// <summary>
    /// The absolute key URL written into a manifest. Absolute because a player resolves the
    /// <c>#EXT-X-KEY</c> URI against the manifest's own URL, and a relative one would break the
    /// moment a manifest is fetched through anything but the exact path we expect.
    /// </summary>
    private string BuildKeyUri(int songMetadataId, string keyToken)
    {
        var path = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HlsStreamUrlFactory.KeyRouteFormat,
            songMetadataId);

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}/{path}?t={Uri.EscapeDataString(keyToken)}";
    }

    /// <summary>
    /// True when the request carries no origin information at all, or carries one we recognise.
    ///
    /// <para>
    /// A missing <c>Origin</c> and <c>Referer</c> is allowed on purpose: native players send
    /// neither, and refusing them would mean the mobile apps could never play encrypted audio. This
    /// is a browser-only signal and is treated as one.
    /// </para>
    /// </summary>
    private bool IsOriginAllowed()
    {
        var allowed = _options.Value?.AllowedKeyOrigins;
        if (allowed == null || allowed.Count == 0)
        {
            return true;
        }

        var origin = Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            var referer = Request.Headers.Referer.ToString();
            if (string.IsNullOrWhiteSpace(referer))
            {
                return true;
            }

            if (!Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                return false;
            }

            origin = $"{refererUri.Scheme}://{refererUri.Authority}";
        }

        return IsAllowedOrigin(origin);
    }

    /// <summary>
    /// Echoes the exact allowed origin, never <c>*</c>. A wildcard here would let any page on the
    /// internet read a key out of a fetch it made with a token it had somehow obtained.
    /// </summary>
    private void SetExactOriginHeader()
    {
        var origin = Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin) || !IsAllowedOrigin(origin))
        {
            return;
        }

        Response.Headers.AccessControlAllowOrigin = origin;
        Response.Headers.AccessControlAllowCredentials = "true";
    }

    private bool IsAllowedOrigin(string origin)
    {
        var allowed = _options.Value?.AllowedKeyOrigins;
        if (allowed == null || allowed.Count == 0)
        {
            return false;
        }

        return allowed.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate)
            && string.Equals(candidate.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }
}
