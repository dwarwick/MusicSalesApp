namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// The lifecycle of one Durable Functions orchestration instance, as this application tracks it.
///
/// <para>
/// Deliberately coarser than Azure's own <c>runtimeStatus</c>, which distinguishes states this
/// application has no use for (<c>Pending</c> versus <c>Running</c>) — but <b>not</b> coarser in the
/// one place it matters: <see cref="Terminated"/> is kept apart from <see cref="Failed"/>. A run
/// somebody cancelled is not a run that broke, and folding the two together would show a creator a
/// failure message for something they asked for.
/// </para>
///
/// <para>
/// Azure's raw string is retained alongside this on the row, so a status this enum does not
/// recognise stays visible rather than being coerced into the nearest match.
/// </para>
/// </summary>
public enum DurableTaskStatus
{
    /// <summary>Scheduled or executing. Everything that is not yet an answer.</summary>
    Running = 0,

    /// <summary>The orchestrator returned normally.</summary>
    Completed = 1,

    /// <summary>The orchestrator threw, or never started.</summary>
    Failed = 2,

    /// <summary>Stopped on request, via the terminate endpoint.</summary>
    Terminated = 3
}
