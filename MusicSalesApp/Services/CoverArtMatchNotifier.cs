#nullable enable
using Microsoft.AspNetCore.SignalR;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Hubs;

namespace MusicSalesApp.Services;

/// <summary>
/// Pushes cover-art matching progress and its final pairing to the creator waiting on it.
///
/// <para>
/// Shares <see cref="UploadProgressHub"/> and its per-creator group rather than adding a second hub.
/// That hub already solves the two hard parts — a server-originated connection authenticating
/// against an <c>[Authorize]</c> hub by forwarding the caller's cookies, and re-joining the group
/// after a reconnect because membership is per-connection — and a second hub would have to solve
/// both again for nothing.
/// </para>
/// </summary>
public interface ICoverArtMatchNotifier
{
    /// <summary>Sends a matching progress update. Never throws; progress is decoration.</summary>
    Task NotifyProgressAsync(CoverArtMatchProgress progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the finished pairing, which is what releases the upload page's waiting task.
    ///
    /// <para>
    /// Also never throws, even though this one is not decoration. The caller is a callback endpoint
    /// whose non-2xx makes the Function's queue redeliver, and a redelivery is a better answer to a
    /// transient SignalR fault than a 500. If the message is genuinely lost the page's own deadline
    /// expires and it matches on filenames instead.
    /// </para>
    /// </summary>
    Task NotifyResultAsync(CoverArtMatchResult result, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CoverArtMatchNotifier : ICoverArtMatchNotifier
{
    private readonly IHubContext<UploadProgressHub> _hubContext;
    private readonly ILogger<CoverArtMatchNotifier> _logger;

    public CoverArtMatchNotifier(
        IHubContext<UploadProgressHub> hubContext,
        ILogger<CoverArtMatchNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyProgressAsync(
        CoverArtMatchProgress progress,
        CancellationToken cancellationToken = default)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            await _hubContext.Clients
                .Group(SignalRGroups.ForCreator(progress.CreatorId))
                .SendAsync(SignalRMethodNames.ReceiveCoverArtMatchProgress, progress, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not push match progress for batch {BatchId} at step {Step}",
                progress.BatchId,
                progress.Step);
        }
    }

    /// <inheritdoc />
    public async Task NotifyResultAsync(
        CoverArtMatchResult result,
        CancellationToken cancellationToken = default)
    {
        if (result is null)
        {
            return;
        }

        try
        {
            await _hubContext.Clients
                .Group(SignalRGroups.ForCreator(result.CreatorId))
                .SendAsync(SignalRMethodNames.ReceiveCoverArtMatchResult, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not push the cover-art pairing for batch {BatchId}; the upload page will fall "
                + "back to filename matching when its deadline expires",
                result.BatchId);
        }
    }
}
