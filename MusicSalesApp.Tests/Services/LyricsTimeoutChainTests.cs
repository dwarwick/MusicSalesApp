using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The timing relationships that keep the lyrics reconciler from interfering with runs that are
/// merely slow.
///
/// <para>
/// The sibling of <c>PoisonHandlerBeatsTheReconcilerTests</c>, and worth reading alongside it,
/// because the quantity being bounded is <b>not</b> the same one. There, the danger is a reconciler
/// that guesses "dead" before Azure's poison queue can say so, and the bound is
/// <c>maxDequeueCount x functionTimeout</c>. Substituting that expression here would give a number
/// that is both meaningless and accidentally close enough to look right: a Durable orchestration's
/// trigger message is deleted the moment the run is <em>scheduled</em>, so dequeue count has nothing
/// to do with how long the work takes.
/// </para>
///
/// <para>
/// The quantity that matters here is <b>worst-case time to a reported failure</b>: the orchestration
/// running to its own ceiling, plus the orchestrator's failure-reporting retries, plus the terminal
/// callback's own timeout. The reconciler must not start acting before that has had every chance to
/// arrive.
/// </para>
///
/// <para>
/// The numbers live in three files that never reference each other - a TimeSpan in
/// <c>MediaProcessingOptions</c>, <c>functionTimeout</c> in the Python app's <c>host.json</c>, and
/// the retry policy in its <c>function_app.py</c> - so nothing but this test would notice one of them
/// moving. <b>Changing any of the three means rechecking this.</b>
/// </para>
/// </summary>
[TestFixture]
public class LyricsTimeoutChainTests
{
    /// <summary>
    /// The Python app's <c>host.json</c> <c>functionTimeout</c>. Set explicitly rather than inherited:
    /// Flex Consumption enforces no ceiling of its own, so this is a number somebody chose and can
    /// therefore be reviewed.
    /// </summary>
    private static readonly TimeSpan FunctionTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// The orchestrator's <c>report_failure</c> retry policy: 5 attempts, 30 s first interval,
    /// doubling. The window between the run failing and it giving up on saying so.
    /// </summary>
    private static readonly TimeSpan ReportFailureRetryWindow = TimeSpan.FromMinutes(8);

    /// <summary>
    /// Everything that has to happen between a run going wrong and this application hearing about it.
    /// </summary>
    private static readonly TimeSpan WorstCaseTimeToReportedFailure =
        FunctionTimeout + ReportFailureRetryWindow + LyricsProcessingTimeouts.TerminalCallback;

    private static MediaProcessingOptions Options => new();

    [Test]
    public void TheReconcilerDoesNotStartAskingBeforeAFailureCouldHaveBeenReported()
    {
        Assert.That(
            Options.LyricsStalledJobTimeout,
            Is.GreaterThan(WorstCaseTimeToReportedFailure),
            $"LyricsStalledJobTimeout ({Options.LyricsStalledJobTimeout}) must outlast the worst case "
            + $"time to a reported failure ({WorstCaseTimeToReportedFailure}), or the reconciler starts "
            + "interrogating runs that are about to report for themselves.");
    }

    [Test]
    public void TheReconcilerStillResolvesAnAttemptTheSameDay()
    {
        // It is a backstop, but for an orchestration whose except path never ran it is the only thing
        // that will ever finish the attempt. Left unbounded, the creator watches a frozen bar forever.
        Assert.That(
            Options.LyricsStalledJobTimeout,
            Is.LessThanOrEqualTo(TimeSpan.FromHours(4)));
    }

    [Test]
    public void TheLyricsReconcilerIsAllowedToActSoonerThanTheAudioOne()
    {
        // The inversion is deliberate and is the payoff for recording the orchestration id. The audio
        // reconciler can only ever guess from a stale timestamp, so it is kept slow because a wrong
        // guess destroys healthy work. This one asks Azure, so acting earlier costs nothing - and
        // asking is what lets it tell "failed" from "cancelled" from "finished but the callback was
        // lost", three cases a timestamp cannot distinguish at all.
        Assert.That(
            Options.LyricsStalledJobTimeout,
            Is.LessThan(Options.StalledJobTimeout),
            "If this ever needs to be longer than the audio timeout, the reconciler has stopped "
            + "asking Azure and gone back to guessing - which is a bigger change than a number.");
    }

    [Test]
    public void AnUnanswerableAttemptIsGivenFarLongerThanAStalledOne()
    {
        // "Azure says it failed" is a verdict. "We could not reach Azure" is not, and treating the
        // two alike would fail healthy runs for the duration of an outage - the exact bug the audio
        // pipeline's history is a record of.
        Assert.That(
            Options.LyricsUnreachableJobTimeout,
            Is.GreaterThan(Options.LyricsStalledJobTimeout * 2),
            "An attempt nothing can answer for must be retried across many sweeps before being "
            + "written off, not failed on the first unreachable poll.");
    }

    [Test]
    public void TheSiteFinishesAssemblingBeforeTheFunctionStopsWaiting()
    {
        // If the Function gives up first, its request is abandoned mid-assembly, it retries, and a
        // second assembly runs on top of one still in flight. The same ordering MediaProcessingTimeouts
        // maintains for the audio pair, re-established here because the numbers are different.
        Assert.That(
            LyricsProcessingTimeouts.Assembly,
            Is.LessThan(LyricsProcessingTimeouts.TerminalCallback),
            "Assembly must finish, or fail cleanly, while the Function is still listening.");
    }

    [Test]
    public void TheLyricsAssemblyBudgetIsSmallerThanTheAudioOne()
    {
        // Not arbitrary: lyrics assembly copies two artifacts of a few tens of kilobytes and writes
        // one row. The audio budget is sized for a 150 MB MP3 plus its cover art and renditions.
        // Inheriting that number would leave a stuck copy hanging for minutes for no reason.
        Assert.That(
            LyricsProcessingTimeouts.Assembly,
            Is.LessThan(MediaProcessingTimeouts.Assembly));
    }

    [Test]
    public void AStatusQueryCannotStallTheWholeSweep()
    {
        // The reconciler polls one attempt at a time. A status endpoint that hangs must cost seconds,
        // not the whole sweep - otherwise one unreachable orchestration hides every other stalled
        // attempt behind it.
        Assert.That(
            LyricsProcessingTimeouts.StatusQuery,
            Is.LessThanOrEqualTo(TimeSpan.FromSeconds(30)));
    }
}
