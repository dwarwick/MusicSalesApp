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
/// Persona names have to be unique per creator, and until this existed they were not:
/// CreatePersonaAsync inserted straight into CreatorPersonas with no check at all, so a creator
/// could end up with two personas called the same thing.
///
/// That failure is silent rather than loud. Nothing errors; the songs just credit a name that
/// now points at two different rows, and no surface anywhere - song card, artist page, upload
/// picker - can tell the reader which one it means.
///
/// Tested against a real (in-memory) database rather than a mock of the service, because the
/// check is a query and a mock would only assert that the query was asked for.
/// </summary>
[TestFixture]
public class CreatorPersonaNameUniquenessTests
{
    private const int Creator = 1;
    private const int OtherCreator = 2;

    private DbContextOptions<AppDbContext> _options = null!;
    private CreatorPersonaService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"persona-names-{Guid.NewGuid()}")
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

    private async Task<int> SeedAsync(string name, int creatorId = Creator, bool isEnabled = true)
    {
        await using var context = new AppDbContext(_options);
        var persona = new CreatorPersona { CreatorId = creatorId, Name = name, IsEnabled = isEnabled };
        context.CreatorPersonas.Add(persona);
        await context.SaveChangesAsync();
        return persona.Id;
    }

    [Test]
    public async Task AnExactMatch_IsADuplicate()
    {
        await SeedAsync("Nightshift Radio");

        Assert.That(await _service.PersonaNameExistsAsync(Creator, "Nightshift Radio"), Is.True);
    }

    [Test]
    public async Task CaseAndSurroundingSpaceDoNotMakeItDifferent()
    {
        await SeedAsync("Nightshift Radio");

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.PersonaNameExistsAsync(Creator, "nightshift radio"), Is.True);
            Assert.That(await _service.PersonaNameExistsAsync(Creator, "NIGHTSHIFT RADIO"), Is.True);
            Assert.That(await _service.PersonaNameExistsAsync(Creator, "  Nightshift Radio  "), Is.True);
        });
    }

    [Test]
    public async Task ADisabledPersonaStillCounts()
    {
        // A disabled persona has not gone anywhere - it can be re-enabled, and two rows sharing
        // a name would be indistinguishable the moment it is.
        await SeedAsync("Nightshift Radio", isEnabled: false);

        Assert.That(await _service.PersonaNameExistsAsync(Creator, "Nightshift Radio"), Is.True);
    }

    [Test]
    public async Task AnotherCreatorsPersonaIsNotADuplicate()
    {
        // Uniqueness is per creator, not global. Two unrelated artists may both be "Halo".
        await SeedAsync("Halo", creatorId: OtherCreator);

        Assert.That(await _service.PersonaNameExistsAsync(Creator, "Halo"), Is.False);
    }

    [Test]
    public async Task APersonaDoesNotCollideWithItselfWhenRenamed()
    {
        // Without the exclusion, saving the edit dialog without touching the name would fail.
        var id = await SeedAsync("Nightshift Radio");

        Assert.That(
            await _service.PersonaNameExistsAsync(Creator, "Nightshift Radio", excludePersonaId: id),
            Is.False);
    }

    [Test]
    public async Task ARenameOntoAnotherPersonasNameIsStillADuplicate()
    {
        var renaming = await SeedAsync("Warwick");
        await SeedAsync("Nightshift Radio");

        Assert.That(
            await _service.PersonaNameExistsAsync(Creator, "Nightshift Radio", excludePersonaId: renaming),
            Is.True);
    }

    [Test]
    public async Task AnEmptyNameIsNeverADuplicate()
    {
        // "Name is required" is a different message, raised before this check runs.
        await SeedAsync("Nightshift Radio");

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.PersonaNameExistsAsync(Creator, ""), Is.False);
            Assert.That(await _service.PersonaNameExistsAsync(Creator, "   "), Is.False);
            Assert.That(await _service.PersonaNameExistsAsync(Creator, null!), Is.False);
        });
    }

    [Test]
    public void CreateRefusesADuplicate()
    {
        // The page validates first for a better message, but the service is not only reachable
        // through the page, so it enforces the rule itself.
        Assert.That(async () =>
        {
            await SeedAsync("Nightshift Radio");
            await _service.CreatePersonaAsync(Creator, "  nightshift radio  ", bio: null, websiteUrl: null);
        }, Throws.InstanceOf<InvalidOperationException>()
            .With.Message.Contains("You already have a persona called 'nightshift radio'."));
    }

    [Test]
    public async Task CreateStillAllowsANameNobodyIsUsing()
    {
        await SeedAsync("Nightshift Radio");

        var created = await _service.CreatePersonaAsync(Creator, "Warwick", bio: null, websiteUrl: null);

        Assert.That(created, Is.Not.Null);
        Assert.That(created.Name, Is.EqualTo("Warwick"));
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
