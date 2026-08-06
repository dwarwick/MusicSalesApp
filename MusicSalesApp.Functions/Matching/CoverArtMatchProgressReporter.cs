using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Functions.Matching;

/// <summary>
/// Reports one match batch's progress back to the site.
///
/// <para>
/// Deliberately not <c>ProgressReporter</c>, whose signature is bound to a job GUID and an
/// <c>AudioProcessingStep</c>. It is also much simpler: that one throttles on a two-second interval
/// because FFmpeg's callback fires many times a second, whereas this posts once per step plus once
/// per image — at most a few dozen per batch. A whole-percent gate is enough to stop a large batch
/// posting twice for the same visible position.
/// </para>
/// </summary>
public sealed class CoverArtMatchProgressReporter
{
    private readonly IMediaProcessingCallbackClient _callbackClient;
    private readonly Guid _batchId;
    private readonly int _creatorId;
    private readonly int _imagesTotal;
    private readonly Lock _gate = new();

    private int _lastPostedWholePercent = -1;

    public CoverArtMatchProgressReporter(
        IMediaProcessingCallbackClient callbackClient,
        Guid batchId,
        int creatorId,
        int imagesTotal)
    {
        _callbackClient = callbackClient;
        _batchId = batchId;
        _creatorId = creatorId;
        _imagesTotal = imagesTotal;
    }

    /// <summary>Announces entry into a step. Always sent — there are only a handful per batch.</summary>
    public async Task ReportStepAsync(CoverArtMatchStep step, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _lastPostedWholePercent = -1;
        }

        await _callbackClient.PostMatchProgressAsync(
            new CoverArtMatchProgress
            {
                BatchId = _batchId,
                CreatorId = _creatorId,
                Step = step,
                ImagesTotal = _imagesTotal,
                OverallPercent = CoverArtMatchProgressCalculator.ToOverallPercent(step)
            },
            cancellationToken);
    }

    /// <summary>
    /// Reports how many images have had their text read.
    ///
    /// <para>
    /// The vision calls run concurrently, so these arrive from several threads at once and out of
    /// order. The gate claims a slot and drops anything that would not move the bar, which also
    /// discards a late-arriving lower count.
    /// </para>
    /// </summary>
    public async Task ReportImagesReadAsync(int imagesRead, CancellationToken cancellationToken = default)
    {
        if (_imagesTotal <= 0)
            return;

        var stepPercent = Math.Clamp(imagesRead * 100d / _imagesTotal, 0d, 100d);
        var overall = CoverArtMatchProgressCalculator.ToOverallPercent(
            CoverArtMatchStep.ReadingText, stepPercent);
        var wholePercent = (int)Math.Floor(overall);

        lock (_gate)
        {
            if (wholePercent <= _lastPostedWholePercent)
                return;

            _lastPostedWholePercent = wholePercent;
        }

        await _callbackClient.PostMatchProgressAsync(
            new CoverArtMatchProgress
            {
                BatchId = _batchId,
                CreatorId = _creatorId,
                Step = CoverArtMatchStep.ReadingText,
                ImagesProcessed = imagesRead,
                ImagesTotal = _imagesTotal,
                OverallPercent = overall
            },
            cancellationToken);
    }
}
