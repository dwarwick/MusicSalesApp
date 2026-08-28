#nullable enable
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <summary>
/// Mints the read SAS that segment URLs carry.
///
/// <para>
/// Scoped to <b>one blob</b>, not to the container, and that is the whole point. There is exactly
/// one AES-128 content key per song and every segment is encrypted with it, so the key cannot
/// distinguish a preview listener from a subscriber — both must be able to decrypt what they are
/// given. The only thing that can tell them apart is which segments they can <em>fetch</em>.
/// </para>
///
/// <para>
/// A container-scoped SAS therefore quietly undoes the server-side preview: segment names are
/// deterministic (<c>seg-000.ts</c>, <c>seg-001.ts</c>, …) and the package folder is visible in the
/// URLs of the segments the listener legitimately received, so anyone holding one container SAS can
/// enumerate and fetch the segments their manifest deliberately omitted, and decrypt them with the
/// key they were legitimately handed. Signing per blob makes the manifest the entitlement: the
/// listener holds credentials for exactly the segments they were given and nothing else.
/// </para>
///
/// <para>
/// Signing is a local HMAC against the account key and costs no storage request at all, which is
/// what makes per-blob affordable — a four-minute song is roughly forty signatures per manifest,
/// and a manifest is built once per play. The same reasoning
/// <c>StorageBackupBlobGateway.CreateContainerReadSasQuery</c> documents.
/// </para>
/// </summary>
public interface IHlsSegmentSasProvider
{
    /// <summary>
    /// A read SAS query string for one blob in the streaming container, without the leading <c>?</c>.
    /// Returns null when SAS cannot be generated, which the caller reports rather than papers over.
    /// </summary>
    /// <param name="blobPath">Path within the streaming container, e.g. <c>{streamId:N}/seg-000.ts</c>.</param>
    string? GetReadSasQuery(string blobPath);
}

/// <inheritdoc />
public sealed class HlsSegmentSasProvider : IHlsSegmentSasProvider
{
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IOptions<HlsOptions> _options;
    private readonly ILogger<HlsSegmentSasProvider> _logger;

    /// <summary>
    /// Ensures a deployment that cannot sign says so once rather than once per segment. Without it a
    /// misconfiguration would emit a log line per segment per play — tens of thousands of identical
    /// errors during exactly the incident someone is trying to read the log to understand.
    /// </summary>
    private int _signingUnavailableLogged;

    public HlsSegmentSasProvider(
        IBlobContainerFactory containerFactory,
        IOptions<HlsOptions> options,
        ILogger<HlsSegmentSasProvider> logger)
    {
        _containerFactory = containerFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? GetReadSasQuery(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        var lifetime = _options.Value?.SegmentSasLifetime ?? TimeSpan.FromHours(8);
        if (lifetime <= TimeSpan.Zero)
        {
            lifetime = TimeSpan.FromHours(8);
        }

        var container = _containerFactory.GetStreamingContainer();
        var blob = container.GetBlobClient(blobPath);

        if (!blob.CanGenerateSasUri)
        {
            // Managed identity rather than an account key. Everything else in this app signs SAS the
            // same way, so this would be a deployment-wide change rather than something to work
            // around here.
            if (Interlocked.Exchange(ref _signingUnavailableLogged, 1) == 0)
            {
                _logger.LogError(
                    "Cannot sign a SAS for '{Container}'. Encrypted playback needs a key-based connection string.",
                    container.Name);
            }

            return null;
        }

        try
        {
            // Signed fresh per manifest rather than cached. A cached credential is eventually handed
            // out with minutes left on it, and playback then dies part-way through a song for no
            // visible reason; signing costs microseconds, so there was never anything to buy.
            var sasUri = blob.GenerateSasUri(
                BlobSasPermissions.Read,
                DateTimeOffset.UtcNow.Add(lifetime));

            return sasUri.Query.TrimStart('?');
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _signingUnavailableLogged, 1) == 0)
            {
                _logger.LogError(ex, "Could not sign a segment SAS for '{Container}'.", container.Name);
            }

            return null;
        }
    }
}
