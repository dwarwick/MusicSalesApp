using Microsoft.JSInterop;
using MusicSalesApp.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class CircuitTeardownTests
{
    [Test]
    public void IsExpected_IsTrue_WhenTheBrowserIsGone()
    {
        Assert.That(CircuitTeardown.IsExpected(new JSDisconnectedException("gone")), Is.True);
    }

    [Test]
    public void IsExpected_IsTrue_WhenAnInteropCallWasCancelledMidFlight()
    {
        // The case every existing catch (JSDisconnectedException) guard misses, and the one that
        // took a circuit down from UploadFilesModel.DisposeAsync on 2026-08-31.
        Assert.That(CircuitTeardown.IsExpected(new TaskCanceledException()), Is.True);
        Assert.That(CircuitTeardown.IsExpected(new OperationCanceledException()), Is.True);
    }

    [Test]
    public void IsExpected_IsFalse_ForAnHttpClientTimeout()
    {
        // HttpClient reports its own timeout as TaskCanceledException wrapping a TimeoutException.
        // Filing that as "the visitor left" would bury an unreachable PayPal or Azure endpoint at
        // Debug, which is the opposite of what anyone wants from an outage.
        var httpTimeout = new TaskCanceledException("timed out", new TimeoutException());

        Assert.That(CircuitTeardown.IsExpected(httpTimeout), Is.False);
    }

    [Test]
    public void IsExpected_IsTrue_WhenTheCircuitsDiScopeIsDisposed()
    {
        // Exactly what ServiceProviderEngineScope throws once the circuit's scope is gone.
        var disposedScope = new ObjectDisposedException("IServiceProvider");

        Assert.That(CircuitTeardown.IsExpected(disposedScope), Is.True);
    }

    [Test]
    public void IsExpected_IsFalse_ForAnyOtherDisposedObject()
    {
        // The point of matching on the object name. A component using a stream it already disposed
        // is a real bug and has to keep reaching the admin.
        Assert.That(CircuitTeardown.IsExpected(new ObjectDisposedException("FileStream")), Is.False);
        Assert.That(CircuitTeardown.IsExpected(new ObjectDisposedException("HttpClient")), Is.False);
    }

    [Test]
    public void IsExpected_IsFalse_ForAnOrdinaryFailure()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CircuitTeardown.IsExpected(new InvalidOperationException("boom")), Is.False);
            Assert.That(CircuitTeardown.IsExpected(new NullReferenceException()), Is.False);
            Assert.That(CircuitTeardown.IsExpected(new JSException("script blew up")), Is.False);
        });
    }

    [Test]
    public void IsExpected_IsFalse_ForNull()
    {
        Assert.That(CircuitTeardown.IsExpected(null), Is.False);
    }

    [Test]
    public void IsExpected_UnwrapsAnAggregate_WhenEveryInnerExceptionIsTeardown()
    {
        var aggregate = new AggregateException(
            new TaskCanceledException(),
            new ObjectDisposedException("IServiceProvider"));

        Assert.That(CircuitTeardown.IsExpected(aggregate), Is.True);
    }

    [Test]
    public void IsExpected_IsFalse_WhenARealFaultTravelsAlongsideTeardown()
    {
        // One genuine fault in the aggregate has to win, or it hides behind the cancellation.
        var aggregate = new AggregateException(
            new TaskCanceledException(),
            new InvalidOperationException("boom"));

        Assert.That(CircuitTeardown.IsExpected(aggregate), Is.False);
    }

    [Test]
    public void IsExpected_IsFalse_ForAnEmptyAggregate()
    {
        Assert.That(CircuitTeardown.IsExpected(new AggregateException()), Is.False);
    }
}
