#nullable enable

namespace MusicSalesApp.Services;

/// <summary>
/// One Error/Fatal log event captured for admin notification.
/// </summary>
/// <param name="TimestampUtc">When the event was logged, in UTC.</param>
/// <param name="Level">Serilog level name, e.g. Error or Fatal.</param>
/// <param name="Category">SourceContext of the logger that raised it.</param>
/// <param name="MessageTemplate">
/// The unrendered template. This is the dedupe key, not the rendered text: "payout failed for
/// creator {CreatorId}" must collapse to one signature across every creator, or a run that fails
/// for two hundred creators sends two hundred emails.
/// </param>
/// <param name="RenderedMessage">The message with its properties substituted in.</param>
/// <param name="ExceptionType">Full type name of the exception, when there was one.</param>
/// <param name="ExceptionDetail">
/// Exception.ToString() - the full stack trace, including inner exceptions.
/// </param>
public record AdminErrorNotice(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string MessageTemplate,
    string RenderedMessage,
    string? ExceptionType,
    string? ExceptionDetail)
{
    /// <summary>
    /// Identity for throttling. Deliberately excludes the rendered message and the timestamp so
    /// that the same fault recurring with different parameters is recognised as the same fault.
    /// </summary>
    public string Signature => $"{Level}|{Category}|{MessageTemplate}|{ExceptionType}";
}
