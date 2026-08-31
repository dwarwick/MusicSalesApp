#nullable enable

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AdminErrorNotificationDispatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 10, 46, 9, TimeSpan.Zero);

    private MutableTimeProvider _timeProvider = null!;
    private Mock<IEmailService> _emailService = null!;
    private List<SentEmail> _sent = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new MutableTimeProvider(Start);
        _sent = [];
        _emailService = new Mock<IEmailService>();
        ArrangeDelivery(EmailResult.Succeeded());
    }

    private void ArrangeDelivery(EmailResult result) =>
        _emailService.Setup(service => service.SendEmailWithResultAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync((string to, string subject, string body) =>
            {
                lock (_sent)
                {
                    _sent.Add(new SentEmail(to, subject, body));
                }

                return result;
            });

    [Test]
    public async Task Dispatcher_SendsToTheConfiguredNotificationAddress()
    {
        await using var host = await StartAsync(new AdminErrorNotificationOptions
        {
            ToEmail = "oncall@streamtunes.example"
        });
        host.Queue.TryEnqueue(CreateNotice());

        var email = await host.WaitForEmailAsync();

        Assert.That(email.To, Is.EqualTo("oncall@streamtunes.example"));
    }

    [Test]
    public async Task Dispatcher_FallsBackToTheAdminEmailFromEmailSettings()
    {
        await using var host = await StartAsync(new AdminErrorNotificationOptions());
        host.Queue.TryEnqueue(CreateNotice());

        var email = await host.WaitForEmailAsync();

        Assert.That(email.To, Is.EqualTo("admin@streamtunes.example"));
    }

    [Test]
    public async Task Dispatcher_TruncatesASubjectTooLongForAMailClient()
    {
        await using var host = await StartAsync(new AdminErrorNotificationOptions());
        host.Queue.TryEnqueue(CreateNotice(renderedMessage: new string('x', 500)));

        var email = await host.WaitForEmailAsync();

        // Exactly 150, not merely under it: an empty subject would satisfy "at most 150" without
        // any truncation having happened.
        Assert.That(email.Subject, Has.Length.EqualTo(150));
    }

    [Test]
    public async Task Dispatcher_IncludesTheStackTraceAndEncodesTheMessage()
    {
        await using var host = await StartAsync(new AdminErrorNotificationOptions());
        host.Queue.TryEnqueue(CreateNotice(
            renderedMessage: "<script>alert('x')</script>",
            exceptionDetail: "System.InvalidOperationException: boom\n   at Thing.Do()"));

        var email = await host.WaitForEmailAsync();

        Assert.Multiple(() =>
        {
            Assert.That(email.Body, Does.Contain("at Thing.Do()"), "the stack trace is the point");
            Assert.That(email.Body, Does.Contain("Time (UTC)"));
            Assert.That(email.Body, Does.Not.Contain("<script>"), "rendered text is attacker-influenced");
            Assert.That(email.Body, Does.Contain("&lt;script&gt;"));
        });
    }

    [Test]
    public async Task Dispatcher_ReportsSuppressedRepeatsOnceTheWindowElapses()
    {
        await using var host = await StartAsync(new AdminErrorNotificationOptions
        {
            ThrottleWindowMinutes = 60
        });

        host.Queue.TryEnqueue(CreateNotice());
        await host.WaitForEmailAsync();

        // The repeat, then a different signature. The channel has a single reader draining in
        // order, so the second signature's email is proof the repeat ahead of it was already
        // counted - without that barrier the clock could advance first and the repeat would be
        // admitted as a fresh occurrence, quietly passing this test for the wrong reason.
        host.Queue.TryEnqueue(CreateNotice());
        host.Queue.TryEnqueue(CreateNotice(category: "MusicSalesApp.Services.SomethingElse"));
        await host.WaitForEmailCountAsync(2);

        _timeProvider.Advance(TimeSpan.FromMinutes(61));
        host.Queue.TryEnqueue(CreateNotice(category: "MusicSalesApp.Services.SomethingElseAgain"));
        await host.WaitForEmailCountAsync(4);

        Assert.That(
            host.SentSubjects(),
            Has.Some.Contains("1 more"));
    }

    [Test]
    public async Task Dispatcher_KeepsTheDroppedCountWhenDeliveryFails()
    {
        // The count is destructive to read, so a report that is built but never delivered would
        // take the evidence of the drop with it.
        ArrangeDelivery(EmailResult.SmtpError("mail host down"));

        // Overflowed before the dispatcher starts. Filling it while the dispatcher runs made the
        // drop depend on whether the writer outran the reader - true on a slow machine, false on a
        // fast one, so the test could pass without the overflow it claims to exercise ever
        // happening.
        await using var host = await StartAsync(
            new AdminErrorNotificationOptions { QueueCapacity = 16 },
            beforeStart: queue =>
            {
                for (var i = 0; i < 40; i++)
                {
                    queue.TryEnqueue(CreateNotice());
                }
            });

        await host.WaitForEmailCountAsync(1);
        await host.StopAsync();

        Assert.That(
            host.Queue.ExchangeDroppedCount(),
            Is.EqualTo(24),
            "a failed send must put back every one of the 40 - 16 dropped notices");
    }

    // --- harness ---

    private async Task<DispatcherHost> StartAsync(
        AdminErrorNotificationOptions options,
        Action<AdminErrorNotificationQueue>? beforeStart = null)
    {
        var queue = new AdminErrorNotificationQueue(Options.Create(options));
        beforeStart?.Invoke(queue);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:AdminEmail"] = "admin@streamtunes.example"
            })
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(host => host.EnvironmentName).Returns("Production");

        var dispatcher = new AdminErrorNotificationDispatcher(
            queue,
            new StubScopeFactory(_emailService.Object),
            Options.Create(options),
            configuration,
            environment.Object,
            _timeProvider,
            Mock.Of<ILogger<AdminErrorNotificationDispatcher>>());

        await dispatcher.StartAsync(CancellationToken.None);
        return new DispatcherHost(dispatcher, queue, this);
    }

    private static AdminErrorNotice CreateNotice(
        string renderedMessage = "PayPal payout failed for creator 12",
        string category = "MusicSalesApp.Services.StreamPayoutService",
        string? exceptionDetail = null) =>
        new(
            Start,
            "Error",
            category,
            "PayPal payout failed for creator {CreatorId}",
            renderedMessage,
            exceptionDetail == null ? null : "System.InvalidOperationException",
            exceptionDetail);

    private sealed record SentEmail(string To, string Subject, string Body);

    private sealed class DispatcherHost(
        AdminErrorNotificationDispatcher dispatcher,
        AdminErrorNotificationQueue queue,
        AdminErrorNotificationDispatcherTests owner) : IAsyncDisposable
    {
        public AdminErrorNotificationQueue Queue => queue;

        public async Task<SentEmail> WaitForEmailAsync()
        {
            await WaitForEmailCountAsync(1);
            lock (owner._sent)
            {
                return owner._sent[0];
            }
        }

        public async Task WaitForEmailCountAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (owner._sent)
                {
                    if (owner._sent.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            lock (owner._sent)
            {
                Assert.Fail($"Expected {count} email(s), got {owner._sent.Count}");
            }
        }

        public Task StopAsync() => dispatcher.StopAsync(CancellationToken.None);

        public IReadOnlyList<string> SentSubjects()
        {
            lock (owner._sent)
            {
                return owner._sent.Select(email => email.Subject).ToList();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await dispatcher.StopAsync(CancellationToken.None);
            dispatcher.Dispose();
        }
    }

    private sealed class StubScopeFactory(IEmailService emailService)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEmailService) ? emailService : null;

        public void Dispose()
        {
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _utcNow = utcNow;

        // Written by the test thread and read by the dispatcher's loop, so the 16-byte
        // DateTimeOffset needs a lock; an unsynchronised write can be read torn.
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _utcNow = _utcNow.Add(by);
            }
        }
    }
}
