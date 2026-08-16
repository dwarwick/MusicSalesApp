#nullable enable
namespace MusicSalesApp.Services;

/// <summary>
/// How to reach the lyrics-alignment Function app.
///
/// <para>
/// A second Function app, separate from the audio one, because a Function app is pinned to a single
/// language runtime and the alignment pipeline is PyTorch - Demucs for vocal separation, a forced
/// aligner for the timings - which means Python, which means Linux.
/// </para>
///
/// <para>
/// Unlike <see cref="MediaProcessingOptions"/> this describes a <em>callee</em> rather than a
/// storage account, which is why it gets its own class instead of more properties on that one. The
/// objection recorded there - that a second copy of a connection string is worse than no copy - does
/// not apply: nothing here duplicates anything.
/// </para>
///
/// <para>
/// Note the direction of the secret. <c>MediaProcessingApiKey</c> authorises the Function apps to
/// call <em>this</em> app; <see cref="FunctionKey"/> authorises this app to call the Function app.
/// Opposite directions, different trust decisions, so deliberately different secrets.
/// </para>
/// </summary>
public class LyricsFunctionsOptions
{
    /// <summary>
    /// Root of the Function app, e.g. <c>https://streamtunes-lyrics-test.azurewebsites.net</c>.
    /// Empty in an environment where lyrics alignment is not configured, which is not an error - the
    /// feature simply reports itself unavailable and nothing else changes.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The Function app's key, sent as <c>x-functions-key</c> when starting an orchestration.
    ///
    /// <para>
    /// Not the same secret as the <c>code</c> embedded in the management URLs Durable returns - that
    /// one is the durable-task extension's own system key. Nothing here needs it, because those URLs
    /// arrive complete and are stored as-is.
    /// </para>
    /// </summary>
    public string FunctionKey { get; set; } = string.Empty;

    /// <summary>
    /// How long to wait for the starter to answer. Short: it only schedules an orchestration and
    /// returns, so anything slow here is a cold start or an outage rather than work in progress.
    /// The Hangfire job wrapping it retries either way.
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>True when enough is configured to start an orchestration at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(FunctionKey);
}
