#nullable enable

using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Services;

/// <summary>
/// Drains the admin error queue and emails what survives throttling.
///
/// Runs as a hosted service so that mail delivery never happens on the thread that logged the
/// error. Every failure in here is logged at Warning: logging at Error would feed the sink that
/// feeds this service.
/// </summary>
public class AdminErrorNotificationDispatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private const int MaxSubjectLength = 150;

    private readonly IAdminErrorNotificationQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AdminErrorNotificationOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminErrorNotificationDispatcher> _logger;
    private readonly AdminErrorNotificationThrottle _throttle;

    public AdminErrorNotificationDispatcher(
        IAdminErrorNotificationQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<AdminErrorNotificationOptions> options,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<AdminErrorNotificationDispatcher> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _configuration = configuration;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;
        _throttle = new AdminErrorNotificationThrottle(_options.ThrottleWindow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Admin error notifications are disabled");
            return;
        }

        _logger.LogInformation(
            "Admin error notifications enabled; sending to {Recipient} with a {Window} throttle window",
            ResolveRecipient(),
            _options.ThrottleWindow);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitForWorkAsync(stoppingToken);
                await DrainAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Admin error notification dispatch failed");
            }
        }

        // Best effort on shutdown: report anything already counted rather than losing it.
        try
        {
            await DrainAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin error notification final drain failed");
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        // Wake on either a new notice or the poll interval, so a suppressed-repeat summary still
        // goes out once its window elapses even if nothing else is logged.
        //
        // One awaited wait under a linked token, rather than racing two tasks and abandoning the
        // loser. An abandoned WaitToReadAsync stays queued on the channel's waiter list holding a
        // registration on a token that only fires at shutdown, and the quiet case - no errors
        // logged, which is the normal case - would accumulate one of those per minute for the
        // life of the process.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PollInterval);

        try
        {
            await _queue.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The poll interval elapsed rather than the app shutting down. The filter is
            // load-bearing: without it every ordinary tick reads as a shutdown and the loop exits,
            // leaving the dispatcher silent after its first email.
        }
    }

    private async Task DrainAsync()
    {
        while (_queue.Reader.TryRead(out var notice))
        {
            var notification = _throttle.Admit(notice, _timeProvider.GetUtcNow());
            if (notification != null)
            {
                await SendAsync(notification);
            }
        }

        foreach (var followUp in _throttle.CollectDueFollowUps(_timeProvider.GetUtcNow()))
        {
            await SendAsync(followUp);
        }
    }

    private async Task SendAsync(AdminErrorNotification notification)
    {
        var recipient = ResolveRecipient();
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return;
        }

        // Claimed here rather than inside BuildBody: the count is destructive to read, and a
        // report that is built but never delivered would take the evidence with it.
        var dropped = _queue.ExchangeDroppedCount();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

            // SendEmailWithResultAsync, not SendEmailAsync: EmailService catches every delivery
            // failure internally and returns false, so the catch below never fires for the failure
            // that actually matters. Silently failing to deliver an error alert is precisely the
            // blindness this pipeline exists to end.
            var result = await emailService.SendEmailWithResultAsync(
                recipient,
                BuildSubject(notification),
                BuildBody(notification, dropped));

            if (!result.Success)
            {
                _queue.RestoreDroppedCount(dropped);

                // Cleared before anything else can throw: the catch below also restores, and
                // restoring twice would permanently inflate the count.
                dropped = 0;

                _throttle.MarkDeliveryFailed(notification);
                _logger.LogWarning(
                    "Could not deliver the admin error notification for {Signature}: {ErrorType} {ErrorMessage}",
                    notification.Notice.Signature,
                    result.ErrorType,
                    result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _queue.RestoreDroppedCount(dropped);
            _throttle.MarkDeliveryFailed(notification);
            _logger.LogWarning(
                ex,
                "Could not email the admin error notification for {Signature}",
                notification.Notice.Signature);
        }
    }

    private string ResolveRecipient()
    {
        if (!string.IsNullOrWhiteSpace(_options.ToEmail))
        {
            return _options.ToEmail.Trim();
        }

        var configured = _configuration["EmailSettings:AdminEmail"];
        return string.IsNullOrWhiteSpace(configured)
            ? AdminErrorNotificationOptions.DefaultToEmail
            : configured.Trim();
    }

    private string BuildSubject(AdminErrorNotification notification)
    {
        var notice = notification.Notice;
        var scope = notification.IsFollowUp
            ? $"{notification.SuppressedCount} more"
            : notice.Level;

        var subject = new StringBuilder()
            .Append("[StreamTunes ")
            .Append(_environment.EnvironmentName)
            .Append("] ")
            .Append(scope)
            .Append(": ")
            .Append(ShortCategory(notice.Category))
            .Append(" - ")
            .Append(FirstLine(notice.RenderedMessage))
            .ToString();

        return subject.Length <= MaxSubjectLength
            ? subject
            : subject[..MaxSubjectLength];
    }

    private string BuildBody(AdminErrorNotification notification, int dropped)
    {
        var notice = notification.Notice;

        var body = new StringBuilder();
        body.Append("<h2>").Append(Encode(notice.Level)).Append(" in StreamTunes ")
            .Append(Encode(_environment.EnvironmentName)).Append("</h2>");

        if (notification.IsFollowUp)
        {
            body.Append("<p><strong>")
                .Append(notification.SuppressedCount)
                .Append(" further occurrence(s)</strong> of an error already reported. ")
                .Append("Details below are from the most recent one.</p>");
        }
        else if (notification.SuppressedCount > 0)
        {
            body.Append("<p><strong>")
                .Append(notification.SuppressedCount)
                .Append(" occurrence(s)</strong> were suppressed since the last email for this error.</p>");
        }

        body.Append("<table cellpadding=\"4\" style=\"border-collapse:collapse\">");
        AppendRow(body, "Time (UTC)", notice.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'"));
        AppendRow(body, "Time (server local)", notice.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendRow(body, "Level", notice.Level);
        AppendRow(body, "Source", notice.Category);
        AppendRow(body, "Machine", Environment.MachineName);
        if (!string.IsNullOrWhiteSpace(notice.ExceptionType))
        {
            AppendRow(body, "Exception", notice.ExceptionType);
        }

        body.Append("</table>");

        body.Append("<h3>Message</h3><pre style=\"white-space:pre-wrap\">")
            .Append(Encode(notice.RenderedMessage))
            .Append("</pre>");

        if (!string.IsNullOrWhiteSpace(notice.ExceptionDetail))
        {
            // Exception.ToString(), so this is the full stack trace including inner exceptions.
            body.Append("<h3>Stack trace</h3><pre style=\"white-space:pre-wrap\">")
                .Append(Encode(notice.ExceptionDetail))
                .Append("</pre>");
        }
        else
        {
            body.Append("<h3>Stack trace</h3><p>No exception was attached to this log entry.</p>");
        }

        if (dropped > 0)
        {
            body.Append("<p><em>")
                .Append(dropped)
                .Append(" further error(s) were dropped because the notification queue was full.</em></p>");
        }

        return body.ToString();
    }

    private static void AppendRow(StringBuilder body, string label, string value)
    {
        body.Append("<tr><td><strong>")
            .Append(Encode(label))
            .Append("</strong></td><td>")
            .Append(Encode(value))
            .Append("</td></tr>");
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string ShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1
            ? category[(lastDot + 1)..]
            : category;
    }

    private static string FirstLine(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var breakIndex = message.IndexOfAny(['\r', '\n']);
        return breakIndex < 0 ? message : message[..breakIndex];
    }
}
