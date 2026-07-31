namespace MusicSalesApp.Tests.Services;

/// <summary>
/// A clock the test drives by hand. Progress flushing is deliberately time-based, and asserting that
/// against the wall clock would mean sleeping for real seconds in every test that touches it.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan interval) => _ticks += interval.Ticks;
}
