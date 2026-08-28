#nullable enable
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <summary>
/// Hands out the blob containers for the environment this process is configured against.
///
/// <para>
/// <see cref="IAzureStorageService"/> is a singleton bound to one container in its constructor, so
/// it cannot reach the persona-image container. Rather than make it container-parameterised — which
/// would touch every one of its call sites, including the audio paths — this factory supplies the
/// second container to the handful of services that need both.
/// </para>
///
/// <para>
/// <b>There is deliberately no <c>GetContainer(string name)</c> overload.</b> The image-variant
/// backfill must never write to the <c>backup-*</c> containers: those are populated exclusively by
/// <see cref="StorageBackupService"/>, whose incremental diff depends on the source ETag it stamps
/// into each backup blob's metadata at copy time. Renditions written directly into a backup
/// container would carry none of that. Without a by-name overload the backfill has no way to
/// address those containers at all, so the rule is enforced by the type system rather than by
/// remembering it.
/// </para>
/// </summary>
public interface IBlobContainerFactory
{
    /// <summary>
    /// The song media container for the current environment — <c>musiccontainer</c> in production,
    /// <c>musiccontainer-dev</c> on the test server, <c>musiccontainer-local</c> for local dev.
    /// </summary>
    BlobContainerClient GetMediaContainer();

    /// <summary>The creator persona image container for the current environment.</summary>
    BlobContainerClient GetPersonaImageContainer();

    /// <summary>
    /// The encrypted-HLS streaming container for the current environment — <c>musicstreaming</c>,
    /// <c>musicstreaming-dev</c> or <c>musicstreaming-local</c>. Same storage account as the media
    /// container.
    ///
    /// <para>
    /// <b>Private, like every other container here</b>, and everything written to it is already
    /// encrypted. It was going to be the one public container in the product, but both storage
    /// accounts set <c>allowBlobPublicAccess: false</c> and that guardrail is worth keeping — this
    /// account holds every song master and the Data Protection key rings, for Production as well as
    /// Test. Segment URLs carry a container read SAS instead
    /// (<see cref="IHlsSegmentSasProvider"/>), which costs nothing: signing is local, and the bytes
    /// it addresses are AES-128 ciphertext either way.
    /// </para>
    /// </summary>
    BlobContainerClient GetStreamingContainer();

    /// <summary>
    /// The base URI of the streaming container, e.g.
    /// <c>https://highspeedstorageaccount.blob.core.windows.net/musicstreaming-dev</c>. The manifest
    /// builder stamps absolute segment URLs from this, since the player fetches them from storage
    /// directly rather than through this app.
    /// </summary>
    Uri GetStreamingContainerUri();

    /// <summary>
    /// The upload staging container for the current environment - <c>musicuploads</c>,
    /// <c>musicuploads-dev</c> or <c>musicuploads-local</c>.
    ///
    /// <para>
    /// <b>This lives on a different storage account from the other two.</b> Song media is on a
    /// Premium account, which offers no Queue service, so the queues and this container are on the
    /// Standard general-purpose account instead. Anything copying from here into the media
    /// container is therefore a cross-account copy and needs a source SAS.
    /// </para>
    ///
    /// <para>Null when <see cref="MediaProcessingOptions"/> is not configured.</para>
    /// </summary>
    BlobContainerClient? GetUploadStagingContainer();

    /// <summary>
    /// The names of the containers this environment writes to, for display on the admin pages so an
    /// operator can confirm which environment a destructive job is about to touch.
    /// </summary>
    IReadOnlyList<string> GetConfiguredContainerNames();
}

/// <inheritdoc />
public sealed class BlobContainerFactory : IBlobContainerFactory
{
    private readonly BlobContainerClient _mediaContainer;
    private readonly BlobContainerClient _personaImageContainer;
    private readonly BlobContainerClient _streamingContainer;
    private readonly BlobContainerClient? _uploadStagingContainer;

    public BlobContainerFactory(
        IOptions<AzureStorageOptions> options,
        IOptions<MediaProcessingOptions> mediaProcessingOptions)
    {
        var opts = options?.Value
            ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(opts.StorageAccountConnectionString))
            throw new ArgumentException("StorageAccountConnectionString configuration missing.", nameof(options));
        if (string.IsNullOrWhiteSpace(opts.ContainerName))
            throw new ArgumentException("ContainerName configuration missing.", nameof(options));
        if (string.IsNullOrWhiteSpace(opts.PersonaImageContainerName))
            throw new ArgumentException("PersonaImageContainerName configuration missing.", nameof(options));
        if (string.IsNullOrWhiteSpace(opts.StreamingContainerName))
            throw new ArgumentException("StreamingContainerName configuration missing.", nameof(options));

        // BlobContainerClient is thread-safe and holds a pooled HTTP pipeline, so both clients are
        // built once and shared, matching how AzureStorageService treats its own.
        _mediaContainer = new BlobContainerClient(opts.StorageAccountConnectionString, opts.ContainerName);
        _personaImageContainer = new BlobContainerClient(opts.StorageAccountConnectionString, opts.PersonaImageContainerName);
        _streamingContainer = new BlobContainerClient(opts.StorageAccountConnectionString, opts.StreamingContainerName);

        // Note the different connection string: staging is on the Standard account, not the Premium
        // media one. Missing configuration degrades to null rather than throwing, matching how
        // StorageBackupBlobGateway and CreatorPersonaService handle their optional accounts - a web
        // app that cannot process uploads should still start and serve the existing catalogue.
        var mediaProcessing = mediaProcessingOptions?.Value;
        _uploadStagingContainer = mediaProcessing is { IsConfigured: true }
            ? new BlobContainerClient(mediaProcessing.StorageConnectionString, mediaProcessing.StagingContainerName)
            : null;
    }

    /// <inheritdoc />
    public BlobContainerClient GetMediaContainer() => _mediaContainer;

    /// <inheritdoc />
    public BlobContainerClient GetPersonaImageContainer() => _personaImageContainer;

    /// <inheritdoc />
    public BlobContainerClient GetStreamingContainer() => _streamingContainer;

    /// <inheritdoc />
    public Uri GetStreamingContainerUri() => _streamingContainer.Uri;

    /// <inheritdoc />
    public BlobContainerClient? GetUploadStagingContainer() => _uploadStagingContainer;

    /// <inheritdoc />
    public IReadOnlyList<string> GetConfiguredContainerNames()
        => new[] { _mediaContainer.Name, _personaImageContainer.Name };

    /// <summary>
    /// Creates the streaming container if absent, private.
    ///
    /// <para>
    /// Provisioning creates it too; this covers local development and the case where an environment
    /// is deployed before it is re-provisioned. Private explicitly rather than by default, because
    /// this container's contents are addressable by SAS and its access level is one of the few
    /// settings whose being wrong would be both serious and invisible from the request path.
    /// </para>
    ///
    /// <para>
    /// A failure here is logged, never thrown. Losing streaming is bad; refusing to start the site
    /// over it is worse, and the log line is the intended signal.
    /// </para>
    /// </summary>
    public static async Task EnsureStreamingContainerAsync(
        IBlobContainerFactory factory,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var container = factory.GetStreamingContainer();

        try
        {
            await container.CreateIfNotExistsAsync(
                PublicAccessType.None,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not ensure the streaming container '{Container}'. Encrypted playback may be unavailable.",
                container.Name);
        }
    }
}
