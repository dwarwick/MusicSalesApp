#nullable enable

using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace MusicSalesApp.Services;

/// <summary>
/// Serilog sink that forwards Error and Fatal events to the admin notification queue.
///
/// A sink rather than an ILoggerProvider because UseSerilog owns the logging pipeline here, and
/// because the LogEvent carries what the email needs: the exception object (so the mail can show a
/// real stack trace) and the event's own timestamp.
/// </summary>
public class AdminErrorNotificationSink : ILogEventSink
{
    private const string SourceContextProperty = "SourceContext";
    private const string UnknownCategory = "Application";

    private readonly IAdminErrorNotificationQueue _queue;
    private readonly AdminErrorNotificationOptions _options;

    public AdminErrorNotificationSink(
        IAdminErrorNotificationQueue queue,
        IOptions<AdminErrorNotificationOptions> options)
    {
        _queue = queue;
        _options = options.Value;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null || !_options.Enabled)
        {
            return;
        }

        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        var category = ResolveCategory(logEvent);
        if (IsExcluded(category))
        {
            return;
        }

        // Emit runs on the logging thread. Everything here must stay allocation-cheap and
        // non-blocking; the rendering that matters for the email happens on the dispatcher.
        var notice = new AdminErrorNotice(
            logEvent.Timestamp.ToUniversalTime(),
            logEvent.Level.ToString(),
            category,
            logEvent.MessageTemplate.Text,
            SafeRender(logEvent),
            logEvent.Exception?.GetType().FullName,
            logEvent.Exception?.ToString());

        _queue.TryEnqueue(notice);
    }

    private bool IsExcluded(string category)
    {
        foreach (var prefix in _options.ExcludedCategoryPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix)
                && category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveCategory(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue(SourceContextProperty, out var value)
            && value is ScalarValue { Value: string sourceContext }
            && !string.IsNullOrWhiteSpace(sourceContext))
        {
            return sourceContext;
        }

        return UnknownCategory;
    }

    private static string SafeRender(LogEvent logEvent)
    {
        try
        {
            return logEvent.RenderMessage();
        }
        catch (Exception ex)
        {
            // Rendering can throw on a malformed property. Losing the notification because the
            // message would not format is worse than sending it with the raw template.
            return $"{logEvent.MessageTemplate.Text} (message could not be rendered: {ex.Message})";
        }
    }
}
