using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Models;

/// <summary>
/// One Azure Durable Functions orchestration this application started, and what became of it.
///
/// <para>
/// Deliberately generic: it records orchestration-level facts and knows nothing about lyrics, so the
/// next durable function to arrive uses it unchanged. <see cref="FunctionName"/> is what keeps the
/// rows apart. The domain-specific state - which song, which step, what confidence - lives on the
/// caller's own row, which points here.
/// </para>
///
/// <para>
/// The row exists because a Durable orchestration is fire-and-forget from this side. Starting one
/// over HTTP is the only moment its instance id is ever offered to us, and without the id there is
/// no way to ask Azure how a run is going, no way to stop one, and nothing to correlate a late
/// callback against. Recording it turns the reconciler from something that infers death from a stale
/// timestamp into something that asks.
/// </para>
/// </summary>
public class DurableFunctionTask
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The instance id Durable returned when the orchestration was started. Unique: two rows
    /// claiming the same instance would mean two callers believing they own the same run.
    ///
    /// <para>
    /// Required, because the row is only ever written after a start has succeeded and this value is
    /// the entire reason it exists. Leaving it nullable would also make the unique index a filtered
    /// one, silently permitting any number of id-less rows.
    /// </para>
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string InstanceId { get; set; }

    /// <summary>
    /// Which orchestrator this is an instance of. The only thing distinguishing one durable
    /// function's rows from another's, so it is written from a constant rather than a literal.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string FunctionName { get; set; }

    public DurableTaskStatus Status { get; set; } = DurableTaskStatus.Running;

    /// <summary>
    /// The orchestration input, serialized exactly as it was POSTed.
    ///
    /// <para>
    /// Kept because it is the only complete record of what was actually asked for. A job row holds
    /// the ids, but a support question months later is usually "what did we send it", and
    /// reconstructing that from the domain row assumes the reconstruction code has not changed since.
    /// </para>
    /// </summary>
    public string InputJson { get; set; }

    /// <summary>
    /// Azure's own <c>runtimeStatus</c> string as last observed, verbatim.
    ///
    /// <para>
    /// Kept alongside <see cref="Status"/> rather than only mapped into it, so a value this
    /// application does not recognise stays visible instead of being coerced into the nearest match.
    /// Azure has added runtime statuses before.
    /// </para>
    /// </summary>
    [MaxLength(50)]
    public string RuntimeStatusRaw { get; set; }

    /// <summary>
    /// Why the orchestration ended badly, as Azure reported it. Truncated on write - an
    /// orchestration failure carries a Python traceback, and the useful part is the top of it.
    /// </summary>
    [MaxLength(2000)]
    public string FailureDetail { get; set; }

    /// <summary>
    /// Where to ask Azure how this run is going, exactly as Durable returned it.
    ///
    /// <para>
    /// Stored rather than reconstructed, and the reason is not convenience. These URLs carry
    /// <c>taskHub</c> and <c>connection</c> parameters this application does not otherwise know, and
    /// their <c>code</c> is the <b>durable-task extension's system key</b> - a different secret from
    /// the function key used to start an orchestration, and one nothing here has a copy of. Handing
    /// that payload to the caller is precisely what
    /// <c>create_check_status_response</c> exists to do, so keeping it is the intended use rather
    /// than a workaround.
    /// </para>
    ///
    /// <para>
    /// The tradeoff, stated plainly so nobody has to rediscover it: this is a credential living in a
    /// database column, and rotating the extension's system key silently invalidates every stored
    /// URL. That is survivable because it degrades rather than breaks - a status poll that comes back
    /// unauthorised leaves the reconciler on its timestamp fallback, which is exactly where the audio
    /// pipeline has always been.
    /// </para>
    /// </summary>
    [MaxLength(1000)]
    public string StatusQueryUri { get; set; }

    /// <summary>
    /// Where to stop this run. Same provenance and same caveats as <see cref="StatusQueryUri"/>.
    ///
    /// <para>
    /// The other management URLs Durable returns - send-event, purge-history, restart, suspend,
    /// resume - are deliberately dropped rather than stored. Nothing here uses them, and an unused
    /// credential in a database column is all cost and no benefit.
    /// </para>
    /// </summary>
    [MaxLength(1000)]
    public string TerminateUri { get; set; }

    /// <summary>When the status was last read back from Azure. Null until the first poll.</summary>
    public DateTime? LastPolledAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set once the run reaches any terminal status, including terminated.</summary>
    public DateTime? CompletedAt { get; set; }
}
