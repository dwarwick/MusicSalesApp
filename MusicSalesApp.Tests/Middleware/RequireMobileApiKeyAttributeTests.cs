#nullable enable
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Middleware;

namespace MusicSalesApp.Tests.Middleware;

[TestFixture]
public class RequireMobileApiKeyAttributeTests
{
    private const string ValidApiKey = "test-api-key-12345";
    private RequireMobileApiKeyAttribute _filter;

    [SetUp]
    public void SetUp()
    {
        _filter = new RequireMobileApiKeyAttribute();
    }

    [Test]
    public async Task ValidApiKey_AllowsRequest()
    {
        var context = CreateContext(ValidApiKey, configuredKey: ValidApiKey);
        var executed = false;

        await _filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.That(executed, Is.True);
        Assert.That(context.Result, Is.Null);
    }

    [Test]
    public async Task MissingApiKeyHeader_Returns401()
    {
        var context = CreateContext(headerKey: null, configuredKey: ValidApiKey);

        await _filter.OnActionExecutionAsync(context, () =>
        {
            Assert.Fail("Should not execute action");
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.That(context.Result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task WrongApiKey_Returns401()
    {
        var context = CreateContext("wrong-key", configuredKey: ValidApiKey);

        await _filter.OnActionExecutionAsync(context, () =>
        {
            Assert.Fail("Should not execute action");
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.That(context.Result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task EmptyApiKeyHeader_Returns401()
    {
        var context = CreateContext("", configuredKey: ValidApiKey);

        await _filter.OnActionExecutionAsync(context, () =>
        {
            Assert.Fail("Should not execute action");
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.That(context.Result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task NoConfiguredApiKey_Returns401_FailClosed()
    {
        var context = CreateContext(ValidApiKey, configuredKey: null);

        await _filter.OnActionExecutionAsync(context, () =>
        {
            Assert.Fail("Should not execute action when key is not configured");
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.That(context.Result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task EmptyConfiguredApiKey_Returns401_FailClosed()
    {
        var context = CreateContext(ValidApiKey, configuredKey: "");

        await _filter.OnActionExecutionAsync(context, () =>
        {
            Assert.Fail("Should not execute action when key is empty");
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.That(context.Result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task CaseSensitiveComparison_RejectsWrongCase()
    {
        var context = CreateContext("Test-Api-Key-12345", configuredKey: "test-api-key-12345");

        await _filter.OnActionExecutionAsync(context, () =>
        {
            Assert.Fail("API key comparison should be case-sensitive");
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.That(context.Result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    private static ActionExecutingContext CreateContext(string? headerKey, string? configuredKey)
    {
        var httpContext = new DefaultHttpContext();
        if (headerKey != null)
            httpContext.Request.Headers["X-Api-Key"] = headerKey;

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["MobileApiKey"]).Returns(configuredKey);

        var services = new ServiceCollection();
        services.AddSingleton(mockConfig.Object);
        httpContext.RequestServices = services.BuildServiceProvider();

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: null!);
    }

    private static ActionExecutedContext CreateExecutedContext(ActionExecutingContext context)
    {
        return new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: null!);
    }
}
