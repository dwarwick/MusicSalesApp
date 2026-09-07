#nullable enable
using Hangfire;

namespace MusicSalesApp.Services;

/// <summary>
/// Turns pending release notifications and artist messages into push notifications.
/// </summary>
/// <remarks>
/// A job rather than an inline send at creation time. Release notifications are created by another
/// job, and an artist message is created inside a Blazor request - blocking that circuit on an
/// outbound HTTPS call to Firebase or Apple would make Send Thank You feel broken for exactly as
/// long as the slowest of the recipient's devices takes to answer.
/// </remarks>
public interface IArtistPushDispatchService
{
    /// <summary>
    /// Sends every push that is due, and retires any device tokens the platforms reject.
    /// </summary>
    /// <returns>The number of devices successfully delivered to.</returns>
    // Hangfire resolves filters from Job.Method, which for an interface-registered job is
    // this declaration. The same attribute on the implementation is silently ignored.
    //
    // Push is fast - no deliberate spacing, unlike the email jobs - so a run finishes in seconds
    // and the lock rarely does anything. It is here for the case that matters: a large release
    // whose fan-out outlives the five-minute interval, where a second run would re-send to every
    // device the first is still working through. AutomaticRetry(0) pairs with it because
    // DisableConcurrentExecution throws on lock timeout rather than swallowing it.
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    Task<int> DispatchPendingAsync();
}
