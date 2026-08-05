#nullable enable
using Microsoft.AspNetCore.SignalR;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Hubs;

namespace MusicSalesApp.Services;

/// <summary>
/// Pushes upload progress to the creator watching it.
/// </summary>
public interface IUploadProgressNotifier
{
    /// <summary>
    /// Sends one progress update to <paramref name="creatorId"/>'s group.
    ///
    /// <para>
    /// Never throws. Progress is decoration on top of a pipeline that must succeed without it, and
    /// the callers - the callback endpoint and the assembly service - are on the critical path for
    /// actually publishing a song. A SignalR hiccup must not fail an upload that otherwise worked.
    /// </para>
    /// </summary>
    Task NotifyAsync(int creatorId, AudioProcessingProgress progress, CancellationToken cancellationToken = default);

    /// <summary>Convenience overload that computes the overall percent from the step.</summary>
    Task NotifyStepAsync(
        int creatorId,
        Guid jobId,
        AudioProcessingStep step,
        string? detail = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class UploadProgressNotifier : IUploadProgressNotifier
{
    private readonly IHubContext<UploadProgressHub> _hubContext;
    private readonly ILogger<UploadProgressNotifier> _logger;

    public UploadProgressNotifier(
        IHubContext<UploadProgressHub> hubContext,
        ILogger<UploadProgressNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(
        int creatorId,
        AudioProcessingProgress progress,
        CancellationToken cancellationToken = default)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            await _hubContext.Clients
                .Group(SignalRGroups.ForCreator(creatorId))
                .SendAsync(SignalRMethodNames.ReceiveUploadProgress, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose - see the interface docs.
            _logger.LogDebug(
                ex,
                "Could not push upload progress for job {JobId} at step {Step}",
                progress.JobId,
                progress.Step);
        }
    }

    /// <inheritdoc />
    public Task NotifyStepAsync(
        int creatorId,
        Guid jobId,
        AudioProcessingStep step,
        string? detail = null,
        CancellationToken cancellationToken = default)
        => NotifyAsync(
            creatorId,
            new AudioProcessingProgress
            {
                JobId = jobId,
                Step = step,
                OverallPercent = AudioProcessingProgressCalculator.ToOverallPercent(step),
                Detail = detail
            },
            cancellationToken);
}
