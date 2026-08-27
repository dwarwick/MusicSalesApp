using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The mobile mapper is the compatibility boundary: every app build already installed reads
/// <c>AlbumArtUrl</c> and <c>PersonaImageUrl</c>, so those must keep meaning exactly what they
/// always have while the new rendition URLs are added alongside them.
/// </summary>
[TestFixture]
public class MobileSongMapperVariantTests
{
    private const string Guid32 = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string CoverArt = $"{Guid32}/{Guid32}-coverart.jpg";
    private const string PersonaImage = "creator-12/persona-7.png";

    private Mock<IAzureStorageService> _storage = null!;
    private Mock<ICreatorPersonaService> _personas = null!;
    private MobileSongMapper _mapper = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new Mock<IAzureStorageService>();
        _storage.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns((string path, TimeSpan _) => new Uri($"https://blob.test/{path}?sig=abc"));

        _personas = new Mock<ICreatorPersonaService>();
        _personas.Setup(s => s.GetPersonaImageSasUrl(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns((string path, TimeSpan _) => $"https://persona.test/{path}?sig=abc");

        _mapper = new MobileSongMapper(_storage.Object, _personas.Object, Mock.Of<IHlsStreamUrlFactory>());
    }

    private static SongMetadata Song(string coverWidths = null, string personaWidths = null) => new()
    {
        Id = 1,
        SongTitle = "Night Drive",
        Mp3BlobPath = $"{Guid32}/{Guid32}-music.mp3",
        ImageBlobPath = CoverArt,
        CoverArtVariantWidths = coverWidths,
        CoverArtVariantVersion = 3,
        Persona = new CreatorPersona
        {
            Id = 7,
            Name = "Nova",
            ImageBlobPath = PersonaImage,
            ImageVariantWidths = personaWidths,
            ImageVariantVersion = 2,
            IsEnabled = true
        }
    };

    private SongListItemDto Map(SongMetadata song)
        => _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false));

    [Test]
    public void WithRenditions_PopulatesTheThumbAndHeroUrls()
    {
        var dto = Map(Song(coverWidths: "128,320,640,1024", personaWidths: "128,320,640"));

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtThumbUrl, Does.Contain($"{CoverArt}.w320.webp"));
            Assert.That(dto.AlbumArtHeroUrl, Does.Contain($"{CoverArt}.w640.webp"));
            Assert.That(dto.PersonaImageThumbUrl, Does.Contain($"{PersonaImage}.w320.webp"));
        });
    }

    [Test]
    public void WithoutRenditions_TheVariantUrlsAreNull()
    {
        // Null rather than a guess: the app's chain ends at AlbumArtUrl, so null degrades to
        // today's behaviour while a URL for a blob that was never generated is a broken image.
        var dto = Map(Song());

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtThumbUrl, Is.Null);
            Assert.That(dto.AlbumArtHeroUrl, Is.Null);
            Assert.That(dto.PersonaImageThumbUrl, Is.Null);
        });
    }

    [Test]
    public void ARenditionIsOnlyOfferedWhenThatExactWidthWasGenerated()
    {
        // An 800px cropped master cannot fill the 1024 rung, and a small source may only have 128.
        var dto = Map(Song(coverWidths: "128"));

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtThumbUrl, Is.Null, "320 was not generated for this song");
            Assert.That(dto.AlbumArtHeroUrl, Is.Null, "640 was not generated for this song");
            Assert.That(dto.AlbumArtUrl, Is.Not.Null, "but the full-size master is always available");
        });
    }

    [Test]
    public void AlbumArtUrlIsIdenticalWhetherOrNotRenditionsExist()
    {
        var withRenditions = Map(Song(coverWidths: "128,320,640"));
        var without = Map(Song());

        Assert.Multiple(() =>
        {
            Assert.That(withRenditions.AlbumArtUrl, Is.EqualTo(without.AlbumArtUrl));
            Assert.That(withRenditions.AlbumArtUrl, Does.Contain(CoverArt));
            Assert.That(withRenditions.AlbumArtUrl, Does.Not.Contain(".webp"));
        });
    }

    [Test]
    public void PersonaImageUrlIsIdenticalWhetherOrNotRenditionsExist()
    {
        var withRenditions = Map(Song(personaWidths: "128,320,640"));
        var without = Map(Song());

        Assert.Multiple(() =>
        {
            Assert.That(withRenditions.PersonaImageUrl, Is.EqualTo(without.PersonaImageUrl));
            Assert.That(withRenditions.PersonaImageUrl, Does.Not.Contain(".webp"));
        });
    }

    [Test]
    public void ADisabledPersonaOffersNeitherTheMasterNorARendition()
    {
        var song = Song(personaWidths: "128,320,640");
        song.Persona.IsEnabled = false;

        var dto = Map(song);

        Assert.Multiple(() =>
        {
            Assert.That(dto.PersonaImageUrl, Is.Null);
            Assert.That(dto.PersonaImageThumbUrl, Is.Null);
        });
    }

    [Test]
    public void ASongWithNoCoverArtOffersNoRenditions()
    {
        var song = Song(coverWidths: "128,320,640");
        song.ImageBlobPath = null;

        var dto = Map(song);

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtUrl, Is.Null);
            Assert.That(dto.AlbumArtThumbUrl, Is.Null);
            Assert.That(dto.AlbumArtHeroUrl, Is.Null);
        });
    }

    [Test]
    public void PlaylistSongsCarryTheSameRenditionUrls()
    {
        var dto = _mapper.MapToPlaylistSong(
            Song(coverWidths: "128,320,640", personaWidths: "128,320"),
            TimeSpan.FromHours(24), userPlaylistId: 5, streamQualifying: new StreamQualifyingSettings(30, false));

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtThumbUrl, Does.Contain(".w320.webp"));
            Assert.That(dto.AlbumArtHeroUrl, Does.Contain(".w640.webp"));
            Assert.That(dto.PersonaImageThumbUrl, Does.Contain(".w320.webp"));
            Assert.That(dto.AlbumArtUrl, Does.Not.Contain(".webp"));
        });
    }

    [Test]
    public void TheContentVersionsAreCarriedSoClientsCanBustTheirCaches()
    {
        // Cover art under the GUID naming scheme sits at a fixed path a re-crop overwrites in place.
        // Without these the mobile cache, which keys on the blob path, would serve the pre-crop
        // image indefinitely.
        var dto = Map(Song(coverWidths: "128,320,640", personaWidths: "128,320"));

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtVersion, Is.EqualTo(3));
            Assert.That(dto.PersonaImageVersion, Is.EqualTo(2));
        });
    }

    [Test]
    public void ASongWithNoPersonaReportsVersionZeroRatherThanFailing()
    {
        var song = Song();
        song.Persona = null;

        Assert.That(Map(song).PersonaImageVersion, Is.Zero);
    }

    [Test]
    public void PlaylistSongsCarryTheContentVersionsToo()
    {
        var dto = _mapper.MapToPlaylistSong(
            Song(coverWidths: "128,320,640", personaWidths: "128,320"),
            TimeSpan.FromHours(24), userPlaylistId: 5, streamQualifying: new StreamQualifyingSettings(30, false));

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtVersion, Is.EqualTo(3));
            Assert.That(dto.PersonaImageVersion, Is.EqualTo(2));
        });
    }

    [Test]
    public void TheThumbAndHeroWidthsMatchTheSharedConstants()
    {
        var dto = Map(Song(coverWidths: ImageVariantSizes.ToCsv(ImageVariantSizes.CoverArt)));

        Assert.Multiple(() =>
        {
            Assert.That(dto.AlbumArtThumbUrl, Does.Contain($".w{ImageVariantSizes.MobileThumbWidth}.webp"));
            Assert.That(dto.AlbumArtHeroUrl, Does.Contain($".w{ImageVariantSizes.MobileHeroWidth}.webp"));
        });
    }
}
