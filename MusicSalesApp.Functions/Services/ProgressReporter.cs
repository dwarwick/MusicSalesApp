using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Functions.Services;

/// <summary>
/// Reports one job's progress back to the site, throttled so FFmpeg's callback rate does not become
/// the site's request rate.
///
/// <para>
/// One instance tracks one job. The throttle state is guarded by a lock because
/// <c>NotifyOnProgress</c> is delivered through <see cref="Progress{T}"/>, which posts to the
/// thread pool - so two callbacks really can land at once even though only one transcode is running.
/// </para>
/// </summary>
public sealed class ProgressReporter
{
    /// <summary>
    /// <c>NotifyOnProgress</c> fires many times a second. Relaying one-for-one would turn a
    /// four-minute song into hundreds of HTTP posts for a bar that moves in whole percent.
    /// </summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);

    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly Guid _jobId;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _throttleGate = new();

    private DateTimeOffset _lastPostedAt = DateTimeOffset.MinValue;
    private int _lastPostedWholePercent = -1;

    public ProgressReporter(
        IMediaProcessingCallbackClient callbackClient,
        Guid jobId,
        TimeProvider timeProvider = null)
    {
        _callbackClient = callbackClient;
        _jobId = jobId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Announces entry into a step. Always sent - these are the transitions the site persists, and
    /// there are only a handful per song.
    /// </summary>
    public async Task ReportStepAsync(
        AudioProcessingStep step,
        string detail = null,
        CancellationToken cancellationToken = default)
    {
        // Reset the throttle so the first percentage inside a new step is not suppressed by the
        // tail of the previous one.
        lock (_throttleGate)
        {
            _lastPostedAt = DateTimeOffset.MinValue;
            _lastPostedWholePercent = -1;
        }

        await _callbackClient.PostProgressAsync(
            new AudioProcessingProgress
            {
                JobId = _jobId,
                Step = step,
                OverallPercent = AudioProcessingProgressCalculator.ToOverallPercent(step),
                Detail = detail
            },
            cancellationToken);
    }

    /// <summary>
    /// Reports progress within a step, dropping anything too soon after the last post or that would
    /// not move the bar by a whole percent.
    /// </summary>
    public async Task ReportStepPercentAsync(
        AudioProcessingStep step,
        double stepPercent,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(stepPercent, 0d, 100d);
        var overall = AudioProcessingProgressCalculator.ToOverallPercent(step, clamped);
        var wholePercent = (int)Math.Floor(overall);
        var now = _timeProvider.GetUtcNow();

        // Decide and claim the slot in one atomic step, so two concurrent callbacks cannot both
        // see a stale timestamp and each fire a post.
        lock (_throttleGate)
        {
            if (wholePercent == _lastPostedWholePercent || now - _lastPostedAt < MinimumInterval)
            {
                return;
            }

            _lastPostedAt = now;
            _lastPostedWholePercent = wholePercent;
        }

        await _callbackClient.PostProgressAsync(
            new AudioProcessingProgress
            {
                JobId = _jobId,
                Step = step,
                StepPercent = clamped,
                OverallPercent = overall
            },
            cancellationToken);
    }
}
