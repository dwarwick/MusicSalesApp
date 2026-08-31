#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicSalesApp.Services;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Parsing;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AdminErrorNotificationThrottleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 10, 46, 9, TimeSpan.Zero);

    [Test]
    public void Admit_FirstOccurrence_IsSentImmediately()
    {
        var throttle = new AdminErrorNotificationThrottle(Window);

        var notification = throttle.Admit(CreateNotice(), Start);

        Assert.Multiple(() =>
        {
            Assert.That(notification, Is.Not.Null);
            Assert.That(notification!.SuppressedCount, Is.Zero);
            Assert.That(notification.IsFollowUp, Is.False);
        });
    }

    [Test]
    public void Admit_RepeatInsideWindow_IsSuppressed()
    {
        var throttle = new AdminErrorNotificationThrottle(Window);
        throttle.Admit(CreateNotice(), Start);

        var repeat = throttle.Admit(CreateNotice(), Start.AddMinutes(30));

        Assert.That(repeat, Is.Null);
    }

    [Test]
    public void Admit_SameTemplateWithDifferentParameters_SharesOneSignature()
    {
        // The payout run logs the same template once per failing creator. Keying on the rendered
        // message would send one email per creator, which is the flood this throttle exists to stop.
        var throttle = new AdminErrorNotificationThrottle(Window);
        throttle.Admit(CreateNotice(renderedMessage: "PayPal payout failed for creator 12"), Start);

        var second = throttle.Admit(
            CreateNotice(renderedMessage: "PayPal payout failed for creator 87"),
            Start.AddSeconds(2));

        Assert.That(second, Is.Null);
    }

    [Test]
    public void Admit_DifferentExceptionTypes_AreSeparateSignatures()
    {
        var throttle = new AdminErrorNotificationThrottle(Window);
        throttle.Admit(CreateNotice(exceptionType: "System.TimeoutException"), Start);

        var other = throttle.Admit(
            CreateNotice(exceptionType: "System.UnauthorizedAccessException"),
            Start.AddSeconds(2));

        Assert.That(other, Is.Not.Null);
    }

    [Test]
    public void Admit_AfterWindowElapses_SendsAgainAndReportsWhatWasSuppressed()
    {
        var throttle = new AdminErrorNotificationThrottle(Window);
        throttle.Admit(CreateNotice(), Start);
        throttle.Admit(CreateNotice(), Start.AddMinutes(10));
        throttle.Admit(CreateNotice(), Start.AddMinutes(20));

        var afterWindow = throttle.Admit(CreateNotice(), Start.Add(Window).AddMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(afterWindow, Is.Not.Null);
            Assert.That(afterWindow!.SuppressedCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void CollectDueFollowUps_ReportsSuppressedRepeats_OnceTheWindowElapses()
    {
        var throttle = new AdminErrorNotificationThrottle(Window);
        throttle.Admit(CreateNotice(), Start);
        throttle.Admit(CreateNotice(renderedMessage: "the most recent one"), Start.AddMinutes(5));

        var beforeWindow = throttle.CollectDueFollowUps(Start.AddMinutes(30));
        var afterWindow = throttle.CollectDueFollowUps(Start.Add(Window).AddMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(beforeWindow, Is.Empty);
            Assert.That(afterWindow, Has.Count.EqualTo(1));
            Assert.That(afterWindow[0].IsFollowUp, Is.True);
            Assert.That(afterWindow[0].SuppressedCount, Is.EqualTo(1));
            Assert.That(afterWindow[0].Notice.RenderedMessage, Is.EqualTo("the most recent one"));
        });
    }

    [Test]
    public void CollectDueFollowUps_SendsNothing_WhenNoRepeatsWereSuppressed()
    {
        var throttle = new AdminErrorNotificationThrottle(Window);
        throttle.Admit(CreateNotice(), Start);

        var due = throttle.CollectDueFollowUps(Start.Add(Window).AddMinutes(1));

        Assert.That(due, Is.Empty);
    }

    private static AdminErrorNotice CreateNotice(
        string renderedMessage = "PayPal payout failed for creator 12",
        string? exceptionType = null) =>
        new(
            Start,
            "Error",
            "MusicSalesApp.Services.StreamPayoutService",
            "PayPal payout failed for creator {CreatorId}",
            renderedMessage,
            exceptionType,
            exceptionType == null ? null : "stack trace");
}

[TestFixture]
public class AdminErrorNotificationSinkTests
{
    private const string PayoutCategory = "MusicSalesApp.Services.StreamPayoutService";

    [Test]
    public void Emit_Error_CapturesTimestampMessageAndFullStackTrace()
    {
        var queue = CreateQueue(new AdminErrorNotificationOptions());
        var sink = CreateSink(queue, new AdminErrorNotificationOptions());
        var exception = CreateThrownException();
        var timestamp = new DateTimeOffset(2026, 8, 30, 22, 23, 55, TimeSpan.Zero);

        sink.Emit(CreateLogEvent(exception: exception, timestamp: timestamp));

        Assert.That(queue.Reader.TryRead(out var notice), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(notice!.TimestampUtc, Is.EqualTo(timestamp));
            Assert.That(notice.Category, Is.EqualTo(PayoutCategory));
            Assert.That(notice.MessageTemplate, Does.Contain("{CreatorId}"));
            Assert.That(notice.RenderedMessage, Does.Contain("12"));
            Assert.That(notice.ExceptionType, Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(notice.ExceptionDetail, Does.Contain(nameof(CreateThrownException)));
        });
    }

    [Test]
    public void Emit_BelowError_IsIgnored()
    {
        var queue = CreateQueue(new AdminErrorNotificationOptions());
        var sink = CreateSink(queue, new AdminErrorNotificationOptions());

        sink.Emit(CreateLogEvent(level: LogEventLevel.Warning));

        Assert.That(queue.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public void Emit_Fatal_IsCaptured()
    {
        var queue = CreateQueue(new AdminErrorNotificationOptions());
        var sink = CreateSink(queue, new AdminErrorNotificationOptions());

        sink.Emit(CreateLogEvent(level: LogEventLevel.Fatal));

        Assert.That(queue.Reader.TryRead(out _), Is.True);
    }

    [TestCaseSource(nameof(ExcludedCategories))]
    public void Emit_ExcludedCategoryPrefix_IsIgnored(string category)
    {
        // Emailing about the email service being down, or about this pipeline's own failures,
        // either cannot work or feeds itself.
        var options = new AdminErrorNotificationOptions();
        var queue = CreateQueue(options);
        var sink = CreateSink(queue, options);

        sink.Emit(CreateLogEvent(category: category));

        Assert.That(queue.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public void Emit_WhenDisabled_IsIgnored()
    {
        var options = new AdminErrorNotificationOptions { Enabled = false };
        var queue = CreateQueue(options);
        var sink = CreateSink(queue, options);

        sink.Emit(CreateLogEvent());

        Assert.That(queue.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public void TryEnqueue_WhenQueueIsFull_DropsAndCountsInsteadOfBlocking()
    {
        var options = new AdminErrorNotificationOptions { QueueCapacity = 16 };
        var queue = CreateQueue(options);
        var sink = CreateSink(queue, options);

        for (var i = 0; i < 20; i++)
        {
            sink.Emit(CreateLogEvent());
        }

        Assert.Multiple(() =>
        {
            Assert.That(queue.ExchangeDroppedCount(), Is.EqualTo(4));
            Assert.That(queue.ExchangeDroppedCount(), Is.Zero, "the count resets when read");
        });
    }

    [Test]
    public void RealSerilogPipeline_PopulatesTheCategoryTheExclusionListMatchesOn()
    {
        // The exclusion list - the only thing stopping this pipeline from emailing about its own
        // failures - matches on SourceContext. Assert that a logger built the way the app builds
        // it actually sets SourceContext to the full type name, rather than trusting it.
        var options = new AdminErrorNotificationOptions();
        var queue = CreateQueue(options);
        var sink = CreateSink(queue, options);
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink, LogEventLevel.Error)
            .CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);
        var logger = factory.CreateLogger(typeof(AdminErrorNotificationDispatcher).FullName!);

        logger.LogError(CreateThrownException(), "PayPal payout failed for creator {CreatorId}", 12);
        logger.LogWarning("not an error");

        Assert.Multiple(() =>
        {
            Assert.That(
                queue.Reader.TryRead(out _),
                Is.False,
                "the dispatcher's own category must be excluded, or a send failure would feed itself");
            Assert.That(queue.ExchangeDroppedCount(), Is.Zero);
        });
    }

    [Test]
    public void RealSerilogPipeline_CapturesAnOrdinaryServiceError()
    {
        var options = new AdminErrorNotificationOptions();
        var queue = CreateQueue(options);
        var sink = CreateSink(queue, options);
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink, LogEventLevel.Error)
            .CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);
        var logger = factory.CreateLogger("MusicSalesApp.Services.StreamPayoutService");

        logger.LogError(CreateThrownException(), "PayPal payout failed for creator {CreatorId}", 12);

        Assert.That(queue.Reader.TryRead(out var notice), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(notice!.Category, Is.EqualTo(PayoutCategory));
            Assert.That(notice.MessageTemplate, Is.EqualTo("PayPal payout failed for creator {CreatorId}"));
            Assert.That(notice.RenderedMessage, Is.EqualTo("PayPal payout failed for creator 12"));
            Assert.That(notice.ExceptionDetail, Does.Contain("payout rejected"));
        });
    }

    // Feeding the prefixes back in as categories only proves StartsWith works on itself. These
    // two build the logger the way the app does - ILogger<T> through Serilog - so they fail if the
    // category a real logger produces ever stops matching the derived prefix.
    [Test]
    public void RealSerilogPipeline_ExcludesTheEmailService()
    {
        // The one that matters: EmailService logs its own delivery failures at Error, so without
        // this exclusion a dead SMTP server makes the pipeline try to email about email.
        var options = new AdminErrorNotificationOptions();
        var queue = CreateQueue(options);
        var sink = CreateSink(queue, options);
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink, LogEventLevel.Error)
            .CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);

        ((ILoggerFactory)factory).CreateLogger<EmailService>()
            .LogError(CreateThrownException(), "SMTP is down");

        Assert.That(queue.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public void RealSerilogPipeline_ExcludesTheNotificationPipelineItself()
    {
        var options = new AdminErrorNotificationOptions();
        var queue = CreateQueue(options);
        var sink = CreateSink(queue, options);
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink, LogEventLevel.Error)
            .CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);

        ((ILoggerFactory)factory).CreateLogger<AdminErrorNotificationDispatcher>()
            .LogError(CreateThrownException(), "could not send");

        Assert.That(queue.Reader.TryRead(out _), Is.False);
    }

    private static IEnumerable<string> ExcludedCategories() =>
        new AdminErrorNotificationOptions().ExcludedCategoryPrefixes;

    private static AdminErrorNotificationQueue CreateQueue(AdminErrorNotificationOptions options) =>
        new(Options.Create(options));

    private static AdminErrorNotificationSink CreateSink(
        IAdminErrorNotificationQueue queue,
        AdminErrorNotificationOptions options) =>
        new(queue, Options.Create(options));

    private static InvalidOperationException CreateThrownException()
    {
        try
        {
            throw new InvalidOperationException("payout rejected");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static LogEvent CreateLogEvent(
        LogEventLevel level = LogEventLevel.Error,
        string category = PayoutCategory,
        Exception? exception = null,
        DateTimeOffset? timestamp = null)
    {
        var template = new MessageTemplateParser()
            .Parse("PayPal payout failed for creator {CreatorId}");

        return new LogEvent(
            timestamp ?? DateTimeOffset.UtcNow,
            level,
            exception,
            template,
            [
                new LogEventProperty("CreatorId", new ScalarValue(12)),
                new LogEventProperty("SourceContext", new ScalarValue(category))
            ]);
    }
}
