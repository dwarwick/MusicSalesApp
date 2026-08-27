#nullable enable
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <summary>
/// Mints the container-scoped read SAS that segment URLs carry.
///
/// <para>
/// The streaming container is private, so a player needs a credential to fetch segments. It is
/// scoped to the <em>container</em> rather than to each blob because one manifest lists dozens of
/// segments and per-blob signing would mean dozens of signatures per request for no benefit — a
/// caller who has one segment's SAS can reach that song's other segments regardless, and every one
/// of them is ciphertext.
/// </para>
///
/// <para>
/// Signing is a local HMAC against the account key: it costs no storage request at all, which is the
/// same reasoning <c>StorageBackupBlobGateway.CreateContainerReadSasQuery</c> documents.
/// </para>
/// </summary>
public interface IHlsSegmentSasProvider
{
    /// <summary>
    /// A read SAS query string for the streaming container, without the leading <c>?</c>.
    /// Returns null when SAS cannot be generated, which the caller reports rather than papers over.
    /// </summary>
    string? GetReadSasQuery();
}

/// <inheritdoc />
public sealed class HlsSegmentSasProvider : IHlsSegmentSasProvider
{
    private const string CacheKey = "hls-segment-sas";

    private readonly IBlobContainerFactory _containerFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptions<HlsOptions> _options;
    private readonly ILogger<HlsSegmentSasProvider> _logger;

    public HlsSegmentSasProvider(
        IBlobContainerFactory containerFactory,
        IMemoryCache cache,
        IOptions<HlsOptions> options,
        ILogger<HlsSegmentSasProvider> logger)
    {
        _containerFactory = containerFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? GetReadSasQuery()
    {
        if (_cache.TryGetValue<string>(CacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        var lifetime = _options.Value?.SegmentSasLifetime ?? TimeSpan.FromHours(8);
        if (lifetime <= TimeSpan.Zero)
        {
            lifetime = TimeSpan.FromHours(8);
        }

        var container = _containerFactory.GetStreamingContainer();

        if (!container.CanGenerateSasUri)
        {
            // Managed identity rather than an account key. Everything else in this app signs SAS the
            // same way, so this would be a deployment-wide change rather than something to work
            // around here.
            _logger.LogError(
                "Cannot sign a SAS for '{Container}'. Encrypted playback needs a key-based connection string.",
                container.Name);

            return null;
        }

        string query;
        try
        {
            var sasUri = container.GenerateSasUri(
                BlobContainerSasPermissions.Read,
                DateTimeOffset.UtcNow.Add(lifetime));

            query = sasUri.Query.TrimStart('?');
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not sign a segment SAS for '{Container}'.", container.Name);
            return null;
        }

        // Cached for a fraction of its life, so every SAS handed out still has most of its lifetime
        // left. Caching for the full lifetime would eventually serve one with seconds remaining,
        // and playback would fail part-way through a song for no visible reason.
        _cache.Set(CacheKey, query, lifetime / 8);

        return query;
    }
}
