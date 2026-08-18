using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The client that starts Durable orchestrations and asks Azure how they are going.
///
/// <para>
/// Everything here is about the 202 payload, because that response is the <b>only</b> moment an
/// orchestration's instance id is ever offered to this application. A queue-triggered starter has no
/// response channel; losing or mis-parsing this payload means a run nobody can query, cancel, or
/// correlate a late callback against.
/// </para>
/// </summary>
[TestFixture]
public class DurableTaskClientTests
{
    private const string BaseUrl = "https://streamtunes-lyrics-test.azurewebsites.net";
    private const string FunctionKey = "test-function-key";

    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private StubHandler _handler = null!;
    private DurableTaskClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"durable-client-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);
        _handler = new StubHandler();

        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri(BaseUrl + "/") };

        _client = new DurableTaskClient(
            httpClient,
            _factory,
            Options.Create(new LyricsFunctionsOptions { BaseUrl = BaseUrl, FunctionKey = FunctionKey }),
            Mock.Of<ILogger<DurableTaskClient>>());
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    // -----------------------------------------------------------------
    // Starting
    // -----------------------------------------------------------------

    [Test]
    public async Task AStartRecordsTheInstanceIdAndItsManagementUrls()
    {
        GivenStartResponse();

        var outcome = await _client.StartAsync("align_lyrics_orchestrator", LyricsStarterRoutes.Start, new { jobId = 1 });

        await using var context = new AppDbContext(_options);
        var task = await context.DurableFunctionTasks.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Started, Is.True);
            Assert.That(task.InstanceId, Is.EqualTo("abc123"));
            Assert.That(task.FunctionName, Is.EqualTo("align_lyrics_orchestrator"));
            Assert.That(task.Status, Is.EqualTo(DurableTaskStatus.Running));
            Assert.That(task.StatusQueryUri, Does.Contain("/instances/abc123"));
            Assert.That(task.TerminateUri, Does.Contain("/terminate"));
        });
    }

    [Test]
    public async Task TheFunctionKeyIsSentAsAHeaderRatherThanInTheUrl()
    {
        // A key in a query string ends up in request logs and browser history. Azure's own header is
        // the platform-standard way to present it, and it is checked before any of our code runs.
        GivenStartResponse();

        await _client.StartAsync("orchestrator", LyricsStarterRoutes.Start, new { jobId = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(
                _handler.LastRequest!.Headers.GetValues(LyricsStarterRoutes.FunctionKeyHeaderName).Single(),
                Is.EqualTo(FunctionKey));
            Assert.That(_handler.LastRequest.RequestUri!.Query, Does.Not.Contain(FunctionKey));
        });
    }

    [Test]
    public async Task TheInputIsPersistedExactlyAsItWasSent()
    {
        // The row is the only complete record of what was actually asked for. Reconstructing it
        // later assumes the reconstruction code has not changed since, which is exactly the
        // assumption that fails during an investigation.
        GivenStartResponse();

        await _client.StartAsync(
            "orchestrator", LyricsStarterRoutes.Start, new { jobId = "j1", songMetadataId = 42 });

        await using var context = new AppDbContext(_options);
        var task = await context.DurableFunctionTasks.SingleAsync();
        var sent = JsonSerializer.Deserialize<JsonElement>(_handler.LastBody!);

        Assert.Multiple(() =>
        {
            Assert.That(task.InputJson, Is.EqualTo(_handler.LastBody));
            Assert.That(sent.GetProperty("songMetadataId").GetInt32(), Is.EqualTo(42));
        });
    }

    [Test]
    public async Task AnUnreachableStarterIsReportedRatherThanThrown()
    {
        // The caller is a Hangfire job with its own retry policy. Reporting lets it decide; throwing
        // from inside here would make that decision twice.
        _handler.Throw = new HttpRequestException("Connection refused");

        var outcome = await _client.StartAsync("orchestrator", LyricsStarterRoutes.Start, new { });

        await using var context = new AppDbContext(_options);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Started, Is.False);
            Assert.That(outcome.FailureDetail, Does.Contain("Connection refused"));
            Assert.That(context.DurableFunctionTasks.Count(), Is.Zero, "Nothing started, nothing recorded.");
        });
    }

    [Test]
    public async Task ARejectedStartIsReportedWithTheStatusCode()
    {
        _handler.Respond(HttpStatusCode.Unauthorized, "no key");

        var outcome = await _client.StartAsync("orchestrator", LyricsStarterRoutes.Start, new { });

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Started, Is.False);
            Assert.That(outcome.FailureDetail, Does.Contain("401"));
        });
    }

    [Test]
    public async Task ASuccessWithNoInstanceIdIsTreatedAsAFailure()
    {
        // An orchestration may well be running and we have no handle on it. That is worse than none:
        // untrackable work that nothing can query or stop. The caller retries and the orphan ages out
        // of the task hub.
        _handler.Respond(HttpStatusCode.Accepted, """{"statusQueryGetUri":"https://x/instances/"}""");

        var outcome = await _client.StartAsync("orchestrator", LyricsStarterRoutes.Start, new { });

        await using var context = new AppDbContext(_options);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Started, Is.False);
            Assert.That(context.DurableFunctionTasks.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task AnUnconfiguredEnvironmentDoesNotEvenTry()
    {
        var client = new DurableTaskClient(
            new HttpClient(_handler),
            _factory,
            Options.Create(new LyricsFunctionsOptions()),
            Mock.Of<ILogger<DurableTaskClient>>());

        var outcome = await client.StartAsync("orchestrator", LyricsStarterRoutes.Start, new { });

        Assert.Multiple(() =>
        {
            Assert.That(client.IsConfigured, Is.False);
            Assert.That(outcome.Started, Is.False);
            Assert.That(_handler.LastRequest, Is.Null, "No request should have been attempted.");
        });
    }

    // -----------------------------------------------------------------
    // Querying
    // -----------------------------------------------------------------

    [Test]
    public async Task AStatusQueryRecordsWhatAzureSaid()
    {
        var id = await AddTaskAsync();
        _handler.Respond(HttpStatusCode.OK, """{"runtimeStatus":"Completed","output":{"jobId":"x"}}""");

        var outcome = await _client.GetStatusAsync(id);

        await using var context = new AppDbContext(_options);
        var task = await context.DurableFunctionTasks.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Answered, Is.True);
            Assert.That(outcome.Status, Is.EqualTo(DurableTaskStatus.Completed));
            Assert.That(outcome.Output, Is.Not.Null);
            Assert.That(task.RuntimeStatusRaw, Is.EqualTo("Completed"));
            Assert.That(task.LastPolledAt, Is.Not.Null);
            Assert.That(task.CompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task AnUnreachableStatusEndpointIsNotAVerdict()
    {
        // The distinction the whole pipeline rests on: "we could not ask" must never be read as "it
        // failed", or an outage fails every healthy run in flight.
        var id = await AddTaskAsync();
        _handler.Throw = new HttpRequestException("timeout");

        var outcome = await _client.GetStatusAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Answered, Is.False);
            Assert.That(outcome.Status, Is.EqualTo(DurableTaskStatus.Running), "The recorded status stands.");
        });
    }

    [Test]
    public async Task AnInstanceThePurgeHasRemovedIsAnAnswer()
    {
        // 404 means the task hub no longer has it - purged history, or a hub recreated. The run is
        // gone and will never call back, so this IS a verdict, unlike an unreachable endpoint.
        var id = await AddTaskAsync();
        _handler.Respond(HttpStatusCode.NotFound, "");

        var outcome = await _client.GetStatusAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Answered, Is.True);
            Assert.That(outcome.Status, Is.EqualTo(DurableTaskStatus.Failed));
        });
    }

    [TestCase("Running", DurableTaskStatus.Running)]
    [TestCase("Pending", DurableTaskStatus.Running)]
    [TestCase("ContinuedAsNew", DurableTaskStatus.Running)]
    [TestCase("Completed", DurableTaskStatus.Completed)]
    [TestCase("Failed", DurableTaskStatus.Failed)]
    [TestCase("Terminated", DurableTaskStatus.Terminated)]
    [TestCase("SomethingAzureAddedLater", DurableTaskStatus.Running)]
    public void RuntimeStatusMapsConservatively(string runtimeStatus, DurableTaskStatus expected)
    {
        // Anything unrecognised maps to Running rather than Failed, deliberately. An unknown status
        // is ignorance, not evidence of a fault, and mapping it to Failed would have a status Azure
        // adds in future start failing healthy runs. The raw string is kept alongside so the unknown
        // value stays visible.
        Assert.That(DurableTaskClient.MapRuntimeStatus(runtimeStatus), Is.EqualTo(expected));
    }

    [Test]
    public async Task TerminatingRecordsTheOutcomeImmediately()
    {
        // So the creator's dialog reflects what they just asked for. Azure agrees a moment later.
        var id = await AddTaskAsync();
        _handler.Respond(HttpStatusCode.OK, "");

        var terminated = await _client.TerminateAsync(id, "Cancelled by the creator.");

        await using var context = new AppDbContext(_options);
        var task = await context.DurableFunctionTasks.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(terminated, Is.True);
            Assert.That(task.Status, Is.EqualTo(DurableTaskStatus.Terminated));
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(), Does.Not.Contain("{text}"));
        });
    }

    [Test]
    public async Task AFailedTerminateIsReportedRatherThanAssumed()
    {
        var id = await AddTaskAsync();
        _handler.Throw = new HttpRequestException("unreachable");

        Assert.That(await _client.TerminateAsync(id, "reason"), Is.False);
    }

    private void GivenStartResponse() => _handler.Respond(HttpStatusCode.Accepted, """
        {
          "id": "abc123",
          "statusQueryGetUri": "https://streamtunes-lyrics-test.azurewebsites.net/runtime/webhooks/durabletask/instances/abc123?taskHub=LyricsAlignHubDev&connection=Storage&code=SYSTEMKEY",
          "sendEventPostUri": "https://x/raiseEvent/{eventName}",
          "terminatePostUri": "https://streamtunes-lyrics-test.azurewebsites.net/runtime/webhooks/durabletask/instances/abc123/terminate?reason={text}&taskHub=LyricsAlignHubDev&code=SYSTEMKEY",
          "purgeHistoryDeleteUri": "https://x/purge"
        }
        """);

    private async Task<int> AddTaskAsync()
    {
        await using var context = new AppDbContext(_options);
        var task = new DurableFunctionTask
        {
            InstanceId = "abc123",
            FunctionName = "orchestrator",
            Status = DurableTaskStatus.Running,
            InputJson = "{}",
            StatusQueryUri = "https://x/instances/abc123?code=k",
            TerminateUri = "https://x/instances/abc123/terminate?reason={text}&code=k"
        };
        context.DurableFunctionTasks.Add(task);
        await context.SaveChangesAsync();
        return task.Id;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "";

        public HttpRequestMessage LastRequest { get; private set; }
        public string LastBody { get; private set; }
        public Exception Throw { get; set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            if (Throw is not null)
            {
                throw Throw;
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
