#nullable enable
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

/// <summary>
/// The persona art endpoint takes a caller-supplied blob path, so the whitelist that decides what
/// it will serve gets its own fixture - the same treatment the song media endpoint gets.
/// </summary>
[TestFixture]
public class PersonaArtControllerTests
{
    private const string Guid32 = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string PersonaImage = $"{Guid32}/{Guid32}-persona.jpg";
    private static readonly string Rendition = $"{PersonaImage}.w128.webp";

    private Mock<ICreatorPersonaService> _personas = null!;
    private PersonaArtController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _personas = new Mock<ICreatorPersonaService>();
        _controller = new PersonaArtController(_personas.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private void AllowPath(string path) =>
        _personas.Setup(p => p.IsPubliclyReadableImagePathAsync(path)).ReturnsAsync(true);

    private void BlobContains(string path, string content) =>
        _personas.Setup(p => p.OpenPersonaImageReadAsync(path))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private void BlobMissing(string path) =>
        _personas.Setup(p => p.OpenPersonaImageReadAsync(path)).ReturnsAsync((Stream?)null);

    [Test]
    public async Task Get_ServesAWhitelistedImage()
    {
        AllowPath(PersonaImage);
        BlobContains(PersonaImage, "image-bytes");

        var result = await _controller.Get(PersonaImage);

        Assert.That(result, Is.InstanceOf<FileStreamResult>());
        Assert.That(((FileStreamResult)result).ContentType, Is.EqualTo("image/jpeg"));
    }

    [Test]
    public async Task Get_MarksTheResponseImmutable()
    {
        // The whole point of the endpoint. Paired with the ?v={ImageVariantVersion} the URL builder
        // emits, this is what lets a browser reuse an avatar instead of re-downloading it per load.
        AllowPath(PersonaImage);
        BlobContains(PersonaImage, "image-bytes");

        await _controller.Get(PersonaImage);

        Assert.That(
            _controller.Response.Headers["Cache-Control"].ToString(),
            Is.EqualTo("public,max-age=31536000,immutable"));
    }

    [Test]
    public async Task Get_RefusesAPathThatIsNotWhitelisted()
    {
        // A disabled persona's avatar is not public, and guessing its path must not be a way in.
        _personas.Setup(p => p.IsPubliclyReadableImagePathAsync(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Get(PersonaImage);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Get_ChecksTheWhitelistBeforeTouchingStorage()
    {
        _personas.Setup(p => p.IsPubliclyReadableImagePathAsync(It.IsAny<string>())).ReturnsAsync(false);

        await _controller.Get(PersonaImage);

        _personas.Verify(p => p.OpenPersonaImageReadAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Get_FallsBackToTheMaster_WhenTheRenditionIsMissing()
    {
        // A rendition can be legitimately absent - mid-backfill, or restored from a backup taken
        // before the backfill ran. A 404 here would show a broken image rather than a large one.
        AllowPath(Rendition);
        BlobMissing(Rendition);
        BlobContains(PersonaImage, "master-bytes");

        var result = await _controller.Get(Rendition);

        Assert.That(result, Is.InstanceOf<FileStreamResult>());
        Assert.That(((FileStreamResult)result).ContentType, Is.EqualTo("image/jpeg"),
            "the master is a .jpg, so the content type must follow the blob actually served");
    }

    [Test]
    public async Task Get_ServesARenditionAsWebp()
    {
        AllowPath(Rendition);
        BlobContains(Rendition, "webp-bytes");

        var result = await _controller.Get(Rendition);

        Assert.That(((FileStreamResult)result).ContentType, Is.EqualTo("image/webp"));
    }

    [Test]
    public async Task Get_WhenNeitherRenditionNorMasterExists_Returns404()
    {
        AllowPath(Rendition);
        BlobMissing(Rendition);
        BlobMissing(PersonaImage);

        Assert.That(await _controller.Get(Rendition), Is.InstanceOf<NotFoundResult>());
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task Get_WithNoPath_ReturnsBadRequest(string path)
        => Assert.That(await _controller.Get(path), Is.InstanceOf<BadRequestResult>());
}
