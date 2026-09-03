#nullable enable
using Microsoft.JSInterop;

namespace MusicSalesApp.Helpers;

/// <summary>
/// Answers one question: did this exception happen because the circuit went away, rather than
/// because the code is wrong?
///
/// <para>
/// A Blazor Server component can be mid-<c>await</c> when the visitor navigates away or the
/// connection drops. The render finishes, the DI scope is disposed, the browser stops answering -
/// and whatever was still in flight fails. Nothing is broken; there is simply nobody left to serve.
/// The failure is still an exception, though, and the app's convention is
/// <c>catch (Exception ex) { Logger.LogError(...) }</c>, which reaches
/// <see cref="Services.AdminErrorNotificationSink"/> and emails the admin. On 2026-09-02 that
/// produced five emails in one afternoon, none of them actionable.
/// </para>
///
/// <para>
/// Worse in <c>DisposeAsync</c>: an exception thrown there is unhandled, so a teardown that fails
/// because teardown is already underway destroys the circuit it was cleaning up after.
/// </para>
/// </summary>
public static class CircuitTeardown
{
    /// <summary>
    /// The name <c>ServiceProviderEngineScope</c> gives itself when it throws after disposal - it
    /// calls <c>new ObjectDisposedException(nameof(IServiceProvider))</c>. Matching on the name is
    /// what separates "the circuit's scope is gone" from a genuine use-after-dispose of a stream or
    /// an <see cref="HttpClient"/>, which must keep surfacing as an error.
    /// </summary>
    private const string DisposedScopeObjectName = "IServiceProvider";

    /// <summary>
    /// True when <paramref name="exception"/> means the circuit is going away or already gone.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Each arm corresponds to one way the runtime reports "there is no longer
    /// anyone to do this for", and nothing else:
    /// <list type="bullet">
    /// <item><see cref="JSDisconnectedException"/> - the browser is no longer reachable.</item>
    /// <item><see cref="OperationCanceledException"/> whose inner exception is not a
    /// <see cref="TimeoutException"/> - the call was cancelled in flight rather than timing out.
    /// This is
    /// the one the existing <c>catch (JSDisconnectedException)</c> guards miss: a circuit being
    /// torn down cancels a pending interop call rather than reporting it as disconnected, and
    /// <c>JSObjectReference.DisposeAsync</c> surfaces that as <see cref="TaskCanceledException"/>.
    /// </item>
    /// <item><see cref="ObjectDisposedException"/> naming <c>IServiceProvider</c> - the scoped
    /// <c>DbContext</c> behind the call cannot be resolved because the scope is disposed.</item>
    /// </list>
    /// An <see cref="AggregateException"/> qualifies only if <em>every</em> inner exception does.
    /// One real fault travelling alongside a teardown failure is still a real fault.
    /// </remarks>
    public static bool IsExpected(Exception? exception) => exception switch
    {
        null => false,
        JSDisconnectedException => true,
        // A cancelled call is teardown only if nobody timed out. HttpClient reports its own
        // timeout as TaskCanceledException wrapping a TimeoutException, so without this an
        // unreachable PayPal or Azure endpoint would be filed as "the visitor left" and disappear
        // at Debug. That is a real outage hiding behind a routine-looking exception type.
        OperationCanceledException canceled => canceled.InnerException is not TimeoutException,
        ObjectDisposedException disposed => disposed.ObjectName == DisposedScopeObjectName,
        AggregateException aggregate =>
            aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsExpected),
        _ => false
    };
}
