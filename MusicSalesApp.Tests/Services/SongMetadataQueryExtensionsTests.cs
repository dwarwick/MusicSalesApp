using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SongMetadataQueryExtensionsTests
{
    [Test]
    public void GetProfileCompletenessPercent_NoCoverArt_ReturnsZeroEvenWithEverythingElseComplete()
    {
        var song = new SongMetadata
        {
            ImageBlobPath = null,
            Genre = "Rock",
            PersonaId = 1,
            Persona = new CreatorPersona { IsEnabled = true, Name = "Test", ImageBlobPath = "personas/a.jpg" }
        };

        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(0));
    }

    [Test]
    public void GetProfileCompletenessPercent_CoverArtOnly_Returns25()
    {
        var song = new SongMetadata { ImageBlobPath = "covers/a.jpg" };
        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(25));
    }

    [Test]
    public void GetProfileCompletenessPercent_CoverArtAndGenre_Returns50()
    {
        var song = new SongMetadata { ImageBlobPath = "covers/a.jpg", Genre = "Rock" };
        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(50));
    }

    [Test]
    public void GetProfileCompletenessPercent_CoverArtGenreAndPersonaNameOnly_Returns75()
    {
        var song = new SongMetadata
        {
            ImageBlobPath = "covers/a.jpg",
            Genre = "Rock",
            PersonaId = 1,
            Persona = new CreatorPersona { IsEnabled = true, Name = "Test", ImageBlobPath = null }
        };
        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(75));
    }

    [Test]
    public void GetProfileCompletenessPercent_CoverArtGenreAndPersonaImageOnly_Returns75()
    {
        var song = new SongMetadata
        {
            ImageBlobPath = "covers/a.jpg",
            Genre = "Rock",
            PersonaId = 1,
            Persona = new CreatorPersona { IsEnabled = true, Name = null, ImageBlobPath = "personas/a.jpg" }
        };
        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(75));
    }

    [Test]
    public void GetProfileCompletenessPercent_AllFourFieldsComplete_Returns100()
    {
        var song = new SongMetadata
        {
            ImageBlobPath = "covers/a.jpg",
            Genre = "Rock",
            PersonaId = 1,
            Persona = new CreatorPersona { IsEnabled = true, Name = "Test", ImageBlobPath = "personas/a.jpg" }
        };
        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(100));
    }

    [Test]
    public void GetProfileCompletenessPercent_DisabledPersona_TreatsNameAndImageAsIncomplete()
    {
        var song = new SongMetadata
        {
            ImageBlobPath = "covers/a.jpg",
            Genre = "Rock",
            PersonaId = 1,
            Persona = new CreatorPersona { IsEnabled = false, Name = "Test", ImageBlobPath = "personas/a.jpg" }
        };
        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(50));
    }

    [Test]
    public void GetProfileCompletenessPercent_PersonaIdSetButNavigationNull_TreatsAsNoPersona()
    {
        var song = new SongMetadata
        {
            ImageBlobPath = "covers/a.jpg",
            Genre = "Rock",
            PersonaId = 1,
            Persona = null
        };
        Assert.That(song.GetProfileCompletenessPercent(), Is.EqualTo(50));
    }

    [Test]
    public async Task GetProfileCompletenessPercent_ReturnsOneHundred_ExactlyForSongsWhereHasCompleteProfileMatches()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"SongMetadataQueryExtensionsTests_{Guid.NewGuid()}")
            .Options;
        await using var context = new AppDbContext(options);

        var user = new ApplicationUser { Email = "creator@test.com", UserName = "creator@test.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var creator = new Creator { UserId = user.Id, IsActive = true, PayPalEmail = "pp@test.com" };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();
        var enabledPersona = new CreatorPersona { CreatorId = creator.Id, Name = "Test", ImageBlobPath = "personas/a.jpg", IsEnabled = true };
        var disabledPersona = new CreatorPersona { CreatorId = creator.Id, Name = "Test2", ImageBlobPath = "personas/b.jpg", IsEnabled = false };
        context.CreatorPersonas.AddRange(enabledPersona, disabledPersona);
        await context.SaveChangesAsync();

        context.SongMetadata.AddRange(
            new SongMetadata { Id = 1, Mp3BlobPath = "a.mp3", ImageBlobPath = "covers/a.jpg", Genre = "Rock", PersonaId = enabledPersona.Id }, // complete
            new SongMetadata { Id = 2, Mp3BlobPath = "b.mp3", ImageBlobPath = null, Genre = "Rock", PersonaId = enabledPersona.Id }, // no cover
            new SongMetadata { Id = 3, Mp3BlobPath = "c.mp3", ImageBlobPath = "covers/c.jpg", Genre = null, PersonaId = enabledPersona.Id }, // no genre
            new SongMetadata { Id = 4, Mp3BlobPath = "d.mp3", ImageBlobPath = "covers/d.jpg", Genre = "Rock", PersonaId = null }, // no persona
            new SongMetadata { Id = 5, Mp3BlobPath = "e.mp3", ImageBlobPath = "covers/e.jpg", Genre = "Rock", PersonaId = disabledPersona.Id }); // disabled persona
        await context.SaveChangesAsync();

        var completeProfileIds = await context.SongMetadata
            .Include(s => s.Persona)
            .WhereHasCompleteProfile()
            .Select(s => s.Id)
            .ToListAsync();

        var songs = await context.SongMetadata.Include(s => s.Persona).OrderBy(s => s.Id).ToListAsync();
        var hundredPercentIds = songs.Where(s => s.GetProfileCompletenessPercent() == 100).Select(s => s.Id).ToList();

        Assert.That(hundredPercentIds, Is.EqualTo(completeProfileIds));
        Assert.That(hundredPercentIds, Is.EqualTo(new[] { 1 }));

        context.Database.EnsureDeleted();
    }
}
