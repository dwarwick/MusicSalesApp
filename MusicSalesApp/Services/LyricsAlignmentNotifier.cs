#nullable enable
using Microsoft.AspNetCore.SignalR;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Hubs;

namespace MusicSalesApp.Services;

/// <summary>
/// Pushes lyrics-alignment progress to the creator watching it.
/// </summary>
public interface ILyricsAlignmentNotifier
{
    /// <summary>
    /// Sends one progress update to <paramref name="creatorId"/>'s group.
    ///
    /// <para>
    /// Never throws, for the same reason <see cref="IUploadProgressNotifier"/> never does: its
    /// callers are on the critical path for actually storing timings, and a SignalR hiccup must not
    /// fail an alignment that otherwise worked.
    /// </para>
    /// </summary>
    Task NotifyAsync(
        int creatorId,
        LyricsAlignmentProgress progress,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience overload that computes the overall percent from the step.</summary>
    Task NotifyStepAsync(
        int creatorId,
        Guid jobId,
        LyricsAlignmentStep step,
        string? detail = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <inheritdoc cref="ILyricsAlignmentNotifier"/>
///
/// <para>
/// Rides <see cref="UploadProgressHub"/> and its per-creator groups rather than introducing a hub of
/// its own, following the precedent <c>CoverArtMatchNotifier</c> sets: a second hub would have to
/// solve authentication and group membership again, for a payload that goes to exactly the same
/// person over exactly the same connection.
/// </para>
/// </summary>
public sealed class LyricsAlignmentNotifier : ILyricsAlignmentNotifier
{
    private readonly IHubContext<UploadProgressHub> _hubContext;
    private readonly ILogger<LyricsAlignmentNotifier> _logger;

    public LyricsAlignmentNotifier(
        IHubContext<UploadProgressHub> hubContext,
        ILogger<LyricsAlignmentNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(
        int creatorId,
        LyricsAlignmentProgress progress,
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
                .SendAsync(SignalRMethodNames.ReceiveLyricsAlignmentProgress, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose - see the interface docs.
            _logger.LogDebug(
                ex,
                "Could not push lyrics progress for job {JobId} at step {Step}",
                progress.JobId,
                progress.Step);
        }
    }

    /// <inheritdoc />
    public Task NotifyStepAsync(
        int creatorId,
        Guid jobId,
        LyricsAlignmentStep step,
        string? detail = null,
        CancellationToken cancellationToken = default)
        => NotifyAsync(
            creatorId,
            new LyricsAlignmentProgress
            {
                JobId = jobId,
                Step = step,
                OverallPercent = LyricsAlignmentProgressCalculator.ToOverallPercent(step),
                Detail = detail
            },
            cancellationToken);
}
