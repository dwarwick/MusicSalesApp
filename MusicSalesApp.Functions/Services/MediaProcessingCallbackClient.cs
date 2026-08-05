using System.Net.Http.Json;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Functions.Services;

/// <summary>
/// The only way this app talks back to StreamTunes.
///
/// <para>
/// The site and its SQL Server run on shared hosting that is not reachable from Azure, so nothing
/// here writes to the database. Every result travels over HTTPS to <c>api/media-processing/*</c>,
/// authorised by a shared secret in the <c>X-Media-Processing-Key</c> header.
/// </para>
/// </summary>
public interface IMediaProcessingCallbackClient
{
    /// <summary>
    /// Posts the terminal result of a transcode.
    ///
    /// <para>
    /// <b>Throws on failure, deliberately.</b> The exception escapes the queue trigger, which
    /// returns the message for another attempt — that is what stops a song being lost because the
    /// site happened to be restarting.
    /// </para>
    /// </summary>
    Task PostTranscodeResultAsync(AudioTranscodeResult result, CancellationToken cancellationToken = default);

    /// <summary>Posts the result of a decode-only probe. Throws on failure, as above.</summary>
    Task PostProbeResultAsync(AudioProbeResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a progress update.
    ///
    /// <para>
    /// <b>Never throws.</b> The exact opposite of the two above: progress is decoration, and letting
    /// a failed cosmetic update fail the message would re-run an entire transcode for nothing.
    /// </para>
    /// </summary>
    Task PostProgressAsync(AudioProcessingProgress progress, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MediaProcessingCallbackClient : IMediaProcessingCallbackClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MediaProcessingCallbackClient> _logger;

    public MediaProcessingCallbackClient(
        HttpClient httpClient,
        ILogger<MediaProcessingCallbackClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PostTranscodeResultAsync(
        AudioTranscodeResult result,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            MediaProcessingRoutes.Complete,
            result,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"Completion callback for job {result.JobId} failed: {(int)response.StatusCode} {body}");
        }

        _logger.LogInformation(
            "Reported {Outcome} for job {JobId}",
            result.Outcome,
            result.JobId);
    }

    /// <inheritdoc />
    public async Task PostProbeResultAsync(
        AudioProbeResult result,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            MediaProcessingRoutes.ControllerRoute + "/probe-result",
            result,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"Probe callback for song {result.SongMetadataId} failed: {(int)response.StatusCode} {body}");
        }
    }

    /// <inheritdoc />
    public async Task PostProgressAsync(
        AudioProcessingProgress progress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // The client's own timeout is sized for the terminal callback, which waits on the site's
            // whole assembly. Applying that to a cosmetic ping would let an unresponsive site cost
            // minutes per step out of a 10-minute invocation, so progress gets its own short leash.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MediaProcessingTimeouts.ProgressCallback);

            await _httpClient.PostAsJsonAsync(MediaProcessingRoutes.Progress, progress, timeout.Token);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose - see the interface docs.
            _logger.LogDebug(
                ex,
                "Could not post progress for job {JobId} at step {Step}",
                progress.JobId,
                progress.Step);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length <= 500 ? body : body[..500];
        }
        catch
        {
            return string.Empty;
        }
    }
}
