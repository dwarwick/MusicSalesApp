#nullable enable
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Turns the manifest as FFmpeg wrote it into the manifest a specific listener is allowed to have.
///
/// <para>
/// The stored manifest is deliberately incomplete: its <c>#EXT-X-KEY</c> URI is a placeholder and
/// its segment lines are bare filenames. Neither is usable on its own, which is the point — the
/// package sitting in public storage describes how to decrypt nothing and where to find nothing
/// until this class fills both in, per request, for one listener.
/// </para>
/// </summary>
public interface IHlsManifestBuilder
{
    /// <summary>
    /// Builds a playable manifest, or returns null when the package is missing from storage.
    ///
    /// <para>
    /// Null is a real and important case: it is what a restore looks like when the streaming
    /// container came back empty while the database still carries every <c>HlsStreamId</c>. The
    /// caller turns it into a 503 rather than a 404, because "this song's package is gone" is an
    /// operational fault, not a bad request.
    /// </para>
    /// </summary>
    Task<string?> BuildAsync(HlsManifestRequest request, CancellationToken cancellationToken = default);
}

/// <param name="HlsStreamId">Which package to read.</param>
/// <param name="KeyUri">The fully-formed, already-tokenised URL the player should fetch the key from.</param>
/// <param name="PreviewLimit">
/// How much of the song to describe, or null for the whole thing. Non-null is what enforces the
/// free-preview rule: the segments beyond it are not merely unplayed, they are never named.
/// </param>
public sealed record HlsManifestRequest(Guid HlsStreamId, string KeyUri, TimeSpan? PreviewLimit);

/// <inheritdoc />
public sealed class HlsManifestBuilder : IHlsManifestBuilder
{
    /// <summary>How long a raw manifest is held in memory. It is immutable at its path, so this is only a bound on memory.</summary>
    private static readonly TimeSpan RawManifestCacheLifetime = TimeSpan.FromHours(6);

    private static readonly Regex KeyUriPattern = new(
        "URI=\"[^\"]*\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IBlobContainerFactory _containerFactory;
    private readonly IMemoryCache _cache;
    private readonly IHlsSegmentSasProvider _sasProvider;
    private readonly ILogger<HlsManifestBuilder> _logger;

    public HlsManifestBuilder(
        IBlobContainerFactory containerFactory,
        IMemoryCache cache,
        IHlsSegmentSasProvider sasProvider,
        ILogger<HlsManifestBuilder> logger)
    {
        _containerFactory = containerFactory;
        _cache = cache;
        _sasProvider = sasProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> BuildAsync(HlsManifestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var raw = await ReadRawManifestAsync(request.HlsStreamId, cancellationToken);
        if (raw == null)
        {
            return null;
        }

        var segmentBase = new Uri(
            _containerFactory.GetStreamingContainerUri().AbsoluteUri.TrimEnd('/')
            + "/" + HlsPackagePaths.Folder(request.HlsStreamId) + "/");

        // Null only when SAS signing is unavailable, which the provider has already logged as an
        // error. The manifest is still returned - every segment would 403, but a manifest with
        // unsigned URLs fails visibly at the player rather than silently here.
        var segmentSas = _sasProvider.GetReadSasQuery();

        return Rewrite(raw, segmentBase, request.KeyUri, request.PreviewLimit, segmentSas);
    }

    /// <summary>
    /// The whole transformation, as a pure function of its inputs.
    ///
    /// <para>
    /// Separated from the blob read so the rules that matter — key substitution, absolute segment
    /// URLs, and where a preview stops — are testable directly, without a storage account or a
    /// cache.
    /// </para>
    /// </summary>
    /// <param name="rawManifest">The manifest exactly as stored.</param>
    /// <param name="segmentBaseUri">Absolute URI of the package folder, with a trailing slash.</param>
    /// <param name="keyUri">The tokenised key URL to substitute in.</param>
    /// <param name="previewLimit">Null for the full song.</param>
    /// <param name="segmentSasQuery">
    /// Read SAS for the streaming container, without the leading <c>?</c>, or null when the container
    /// needs no credential. The container is private, so in practice this is always supplied.
    /// </param>
    public static string Rewrite(
        string rawManifest,
        Uri segmentBaseUri,
        string keyUri,
        TimeSpan? previewLimit,
        string? segmentSasQuery = null)
    {
        ArgumentNullException.ThrowIfNull(rawManifest);
        ArgumentNullException.ThrowIfNull(segmentBaseUri);

        var output = new StringBuilder();

        // Tag lines accumulate here until the segment they describe is either emitted or dropped.
        // Buffering rather than writing straight through is what lets a truncated preview end
        // cleanly: an #EXTINF whose segment we are not going to name must not be emitted either.
        var pending = new List<string>();

        var elapsed = 0d;
        var pendingDuration = 0d;
        var emittedSegments = 0;
        var truncated = false;

        foreach (var rawLine in rawManifest.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                // We always write our own, after any truncation, so the stored one is dropped.
                if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
                {
                    pending.Add(KeyUriPattern.Replace(line, $"URI=\"{keyUri}\"", 1));
                    continue;
                }

                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    pendingDuration = ParseExtInfDuration(line);
                }

                pending.Add(line);
                continue;
            }

            // A non-comment, non-blank line is a segment reference.

            // The cut is decided on the time already emitted, not on where this segment ends, so a
            // preview always covers at least the full preview window rather than stopping just short
            // of it. It overshoots by at most one segment, which the player's own cap trims.
            if (previewLimit.HasValue && emittedSegments > 0 && elapsed >= previewLimit.Value.TotalSeconds)
            {
                truncated = true;
                break;
            }

            output.AppendJoin('\n', pending).Append('\n');
            pending.Clear();

            var segmentUri = new Uri(segmentBaseUri, line.Trim()).AbsoluteUri;

            // The streaming container is private, so the URL carries a container read SAS. Appended
            // here rather than folded into segmentBaseUri because Uri resolution against a base
            // holding a query string would discard it.
            if (!string.IsNullOrEmpty(segmentSasQuery))
            {
                segmentUri += "?" + segmentSasQuery;
            }

            output.Append(segmentUri).Append('\n');

            elapsed += pendingDuration;
            pendingDuration = 0d;
            emittedSegments++;
        }

        // Anything still pending described a segment that was dropped, so it is discarded with it -
        // except when nothing was truncated, where it is the manifest's own trailer.
        if (!truncated && pending.Count > 0)
        {
            output.AppendJoin('\n', pending).Append('\n');
        }

        output.Append("#EXT-X-ENDLIST\n");
        return output.ToString();
    }

    /// <summary>
    /// Reads the stored manifest, caching it in memory.
    ///
    /// <para>
    /// Safe to cache indefinitely because a package is immutable at its path: repackaging mints a
    /// new <c>HlsStreamId</c> and therefore a new cache key, so a stale entry cannot be served. The
    /// expiry is only there to bound memory.
    /// </para>
    /// </summary>
    private async Task<string?> ReadRawManifestAsync(Guid hlsStreamId, CancellationToken cancellationToken)
    {
        var cacheKey = $"hls-manifest:{hlsStreamId:N}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        var blob = _containerFactory
            .GetStreamingContainer()
            .GetBlobClient(HlsPackagePaths.Manifest(hlsStreamId));

        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken);
            var text = response.Value.Content.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("HLS manifest {StreamId} is present but empty.", hlsStreamId);
                return null;
            }

            _cache.Set(cacheKey, text, RawManifestCacheLifetime);
            return text;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            // Deliberately not cached as a negative result. This is what a half-restored streaming
            // container looks like, and the repair pass fixes it by writing the package back - a
            // cached "missing" would keep the song dark until the site recycled.
            _logger.LogWarning(
                "HLS manifest {StreamId} is missing from the streaming container. "
                + "The song has a package recorded but storage does not hold it - run the HLS repair backfill.",
                hlsStreamId);
            return null;
        }
    }

    /// <summary>
    /// Reads the duration off an <c>#EXTINF:6.000000,</c> line. Zero when unparseable, which makes
    /// an odd line cost nothing rather than truncating a preview at the wrong place.
    /// </summary>
    private static double ParseExtInfDuration(string line)
    {
        var value = line["#EXTINF:".Length..];

        var comma = value.IndexOf(',');
        if (comma >= 0)
        {
            value = value[..comma];
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0d;
    }
}
