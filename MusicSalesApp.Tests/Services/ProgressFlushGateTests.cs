using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The admin pages poll the run row every two seconds, so what this gate decides is exactly what the
/// user sees. The behaviour that matters is the time trigger: without it, progress is a function of
/// throughput, and a job with fewer items than the count interval reports nothing until it is done.
/// </summary>
[TestFixture]
public class ProgressFlushGateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private ManualTimeProvider _clock = null!;
    private ProgressFlushGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new ManualTimeProvider();
        _gate = new ProgressFlushGate(itemInterval: 25, timeInterval: Window, _clock);
    }

    [Test]
    public void WithNeitherTriggerMet_DoesNotFlush()
    {
        Assert.That(_gate.ShouldFlush(1), Is.False);
    }

    [Test]
    public void FlushesOnceTheItemIntervalIsReached()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_gate.ShouldFlush(24), Is.False);
            Assert.That(_gate.ShouldFlush(25), Is.True);
        });
    }

    [Test]
    public void FlushesOnElapsedTimeEvenWhenNothingHasBeenProcessed()
    {
        // A slow item - decoding and re-encoding one image - must still move the bar.
        _clock.Advance(Window);

        Assert.That(_gate.ShouldFlush(0), Is.True);
    }

    [Test]
    public void AFlushRestartsBothWindows()
    {
        _clock.Advance(Window);
        _gate.ShouldFlush(3);

        Assert.Multiple(() =>
        {
            Assert.That(_gate.ShouldFlush(4), Is.False, "the time window restarted");
            Assert.That(_gate.ShouldFlush(27), Is.False, "and the count is measured from 3, not from 0");
            Assert.That(_gate.ShouldFlush(28), Is.True);
        });
    }

    [Test]
    public void KeepsFlushingAtTheCadenceForAsLongAsTheJobRuns()
    {
        var flushes = 0;
        for (var second = 0; second < 5; second++)
        {
            _clock.Advance(Window);
            if (_gate.ShouldFlush(second)) flushes++;
        }

        Assert.That(flushes, Is.EqualTo(5));
    }

    [Test]
    public void AFastJobIsNotThrottledToTheTimeWindow()
    {
        // The count trigger is the reason the time window is not simply the only rule: a job racing
        // through cheap items has real progress to report more often than once a second.
        Assert.Multiple(() =>
        {
            Assert.That(_gate.ShouldFlush(25), Is.True);
            Assert.That(_gate.ShouldFlush(50), Is.True);
            Assert.That(_gate.ShouldFlush(75), Is.True);
        });
    }

    [Test]
    public void MarkFlushed_RestartsTheWindowWithoutReportingAFlush()
    {
        _clock.Advance(Window);
        _gate.MarkFlushed(10);

        Assert.That(_gate.ShouldFlush(11), Is.False);
    }

    [Test]
    public void AnIntervalOfZeroIsTreatedAsEveryItem()
    {
        var gate = new ProgressFlushGate(itemInterval: 0, timeInterval: Window, _clock);

        Assert.That(gate.ShouldFlush(1), Is.True);
    }
}
