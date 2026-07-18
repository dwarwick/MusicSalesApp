using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MusicSalesApp.Middleware;

namespace MusicSalesApp.Tests.Middleware;

[TestFixture]
public class DevelopmentPayPalReturnRedirectMiddlewareTests
{
    [Test]
    public async Task InvokeAsync_DevelopmentNgrokPayPalReturn_RedirectsToLocalhostAndPreservesQuery()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(Environments.Development, () => nextCalled = true);
        var context = CreateContext(
            "bev-rigioristic-uncalculatingly.ngrok-free.dev",
            "/manage-account",
            "?success=true&token=abc&ba_token=def&subscription_id=ghi");

        await middleware.InvokeAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(nextCalled, Is.False);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(
                context.Response.Headers.Location.ToString(),
                Is.EqualTo("https://localhost:7173/manage-account?success=true&token=abc&ba_token=def&subscription_id=ghi"));
        });
    }

    [TestCase("Production", "example.ngrok-free.dev", "/manage-account", "?success=true")]
    [TestCase("Development", "localhost", "/manage-account", "?success=true")]
    [TestCase("Development", "example.ngrok-free.dev", "/music-library", "?success=true")]
    [TestCase("Development", "example.ngrok-free.dev", "/manage-account", "")]
    public async Task InvokeAsync_NonMatchingRequest_ContinuesPipeline(
        string environmentName,
        string host,
        string path,
        string queryString)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(environmentName, () => nextCalled = true);
        var context = CreateContext(host, path, queryString);

        await middleware.InvokeAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(nextCalled, Is.True);
            Assert.That(context.Response.Headers.Location, Is.Empty);
        });
    }

    private static DevelopmentPayPalReturnRedirectMiddleware CreateMiddleware(
        string environmentName,
        Action onNext)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Development:LocalBaseUrl"] = "https://localhost:7173"
            })
            .Build();

        return new DevelopmentPayPalReturnRedirectMiddleware(
            _ =>
            {
                onNext();
                return Task.CompletedTask;
            },
            new TestWebHostEnvironment { EnvironmentName = environmentName },
            configuration);
    }

    private static DefaultHttpContext CreateContext(string host, string path, string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        return context;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MusicSalesApp.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
