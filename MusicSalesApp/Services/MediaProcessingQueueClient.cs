#nullable enable
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Services;

/// <summary>
/// Puts audio-processing work on the queues the Azure Function polls.
/// </summary>
public interface IMediaProcessingQueueClient
{
    /// <summary>True when a queue connection is configured. False disables the whole pipeline.</summary>
    bool IsConfigured { get; }

    /// <summary>Asks the Function to transcode one staged upload.</summary>
    Task EnqueueTranscodeAsync(AudioTranscodeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Asks the Function to decode-probe already-stored playback blobs.</summary>
    Task EnqueueProbesAsync(IEnumerable<AudioProbeRequest> requests, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MediaProcessingQueueClient : IMediaProcessingQueueClient
{
    /// <summary>
    /// The Functions queue trigger expects base64. The extension's default binding decodes it, and
    /// a raw-string message would be dequeued as unreadable bytes rather than failing loudly, so
    /// this is set explicitly on both queues rather than left to the SDK default.
    /// </summary>
    private const QueueMessageEncoding MessageEncoding = QueueMessageEncoding.Base64;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly QueueClient? _transcodeQueue;
    private readonly QueueClient? _probeQueue;
    private readonly ILogger<MediaProcessingQueueClient> _logger;

    public MediaProcessingQueueClient(
        IOptions<MediaProcessingOptions> options,
        ILogger<MediaProcessingQueueClient> logger)
    {
        _logger = logger;
        var opts = options?.Value;

        if (opts is null || string.IsNullOrWhiteSpace(opts.StorageConnectionString))
        {
            // Degrade rather than throw at DI time, matching StorageBackupBlobGateway: an
            // environment with no processing configured should still start and serve the catalogue.
            _logger.LogWarning(
                "AzureLowSpeed:StorageAccountConnectionString is not configured. Audio processing is disabled.");
            return;
        }

        var queueOptions = new QueueClientOptions { MessageEncoding = MessageEncoding };
        _transcodeQueue = new QueueClient(opts.StorageConnectionString, opts.TranscodeQueueName, queueOptions);
        _probeQueue = new QueueClient(opts.StorageConnectionString, opts.ProbeQueueName, queueOptions);
    }

    /// <inheritdoc />
    public bool IsConfigured => _transcodeQueue is not null && _probeQueue is not null;

    /// <inheritdoc />
    public async Task EnqueueTranscodeAsync(
        AudioTranscodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_transcodeQueue is null)
        {
            throw new InvalidOperationException(
                "Audio processing is not configured; AzureLowSpeed:StorageAccountConnectionString is missing.");
        }

        await _transcodeQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await _transcodeQueue.SendMessageAsync(
            JsonSerializer.Serialize(request, SerializerOptions),
            cancellationToken);

        _logger.LogInformation(
            "Enqueued transcode for job {JobId} from {SourceBlobPath}",
            request.JobId,
            request.SourceBlobPath);
    }

    /// <inheritdoc />
    public async Task EnqueueProbesAsync(
        IEnumerable<AudioProbeRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (_probeQueue is null)
        {
            throw new InvalidOperationException(
                "Audio processing is not configured; AzureLowSpeed:StorageAccountConnectionString is missing.");
        }

        var materialized = requests as IReadOnlyList<AudioProbeRequest> ?? requests.ToList();
        if (materialized.Count == 0)
        {
            return;
        }

        await _probeQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        // Sent one at a time on purpose. Azure Queues has no batch-send API, and a partial failure
        // partway through a run is recoverable: the audit tracks per-song items, so a song whose
        // message never landed is left pending and swept up by the reconciler rather than silently
        // counted as processed.
        foreach (var request in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _probeQueue.SendMessageAsync(
                JsonSerializer.Serialize(request, SerializerOptions),
                cancellationToken);
        }

        _logger.LogInformation("Enqueued {Count} audio probe(s)", materialized.Count);
    }
}
