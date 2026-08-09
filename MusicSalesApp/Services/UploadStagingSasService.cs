#nullable enable
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>One blob the browser has been authorised to write, and nothing more.</summary>
/// <param name="BlobPath">Where it lands in staging. Server-derived; never came from a caller.</param>
/// <param name="SasUri">The full write URL, including the token.</param>
/// <param name="ContentType">What the browser should send, derived from the extension.</param>
/// <param name="ExpiresOn">When the token stops working, so the caller can renew before it does.</param>
public sealed record StagedUploadTarget(
    string BlobPath,
    Uri SasUri,
    string ContentType,
    DateTimeOffset ExpiresOn);

/// <summary>
/// Mints the short-lived write tokens that let a creator's browser PUT straight into staging.
///
/// <para>
/// <b>A service, not a controller, and that is the security argument rather than a style
/// preference.</b> Blazor Server component code already runs on the server (see AGENTS.md on
/// preferring direct service calls), so the circuit calls this and hands the resulting URL to
/// JavaScript. There is therefore no endpoint, no caller, and <b>no path parameter</b> — the usual
/// hazard of "what stops someone requesting a token for an arbitrary blob?" cannot arise, because
/// nothing outside this class ever names a path.
/// </para>
///
/// <para>
/// Every token is scoped to <b>one blob</b> with <b>create+write only</b>. Not read: a leaked token
/// cannot even read back what it wrote, let alone another creator's upload. Not delete, not list.
/// Container scope was rejected outright — it would grant write over every in-flight job folder and
/// the whole match-batch prefix in the environment.
/// </para>
/// </summary>
public interface IUploadStagingSasService
{
    /// <summary>False when staging is unconfigured, so callers can fall back rather than throw.</summary>
    bool IsAvailable { get; }

    /// <summary>A token for one song's source audio, at <c>{guid}/source{ext}</c>.</summary>
    Task<StagedUploadTarget> CreateAudioTargetAsync(
        Guid mediaGuid,
        string audioExtension,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A token for one candidate cover image, at <c>batch/{batchId}/{index}{ext}</c>.
    ///
    /// <para>
    /// Images cannot go to a song's folder: which image belongs to which song is only known after
    /// matching, which is after the image has been uploaded. They land in the batch folder and are
    /// copied into place server-side once the pairing is known.
    /// </para>
    /// </summary>
    Task<StagedUploadTarget> CreateMatchImageTargetAsync(
        Guid batchId,
        int index,
        string imageExtension,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class UploadStagingSasService : IUploadStagingSasService
{
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IOptions<MediaProcessingOptions> _options;
    private readonly ILogger<UploadStagingSasService> _logger;

    /// <summary>
    /// Guards the one-time container check. The browser's PUT cannot create a missing container the
    /// way every server-side writer does, and would see an opaque 404 instead.
    /// </summary>
    private readonly SemaphoreSlim _containerGate = new(1, 1);
    private bool _containerChecked;

    private static readonly TimeSpan ContainerCheckTimeout = TimeSpan.FromSeconds(5);

    public UploadStagingSasService(
        IBlobContainerFactory containerFactory,
        IOptions<MediaProcessingOptions> options,
        ILogger<UploadStagingSasService> logger)
    {
        _containerFactory = containerFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _containerFactory.GetUploadStagingContainer() is not null;

    /// <inheritdoc />
    public Task<StagedUploadTarget> CreateAudioTargetAsync(
        Guid mediaGuid,
        string audioExtension,
        CancellationToken cancellationToken = default)
    {
        if (mediaGuid == Guid.Empty)
        {
            throw new ArgumentException("A media GUID is required.", nameof(mediaGuid));
        }

        var extension = NormalizeExtension(audioExtension);
        if (!MusicFileExtensions.ValidAudioExtensions.Contains(extension))
        {
            throw new InvalidDataException($"'{audioExtension}' is not a supported audio extension.");
        }

        return CreateTargetAsync(
            MediaProcessingStagingPaths.Source(mediaGuid, extension),
            MusicFileExtensions.GetAudioContentType(extension) ?? "application/octet-stream",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<StagedUploadTarget> CreateMatchImageTargetAsync(
        Guid batchId,
        int index,
        string imageExtension,
        CancellationToken cancellationToken = default)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("A batch id is required.", nameof(batchId));
        }

        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index must not be negative.");
        }

        var extension = NormalizeExtension(imageExtension);
        if (!MusicFileExtensions.ValidCoverArtExtensions.Contains(extension))
        {
            throw new InvalidDataException($"'{imageExtension}' is not a supported cover-art extension.");
        }

        return CreateTargetAsync(
            MediaProcessingStagingPaths.MatchBatchImage(batchId, index, extension),
            MusicFileExtensions.GetCoverArtContentType(extension) ?? "application/octet-stream",
            cancellationToken);
    }

    private async Task<StagedUploadTarget> CreateTargetAsync(
        string blobPath,
        string contentType,
        CancellationToken cancellationToken)
    {
        var staging = _containerFactory.GetUploadStagingContainer()
            ?? throw new InvalidOperationException("Upload staging is not configured.");

        await EnsureContainerExistsAsync(staging, cancellationToken);

        var blob = staging.GetBlobClient(blobPath);
        if (!blob.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                $"Cannot generate an upload SAS for '{blobPath}'. Direct uploads require a key-based "
                + "AzureLowSpeed:StorageAccountConnectionString.");
        }

        var options = _options.Value;
        var expiresOn = DateTimeOffset.UtcNow.Add(options.StagingUploadSasLifetime);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = staging.Name,
            BlobName = blobPath,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.Subtract(options.StagingUploadSasClockSkew),
            ExpiresOn = expiresOn,
            ContentType = contentType
        };

        // Create for the single-PUT path, Write for Put Block / Put Block List. Deliberately nothing
        // else: no Read, so a leaked token cannot read back even its own blob; no Delete; no List.
        builder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        return new StagedUploadTarget(blobPath, blob.GenerateSasUri(builder), contentType, expiresOn);
    }

    /// <remarks>
    /// Once per process. Provisioning creates the container too, so this is the belt to that braces —
    /// it matters because a browser PUT into a missing container fails with nothing on our side to
    /// log, which is the hardest class of failure to diagnose in this whole path.
    /// </remarks>
    private async Task EnsureContainerExistsAsync(
        Azure.Storage.Blobs.BlobContainerClient staging,
        CancellationToken cancellationToken)
    {
        if (_containerChecked)
        {
            return;
        }

        await _containerGate.WaitAsync(cancellationToken);
        try
        {
            if (_containerChecked)
            {
                return;
            }

            // Bounded, because this sits in front of every token mint. An unreachable storage
            // endpoint must cost one short wait, not stall the creator's upload behind the SDK's
            // full retry-and-timeout budget.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ContainerCheckTimeout);

            await staging.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            // Not fatal on its own: provisioning creates the container too, and a genuinely missing
            // one surfaces on the upload with a message that names it.
            _logger.LogWarning(ex, "Could not confirm the staging container '{Container}' exists", staging.Name);
        }
        finally
        {
            // Set whatever happened, so this is attempted once per process rather than once per
            // token. Retrying a failing network call on every mint turned a misconfigured
            // environment from slightly wrong into unusably slow.
            _containerChecked = true;
            _containerGate.Release();
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}
