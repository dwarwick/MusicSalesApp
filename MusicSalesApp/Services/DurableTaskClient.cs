#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// What Azure said when an orchestration was started.
/// </summary>
public sealed record DurableStartOutcome(bool Started, DurableFunctionTask? Task, string? FailureDetail);

/// <summary>
/// An orchestration's runtime status as last reported by Azure.
/// </summary>
public sealed record DurableStatusOutcome(
    bool Answered,
    DurableTaskStatus Status,
    string? RuntimeStatusRaw,
    string? Output,
    string? FailureDetail);

/// <summary>
/// Starts, inspects and stops Azure Durable Functions orchestrations, recording each one.
///
/// <para>
/// Generic on purpose. It knows about orchestrations and nothing about lyrics, so the next durable
/// function to arrive uses it unchanged; the caller supplies a function name and an input object.
/// </para>
/// </summary>
public interface IDurableTaskClient
{
    /// <summary>Whether a Function app is configured to start orchestrations against at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// POSTs to an HTTP starter and records the resulting instance.
    ///
    /// <para>
    /// This is the <b>only</b> moment an orchestration's instance id is ever offered to us. A
    /// queue-triggered starter has no response channel, which is why this pipeline is invoked over
    /// HTTP: without the id there is no way to ask how a run is going, no way to stop one, and
    /// nothing to correlate a late callback against.
    /// </para>
    ///
    /// <para>
    /// Returns rather than throws on a reachable-but-unhappy Function app, so the caller can decide
    /// whether to retry. Genuinely unexpected failures still propagate.
    /// </para>
    /// </summary>
    Task<DurableStartOutcome> StartAsync<TInput>(
        string functionName,
        string starterRoute,
        TInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks Azure how a recorded orchestration is going, and updates the row with the answer.
    ///
    /// <para>
    /// <see cref="DurableStatusOutcome.Answered"/> is false when Azure could not be reached or the
    /// stored URL was rejected. That is <b>not</b> a verdict about the run - a caller must never
    /// treat "we could not ask" as "it failed", which is the whole distinction this pipeline exists
    /// to preserve.
    /// </para>
    /// </summary>
    Task<DurableStatusOutcome> GetStatusAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>Stops a running orchestration. Best-effort: returns false if it could not be reached.</summary>
    Task<bool> TerminateAsync(int taskId, string reason, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DurableTaskClient : IDurableTaskClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IOptions<LyricsFunctionsOptions> _options;
    private readonly ILogger<DurableTaskClient> _logger;

    public DurableTaskClient(
        HttpClient httpClient,
        IDbContextFactory<AppDbContext> contextFactory,
        IOptions<LyricsFunctionsOptions> options,
        ILogger<DurableTaskClient> logger)
    {
        _httpClient = httpClient;
        _contextFactory = contextFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => _options.Value.IsConfigured;

    /// <inheritdoc />
    public async Task<DurableStartOutcome> StartAsync<TInput>(
        string functionName,
        string starterRoute,
        TInput input,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.IsConfigured)
        {
            return new DurableStartOutcome(
                false, null, "No lyrics Function app is configured for this environment.");
        }

        // Persisted verbatim, so the row records what was actually asked for rather than what a
        // later reconstruction would guess.
        var inputJson = JsonSerializer.Serialize(input, SerializerOptions);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, starterRoute)
            {
                Content = new StringContent(inputJson, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add(LyricsStarterRoutes.FunctionKeyHeaderName, options.FunctionKey);

            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Unreachable or too slow. Reported rather than thrown so the caller's own retry policy
            // decides what happens next.
            _logger.LogWarning(ex, "Could not reach the {FunctionName} starter.", functionName);
            return new DurableStartOutcome(false, null, Truncate(ex.Message, 2000));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cancellationToken);
                _logger.LogWarning(
                    "Starter for {FunctionName} answered {StatusCode}: {Body}",
                    functionName,
                    (int)response.StatusCode,
                    body);

                return new DurableStartOutcome(
                    false, null, Truncate($"{(int)response.StatusCode} {body}", 2000));
            }

            DurableCheckStatusResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<DurableCheckStatusResponse>(
                    SerializerOptions, cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Starter for {FunctionName} answered with unreadable JSON.", functionName);
                return new DurableStartOutcome(false, null, Truncate(ex.Message, 2000));
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
            {
                // A 2xx with no instance id means the orchestration may well be running and we have
                // no handle on it. Reported as a failure because an untrackable run is worse than
                // none - the caller retries, and the orphan ages out of the task hub.
                _logger.LogWarning("Starter for {FunctionName} returned no instance id.", functionName);
                return new DurableStartOutcome(false, null, "Starter returned no instance id.");
            }

            var task = new DurableFunctionTask
            {
                InstanceId = payload.Id,
                FunctionName = functionName,
                Status = DurableTaskStatus.Running,
                InputJson = inputJson,
                StatusQueryUri = Truncate(payload.StatusQueryGetUri, 1000),
                TerminateUri = Truncate(payload.TerminatePostUri, 1000),
                CreatedAt = DateTime.UtcNow
            };

            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            context.DurableFunctionTasks.Add(task);
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Started {FunctionName} orchestration {InstanceId}.", functionName, payload.Id);

            return new DurableStartOutcome(true, task, null);
        }
    }

    /// <inheritdoc />
    public async Task<DurableStatusOutcome> GetStatusAsync(
        int taskId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.DurableFunctionTasks
            .FirstOrDefaultAsync(row => row.Id == taskId, cancellationToken);

        if (task is null)
        {
            return new DurableStatusOutcome(false, DurableTaskStatus.Running, null, null, "No such task.");
        }

        if (string.IsNullOrWhiteSpace(task.StatusQueryUri))
        {
            return new DurableStatusOutcome(
                false, task.Status, task.RuntimeStatusRaw, null, "No status URL was recorded.");
        }

        DurableInstanceStatusResponse? payload;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LyricsProcessingTimeouts.StatusQuery);

            using var response = await _httpClient.GetAsync(task.StatusQueryUri, timeout.Token);

            // 404 means the task hub no longer has it. Purged history, or a hub that was recreated -
            // either way the run is gone and will never call back, so it is an answer rather than a
            // failure to get one.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return await RecordAsync(
                    context,
                    task,
                    DurableTaskStatus.Failed,
                    "NotFound",
                    null,
                    "The orchestration is no longer in the task hub.",
                    cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cancellationToken);
                _logger.LogWarning(
                    "Status query for {InstanceId} answered {StatusCode}: {Body}",
                    task.InstanceId,
                    (int)response.StatusCode,
                    body);

                return new DurableStatusOutcome(false, task.Status, task.RuntimeStatusRaw, null, body);
            }

            payload = await response.Content.ReadFromJsonAsync<DurableInstanceStatusResponse>(
                SerializerOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Could not ask. Emphatically not a verdict - see the interface docs.
            _logger.LogWarning(ex, "Could not read status for orchestration {InstanceId}.", task.InstanceId);
            return new DurableStatusOutcome(false, task.Status, task.RuntimeStatusRaw, null, ex.Message);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.RuntimeStatus))
        {
            return new DurableStatusOutcome(false, task.Status, task.RuntimeStatusRaw, null, "Empty status.");
        }

        return await RecordAsync(
            context,
            task,
            MapRuntimeStatus(payload.RuntimeStatus),
            payload.RuntimeStatus,
            payload.Output?.ToString(),
            null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TerminateAsync(
        int taskId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.DurableFunctionTasks
            .FirstOrDefaultAsync(row => row.Id == taskId, cancellationToken);

        if (task is null || string.IsNullOrWhiteSpace(task.TerminateUri))
        {
            return false;
        }

        // Durable's terminate URL carries a {text} placeholder for the reason.
        var url = task.TerminateUri.Replace("{text}", Uri.EscapeDataString(reason ?? string.Empty), StringComparison.Ordinal);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LyricsProcessingTimeouts.StatusQuery);

            using var response = await _httpClient.PostAsync(url, content: null, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Terminating orchestration {InstanceId} answered {StatusCode}.",
                    task.InstanceId,
                    (int)response.StatusCode);
                return false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not terminate orchestration {InstanceId}.", task.InstanceId);
            return false;
        }

        // Recorded immediately rather than waiting for the next poll to observe it, so the creator's
        // dialog reflects what they just asked for. Azure agrees a moment later.
        task.Status = DurableTaskStatus.Terminated;
        task.RuntimeStatusRaw = "Terminated";
        task.CompletedAt = DateTime.UtcNow;
        task.LastPolledAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }

    private async Task<DurableStatusOutcome> RecordAsync(
        AppDbContext context,
        DurableFunctionTask task,
        DurableTaskStatus status,
        string? runtimeStatusRaw,
        string? output,
        string? failureDetail,
        CancellationToken cancellationToken)
    {
        task.Status = status;
        task.RuntimeStatusRaw = Truncate(runtimeStatusRaw, 50);
        task.LastPolledAt = DateTime.UtcNow;

        if (failureDetail is not null)
        {
            task.FailureDetail = Truncate(failureDetail, 2000);
        }

        if (status is not DurableTaskStatus.Running && task.CompletedAt is null)
        {
            task.CompletedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);

        return new DurableStatusOutcome(true, status, task.RuntimeStatusRaw, output, task.FailureDetail);
    }

    /// <summary>
    /// Maps Azure's runtime status onto ours.
    ///
    /// <para>
    /// Everything not recognised maps to <see cref="DurableTaskStatus.Running"/> rather than to
    /// Failed, deliberately. An unknown status is ignorance, not evidence of a fault, and treating it
    /// as failure would let a status Azure adds later start failing healthy runs. The raw string is
    /// stored alongside so the unknown value is visible.
    /// </para>
    /// </summary>
    internal static DurableTaskStatus MapRuntimeStatus(string runtimeStatus) => runtimeStatus switch
    {
        "Completed" => DurableTaskStatus.Completed,
        "Failed" => DurableTaskStatus.Failed,
        "Terminated" => DurableTaskStatus.Terminated,
        _ => DurableTaskStatus.Running
    };

    private static string? Truncate(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];

    private static async Task<string> SafeReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length > 500 ? body[..500] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>The 202 payload from <c>create_check_status_response</c>.</summary>
    private sealed class DurableCheckStatusResponse
    {
        public string? Id { get; set; }

        public string? StatusQueryGetUri { get; set; }

        public string? TerminatePostUri { get; set; }
    }

    /// <summary>The subset of the status payload this application reads.</summary>
    private sealed class DurableInstanceStatusResponse
    {
        public string? RuntimeStatus { get; set; }

        [JsonPropertyName("output")]
        public JsonElement? Output { get; set; }
    }
}
