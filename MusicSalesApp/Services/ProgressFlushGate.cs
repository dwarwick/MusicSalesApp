#nullable enable
namespace MusicSalesApp.Services;

/// <summary>
/// Decides when a long-running job should write its counters back to the database.
///
/// <para>
/// Both admin jobs are watched through a page that polls the run row every two seconds, so a purely
/// count-based trigger makes the progress bar depend on throughput: a job that processes fewer items
/// than the interval - or that spends a second on each one, as image encoding does - writes nothing
/// until it finishes, and the bar sits at zero for the whole run. Adding an elapsed-time trigger puts
/// a ceiling on how stale the row can be regardless of how fast or slow the work is.
/// </para>
///
/// <para>
/// The count trigger is kept as well, for the opposite case: a job racing through thousands of cheap
/// items should not be limited to one update per second when it has real progress to report.
/// </para>
/// </summary>
internal sealed class ProgressFlushGate
{
    private readonly int _itemInterval;
    private readonly TimeSpan _timeInterval;
    private readonly TimeProvider _time;

    private long _lastFlushTimestamp;
    private int _lastFlushedCount;

    public ProgressFlushGate(int itemInterval, TimeSpan timeInterval, TimeProvider? time = null)
    {
        _itemInterval = Math.Max(1, itemInterval);
        _timeInterval = timeInterval;
        _time = time ?? TimeProvider.System;
        _lastFlushTimestamp = _time.GetTimestamp();
    }

    /// <summary>
    /// True when the caller should flush, given how many items it has processed so far. Calling this
    /// is what resets the window, so it must be called once per decision point and its answer acted
    /// on - treating it as a pure query would drop updates.
    /// </summary>
    public bool ShouldFlush(int processed)
    {
        var now = _time.GetTimestamp();

        var enoughItems = processed - _lastFlushedCount >= _itemInterval;
        var longEnough = _time.GetElapsedTime(_lastFlushTimestamp, now) >= _timeInterval;

        if (!enoughItems && !longEnough)
        {
            return false;
        }

        _lastFlushedCount = processed;
        _lastFlushTimestamp = now;
        return true;
    }

    /// <summary>
    /// Restarts the window without flushing. Used after a flush that happened for some other reason,
    /// so that an unconditional end-of-phase write does not leave the gate immediately due again.
    /// </summary>
    public void MarkFlushed(int processed)
    {
        _lastFlushedCount = processed;
        _lastFlushTimestamp = _time.GetTimestamp();
    }
}
