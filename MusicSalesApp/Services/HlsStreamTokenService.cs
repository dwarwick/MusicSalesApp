#nullable enable
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <summary>
/// Which door a token opens. The two are never interchangeable, and keeping them apart is what
/// allows their lifetimes to differ by three orders of magnitude.
/// </summary>
public enum HlsTokenKind
{
    /// <summary>
    /// Authorises fetching a manifest. Long-lived, because the catalogue endpoint mints these in
    /// bulk hours before anything is played. Worth little on its own: it names public, encrypted
    /// segments and is refused at the key endpoint.
    /// </summary>
    Manifest,

    /// <summary>
    /// Authorises fetching the content key. Embedded in a manifest at the moment it is generated and
    /// alive for about a minute. This is the one that actually protects the audio.
    /// </summary>
    Key
}

/// <summary>
/// What a stream or key token asserts. Deliberately small: a token authorises <em>one song, for one
/// listener, for one purpose, for a bounded time</em>, and nothing else.
/// </summary>
/// <param name="SongMetadataId">
/// The song this token is good for. Checked against the route, so a token lifted from one song's
/// manifest cannot be replayed against another's key endpoint.
/// </param>
/// <param name="Kind">
/// Which endpoint will accept it. Checked on validation, so the long-lived manifest token handed out
/// with the catalogue can never be used to fetch a key — without this the generous manifest lifetime
/// would become the key's lifetime too, and the whole scheme would collapse to a 24-hour key URL.
/// </param>
/// <param name="UserId">
/// Who it was issued to, or null for an anonymous listener. Recorded so a leaked token can be traced
/// to an account.
/// </param>
/// <param name="HasFullAccess">
/// Whether the listener was entitled to the whole song when the token was issued. Baked in on
/// purpose: a subscription that lapses mid-song should not cut the audio off, and re-checking on
/// every request would put a database round trip in the playback path.
/// </param>
public sealed record HlsStreamTokenPayload(
    int SongMetadataId,
    HlsTokenKind Kind,
    int? UserId,
    bool HasFullAccess);

/// <summary>
/// Issues and validates the tokens that gate manifest and key requests.
///
/// <para>
/// These are the one part of the design that is genuinely transient, so unlike the content keys they
/// do use ASP.NET Data Protection — losing the key ring invalidates tokens in flight, every player
/// asks for a new manifest, and nothing is permanently lost. That is exactly the disposability the
/// key ring is designed around, and why content keys must not depend on it.
/// </para>
///
/// <para>
/// Tokens travel in the query string rather than a header, and that is a requirement rather than a
/// shortcut: a native player fetches the manifest, the segments and the AES key through its own HTTP
/// stack. ExoPlayer's HLS key loader and AVPlayer both use the URI in the manifest as written, with
/// no way to attach an Authorization header short of replacing the data source. Putting the
/// credential in the URL is what will let the mobile apps play encrypted audio without custom
/// native plumbing on either platform.
/// </para>
/// </summary>
public interface IHlsStreamTokenService
{
    /// <summary>Mints a token for one song, listener and purpose.</summary>
    string Issue(HlsStreamTokenPayload payload);

    /// <summary>
    /// Validates a token and confirms it was issued for this song <em>and this purpose</em>.
    /// Returns false for anything expired, tampered with, issued for a different song, or issued as
    /// the other kind.
    /// </summary>
    bool TryValidate(
        string? token,
        int expectedSongMetadataId,
        HlsTokenKind expectedKind,
        out HlsStreamTokenPayload payload);
}

/// <inheritdoc />
public sealed class HlsStreamTokenService : IHlsStreamTokenService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan FallbackKeyLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FallbackManifestLifetime = TimeSpan.FromHours(24);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly IOptions<HlsOptions> _options;

    public HlsStreamTokenService(IDataProtectionProvider dataProtectionProvider, IOptions<HlsOptions> options)
    {
        _protector = dataProtectionProvider
            .CreateProtector("HlsStreaming", "StreamToken")
            .ToTimeLimitedDataProtector();
        _options = options;
    }

    /// <inheritdoc />
    public string Issue(HlsStreamTokenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        return _protector.Protect(json, LifetimeFor(payload.Kind));
    }

    /// <inheritdoc />
    public bool TryValidate(
        string? token,
        int expectedSongMetadataId,
        HlsTokenKind expectedKind,
        out HlsStreamTokenPayload payload)
    {
        payload = default!;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        HlsStreamTokenPayload? decoded;
        try
        {
            var json = _protector.Unprotect(token);
            decoded = JsonSerializer.Deserialize<HlsStreamTokenPayload>(json, SerializerOptions);
        }
        catch (Exception)
        {
            // Expired, tampered with, or protected by a key ring this instance no longer has. All
            // three mean "ask for a new manifest", and none is worth distinguishing to a caller who
            // could use the difference to probe.
            return false;
        }

        if (decoded == null
            || decoded.SongMetadataId != expectedSongMetadataId
            || decoded.Kind != expectedKind)
        {
            return false;
        }

        payload = decoded;
        return true;
    }

    private TimeSpan LifetimeFor(HlsTokenKind kind)
    {
        var options = _options?.Value;

        var configured = kind == HlsTokenKind.Key
            ? options?.KeyTokenLifetime
            : options?.ManifestTokenLifetime;

        var fallback = kind == HlsTokenKind.Key ? FallbackKeyLifetime : FallbackManifestLifetime;

        // A misconfigured non-positive lifetime would otherwise mint tokens that are already expired,
        // i.e. silently disable playback rather than loosen it. Fall back rather than throw.
        return configured is { } lifetime && lifetime > TimeSpan.Zero ? lifetime : fallback;
    }
}
