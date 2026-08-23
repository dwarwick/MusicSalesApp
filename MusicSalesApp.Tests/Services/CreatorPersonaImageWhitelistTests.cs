#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The gate behind the public persona-art endpoint. This is the only thing standing between an
/// arbitrary blob path on the wire and the persona image container, so it is tested against a real
/// (in-memory) database rather than through a mock of itself.
/// </summary>
[TestFixture]
public class CreatorPersonaImageWhitelistTests
{
    private const string Guid32 = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string PersonaImage = $"{Guid32}/{Guid32}-persona.jpg";

    private DbContextOptions<AppDbContext> _options = null!;
    private CreatorPersonaService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"persona-whitelist-{Guid.NewGuid()}")
            .Options;

        _service = new CreatorPersonaService(
            new TestFactory(_options),
            Options.Create(new AzureStorageOptions()),
            Mock.Of<ILogger<CreatorPersonaService>>(),
            Mock.Of<IAdminNotificationService>(),
            Mock.Of<IEmailService>(),
            Mock.Of<IImageVariantCoordinator>(),
            Mock.Of<IConfiguration>());
    }

    private async Task SeedPersonaAsync(string? imageBlobPath, bool isEnabled)
    {
        await using var context = new AppDbContext(_options);
        context.CreatorPersonas.Add(new CreatorPersona
        {
            CreatorId = 1,
            Name = "Test Persona",
            ImageBlobPath = imageBlobPath,
            IsEnabled = isEnabled
        });
        await context.SaveChangesAsync();
    }

    [Test]
    public async Task AnEnabledPersonasImage_IsPublic()
    {
        await SeedPersonaAsync(PersonaImage, isEnabled: true);

        Assert.That(await _service.IsPubliclyReadableImagePathAsync(PersonaImage), Is.True);
    }

    [Test]
    public async Task ARenditionOfAnEnabledPersonasImage_IsPublic()
    {
        // The rendition path is the master's with ".w{width}.webp" appended, so one lookup covers
        // both - there is no separate row recording that a rendition exists.
        await SeedPersonaAsync(PersonaImage, isEnabled: true);

        Assert.That(
            await _service.IsPubliclyReadableImagePathAsync($"{PersonaImage}.w128.webp"),
            Is.True);
    }

    [Test]
    public async Task ADisabledPersonasImage_IsNotPublic()
    {
        // The gate is the persona's status, never the shape of the path. Disabling a persona has to
        // actually take its avatar off the public endpoint.
        await SeedPersonaAsync(PersonaImage, isEnabled: false);

        Assert.That(await _service.IsPubliclyReadableImagePathAsync(PersonaImage), Is.False);
    }

    [Test]
    public async Task ADisabledPersonasRendition_IsNotPublic()
    {
        // The rendition path must not be a way around the check the master path fails.
        await SeedPersonaAsync(PersonaImage, isEnabled: false);

        Assert.That(
            await _service.IsPubliclyReadableImagePathAsync($"{PersonaImage}.w128.webp"),
            Is.False);
    }

    [Test]
    public async Task APathNoPersonaClaims_IsNotPublic()
    {
        await SeedPersonaAsync(PersonaImage, isEnabled: true);

        Assert.That(
            await _service.IsPubliclyReadableImagePathAsync($"{Guid32}/someone-elses-file.jpg"),
            Is.False);
    }

    [Test]
    public async Task APersonaWithNoImage_DoesNotWhitelistAnEmptyPath()
    {
        // Guards the null-vs-empty trap: a persona row with no ImageBlobPath must not make the
        // empty path - or any path - readable.
        await SeedPersonaAsync(null, isEnabled: true);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.IsPubliclyReadableImagePathAsync(""), Is.False);
            Assert.That(await _service.IsPubliclyReadableImagePathAsync(PersonaImage), Is.False);
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task ABlankPath_IsNotPublic(string path)
        => Assert.That(await _service.IsPubliclyReadableImagePathAsync(path), Is.False);

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
