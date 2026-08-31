#nullable enable

using System.Net;
using System.Net.Mail;

namespace MusicSalesApp.Services;

/// <summary>
/// Emails the admin when the application fails to start or dies out of <c>app.Run()</c>.
///
/// <para>
/// Deliberately standalone. The <see cref="AdminErrorNotificationSink"/> pipeline cannot carry this
/// alert: a failure before <c>builder.Build()</c> is logged by Serilog's bootstrap logger, which has
/// no sink attached, and a failure escaping <c>app.Run()</c> enqueues a notice that
/// <see cref="AdminErrorNotificationDispatcher"/> will never drain, because the process is ending
/// and the host shutdown timeout is shorter than a single SMTP send. So this reads configuration
/// itself, talks to SMTP directly, and depends on no DI container, no host and no logger.
/// </para>
///
/// <para>
/// Every failure inside it is swallowed. A crashing app must not be made worse by a crashing
/// notifier, and the exception it was reporting is about to be rethrown regardless.
/// </para>
/// </summary>
public static class StartupFailureNotifier
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    public static async Task TryNotifyAsync(Exception failure)
    {
        try
        {
            var configuration = BuildConfiguration();
            var emailSettings = configuration.GetSection("EmailSettings");

            var enabled = configuration
                .GetSection(AdminErrorNotificationOptions.SectionName)
                .GetValue("Enabled", true);
            if (!enabled)
            {
                return;
            }

            var server = emailSettings["Server"];
            var userName = emailSettings["Username"];
            var password = emailSettings["Password"];
            var fromEmail = emailSettings["CustomerServiceEmail"];
            if (string.IsNullOrWhiteSpace(server)
                || string.IsNullOrWhiteSpace(userName)
                || string.IsNullOrWhiteSpace(password)
                || string.IsNullOrWhiteSpace(fromEmail))
            {
                return;
            }

            // Resolved exactly as AdminErrorNotificationDispatcher does. Using ?? instead would
            // accept a configured empty string and then throw inside MailAddress, which this type
            // swallows - so a blank ToEmail would silently disable the one alert that matters.
            var recipient = FirstNonBlank(
                configuration[$"{AdminErrorNotificationOptions.SectionName}:ToEmail"],
                emailSettings["AdminEmail"],
                AdminErrorNotificationOptions.DefaultToEmail);

            var environmentName = ResolveEnvironmentName();

            using var message = new MailMessage();
            message.From = new MailAddress(fromEmail, "StreamTunes");
            message.To.Add(new MailAddress(recipient));
            // Not "failed to start": this catch also covers a failure escaping app.Run(), so the
            // app may have been serving for a week before it got here.
            message.Subject = $"[StreamTunes {environmentName}] FATAL: the application terminated unexpectedly";
            message.IsBodyHtml = true;
            message.Body = BuildBody(failure, environmentName);

            using var client = new SmtpClient(server)
            {
                Port = 587,
                Credentials = new NetworkCredential(userName, password),
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = (int)SendTimeout.TotalMilliseconds
            };

            using var cancellation = new CancellationTokenSource(SendTimeout);
            await client.SendMailAsync(message, cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            // Intentionally swallowed - see the type remarks.
        }
    }

    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return AdminErrorNotificationOptions.DefaultToEmail;
    }

    private static IConfiguration BuildConfiguration()
    {
        var environmentName = ResolveEnvironmentName();
        var builder = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory());

        // Each file added under its own try. `optional: true` covers a missing file, not an
        // unparseable one - and a truncated appsettings.json is among the likeliest reasons the
        // app failed to start in the first place, which would otherwise silence this notifier at
        // exactly the moment it is needed. Environment variables are added last and always.
        AddJsonFileSafely(builder, "appsettings.json");
        AddJsonFileSafely(builder, $"appsettings.{environmentName}.json");

        return builder.AddEnvironmentVariables().Build();
    }

    private static void AddJsonFileSafely(IConfigurationBuilder builder, string fileName)
    {
        try
        {
            builder.AddJsonFile(fileName, optional: true);
        }
        catch
        {
            // Unreadable or malformed. Carry on with whatever else can be loaded.
        }
    }

    /// <summary>
    /// Honours the same variables the host does, so the notifier cannot end up reading a different
    /// environment's settings than the app it is reporting on.
    /// </summary>
    private static string ResolveEnvironmentName() =>
        FirstNonBlankEnvironment("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT") ?? "Production";

    private static string? FirstNonBlankEnvironment(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string BuildBody(Exception failure, string environmentName)
    {
        var now = DateTimeOffset.UtcNow;

        return $"""
            <h2>StreamTunes {WebUtility.HtmlEncode(environmentName)} terminated unexpectedly</h2>
            <table cellpadding="4" style="border-collapse:collapse">
              <tr><td><strong>Time (UTC)</strong></td><td>{now.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z</td></tr>
              <tr><td><strong>Time (server local)</strong></td><td>{now.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}</td></tr>
              <tr><td><strong>Machine</strong></td><td>{WebUtility.HtmlEncode(Environment.MachineName)}</td></tr>
              <tr><td><strong>Exception</strong></td><td>{WebUtility.HtmlEncode(failure.GetType().FullName)}</td></tr>
            </table>
            <h3>Message</h3>
            <pre style="white-space:pre-wrap">{WebUtility.HtmlEncode(failure.Message)}</pre>
            <h3>Stack trace</h3>
            <pre style="white-space:pre-wrap">{WebUtility.HtmlEncode(failure.ToString())}</pre>
            """;
    }
}
