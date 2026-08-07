using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Two independent things now decide that an upload has failed, and the order matters.
///
/// <para>
/// <c>HandleTranscodePoisonFunction</c> reports the authoritative verdict, from Azure's own
/// exhausted-retries event. <c>SongUploadJobReconciler</c> guesses from a stale timestamp, and that
/// guess is only safe when it arrives <em>after</em> the real answer has had every chance to. If the
/// reconciler can fire first, it reintroduces the bug this arrangement was built to fix: a web app
/// restart silences the progress pings that refresh liveness, and songs that are processing
/// perfectly well get failed, told to their creator as broken, and have their staging deleted out
/// from under the Function still working on them.
/// </para>
///
/// <para>
/// The relationship lives in two files that never reference each other - a TimeSpan in
/// <c>MediaProcessingOptions</c> and two numbers in the Function app's <c>host.json</c> - so nothing
/// but this test would notice it breaking.
/// </para>
/// </summary>
[TestFixture]
public class PoisonHandlerBeatsTheReconcilerTests
{
    /// <summary>host.json: <c>extensions.queues.maxDequeueCount</c>.</summary>
    private const int MaxDequeueCount = 3;

    /// <summary>
    /// host.json: <c>functionTimeout</c>. The Consumption ceiling, not a preference - it cannot be
    /// raised, so this is genuinely the longest a single attempt can run.
    /// </summary>
    private static readonly TimeSpan FunctionTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The longest a message can take to reach the poison queue: every attempt running to the
    /// execution ceiling before being killed.
    /// </summary>
    private static readonly TimeSpan WorstCaseTimeToPoison = FunctionTimeout * MaxDequeueCount;

    private static TimeSpan StalledJobTimeout => new MediaProcessingOptions().StalledJobTimeout;

    [Test]
    public void TheReconcilerCannotBeatThePoisonHandlerToAVerdict()
    {
        Assert.That(
            StalledJobTimeout,
            Is.GreaterThan(WorstCaseTimeToPoison),
            $"StalledJobTimeout ({StalledJobTimeout}) must outlast the worst case time to poison "
            + $"({MaxDequeueCount} attempts x {FunctionTimeout} = {WorstCaseTimeToPoison}), or the "
            + "reconciler can fail a job that was still going to be reported properly.");
    }

    [Test]
    public void TheMarginIsWideEnoughToCoverAWebAppDeploy()
    {
        // The specific event that caused the original incident. Liveness is refreshed only by
        // progress pings, and those swallow their own failures - so for as long as the site is down,
        // every in-flight job looks stalled. The timeout has to outlast a deploy by a wide margin,
        // not merely exceed it.
        var headroom = StalledJobTimeout - WorstCaseTimeToPoison;

        Assert.That(
            headroom,
            Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(30)),
            $"Only {headroom} separates the reconciler from the poison handler. A slow deploy or a "
            + "restart during a large batch would land inside that gap.");
    }

    [Test]
    public void TheReconcilerStillFiresInAHumanTimeframe()
    {
        // It is a backstop, but it is the only thing that ever finishes a job whose message vanished
        // without poisoning. Left unbounded, such a job would show the creator a moving-then-frozen
        // bar indefinitely.
        Assert.That(
            StalledJobTimeout,
            Is.LessThanOrEqualTo(TimeSpan.FromHours(4)),
            "A job whose message vanished should still be resolved the same day it was uploaded.");
    }
}
