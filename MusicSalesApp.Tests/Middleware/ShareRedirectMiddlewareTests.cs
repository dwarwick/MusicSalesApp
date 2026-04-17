#nullable enable
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Middleware;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Middleware;

[TestFixture]
public class ShareRedirectMiddlewareTests
{
    private Mock<ISongMetadataService> _songMetadataService;
    private Mock<IOpenGraphService> _openGraphService;
    private Mock<ILogger<ShareRedirectMiddleware>> _logger;
    private bool _nextCalled;

    [SetUp]
    public void SetUp()
    {
        _songMetadataService = new Mock<ISongMetadataService>();
        _openGraphService = new Mock<IOpenGraphService>();
        _logger = new Mock<ILogger<ShareRedirectMiddleware>>();
        _nextCalled = false;
    }

    private ShareRedirectMiddleware CreateMiddleware()
    {
        return new ShareRedirectMiddleware(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });
    }

    private HttpContext CreateHttpContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private async Task<string> GetResponseBody(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Test]
    public async Task NonSharePath_PassesToNext()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/song/test");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        Assert.That(_nextCalled, Is.True);
    }

    [TestCase("/")]
    [TestCase("/about")]
    [TestCase("/share/")]
    [TestCase("/share/abc")]
    public async Task InvalidPaths_PassToNext(string path)
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(path);

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        Assert.That(_nextCalled, Is.True);
    }

    [Test]
    public async Task SongNotFound_Returns404()
    {
        _songMetadataService.Setup(s => s.GetByIdAsync(999))
            .ReturnsAsync((SongMetadata)null!);

        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/share/999");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
        Assert.That(_nextCalled, Is.False);
    }

    [Test]
    public async Task ValidSong_Returns200WithOgTags()
    {
        var song = new SongMetadata { Id = 19, SongTitle = "All Around Me", Mp3BlobPath = "music/all-around-me.mp3" };
        _songMetadataService.Setup(s => s.GetByIdAsync(19)).ReturnsAsync(song);
        _openGraphService.Setup(o => o.GenerateSongMetaTagsByIdAsync(19))
            .ReturnsAsync("<meta property=\"og:title\" content=\"All Around Me\" />");

        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/share/19");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        var body = await GetResponseBody(context);
        Assert.That(body, Does.Contain("og:title"));
    }

    [Test]
    public async Task ValidSong_ContainsCustomSchemeAppLink()
    {
        var song = new SongMetadata { Id = 19, SongTitle = "All Around Me", Mp3BlobPath = "music/all-around-me.mp3" };
        _songMetadataService.Setup(s => s.GetByIdAsync(19)).ReturnsAsync(song);
        _openGraphService.Setup(o => o.GenerateSongMetaTagsByIdAsync(19)).ReturnsAsync("");

        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/share/19");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        var body = await GetResponseBody(context);
        Assert.That(body, Does.Contain("streamtunes://share/19"));
    }

    [Test]
    public async Task ValidSong_ContainsOpenAppBanner()
    {
        var song = new SongMetadata { Id = 19, SongTitle = "All Around Me", Mp3BlobPath = "music/all-around-me.mp3" };
        _songMetadataService.Setup(s => s.GetByIdAsync(19)).ReturnsAsync(song);
        _openGraphService.Setup(o => o.GenerateSongMetaTagsByIdAsync(19)).ReturnsAsync("");

        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/share/19");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        var body = await GetResponseBody(context);
        Assert.That(body, Does.Contain("Open App"));
        Assert.That(body, Does.Contain("Listen in the StreamTunes app"));
    }

    [Test]
    public async Task ValidSong_ContainsWebRedirectFallback()
    {
        var song = new SongMetadata { Id = 19, SongTitle = "All Around Me", Mp3BlobPath = "music/all-around-me.mp3" };
        _songMetadataService.Setup(s => s.GetByIdAsync(19)).ReturnsAsync(song);
        _openGraphService.Setup(o => o.GenerateSongMetaTagsByIdAsync(19)).ReturnsAsync("");

        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/share/19");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        var body = await GetResponseBody(context);
        Assert.That(body, Does.Contain("/song/All%20Around%20Me"));
        Assert.That(body, Does.Contain("window.location.replace"));
    }

    [Test]
    public async Task NullSongTitle_FallsBackToFileName()
    {
        var song = new SongMetadata { Id = 19, SongTitle = null!, Mp3BlobPath = "music/All Around Me.mp3" };
        _songMetadataService.Setup(s => s.GetByIdAsync(19)).ReturnsAsync(song);
        _openGraphService.Setup(o => o.GenerateSongMetaTagsByIdAsync(19)).ReturnsAsync("");

        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/share/19");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        var body = await GetResponseBody(context);
        Assert.That(body, Does.Contain("All Around Me"));
        Assert.That(body, Does.Contain("/song/All%20Around%20Me"));
    }

    [Test]
    public async Task TrailingSlash_StillMatches()
    {
        var song = new SongMetadata { Id = 5, SongTitle = "Test", Mp3BlobPath = "music/test.mp3" };
        _songMetadataService.Setup(s => s.GetByIdAsync(5)).ReturnsAsync(song);
        _openGraphService.Setup(o => o.GenerateSongMetaTagsByIdAsync(5)).ReturnsAsync("");

        var middleware = CreateMiddleware();
        var context = CreateHttpContext("/share/5/");

        await middleware.InvokeAsync(context, _openGraphService.Object,
            _songMetadataService.Object, _logger.Object);

        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        Assert.That(_nextCalled, Is.False);
    }
}
