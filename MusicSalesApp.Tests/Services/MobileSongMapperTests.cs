using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MobileSongMapperTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<ICreatorPersonaService> _mockCreatorPersonaService;
    private MobileSongMapper _mapper;

    [SetUp]
    public void SetUp()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockCreatorPersonaService = new Mock<ICreatorPersonaService>();
        _mockStorageService
            .Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://storage.blob.core.windows.net/container/file?sig=test"));

        _mapper = new MobileSongMapper(_mockStorageService.Object, _mockCreatorPersonaService.Object);
    }

    [Test]
    public void MapToPlaylistSong_UsesCreatorSpecificStreamThreshold_WhenAvailable()
    {
        var song = new SongMetadata
        {
            Id = 10,
            SongTitle = "Threshold Song",
            Mp3BlobPath = "folder/test.mp3",
            Creator = new Creator { Id = 5, StreamQualifyingSeconds = 65 }
        };

        var mapped = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), userPlaylistId: 9, streamQualifying: new StreamQualifyingSettings(45, false));

        Assert.That(mapped.StreamQualifyingSeconds, Is.EqualTo(65));
        Assert.That(mapped.UserPlaylistId, Is.EqualTo(9));
    }

    [Test]
    public void MapToPlaylistSong_FallsBackToDefaultStreamThreshold_WhenCreatorMissing()
    {
        var song = new SongMetadata
        {
            Id = 10,
            SongTitle = "Threshold Song",
            Mp3BlobPath = "folder/test.mp3"
        };

        var mapped = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), userPlaylistId: null, streamQualifying: new StreamQualifyingSettings(45, false));

        Assert.That(mapped.StreamQualifyingSeconds, Is.EqualTo(45));
    }

    [Test]
    public void MapToSongDtos_IncludesAiDisclosureFields()
    {
        var song = new SongMetadata
        {
            Id = 10,
            SongTitle = "AI Disclosure Song",
            Mp3BlobPath = "folder/test.mp3",
            IsAiGenerated = true,
            IsAiVocals = true,
            IsAiLyrics = true
        };

        var listItem = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), streamQualifying: new StreamQualifyingSettings(45, false));
        var playlistItem = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), userPlaylistId: 9, streamQualifying: new StreamQualifyingSettings(45, false));

        Assert.That(listItem.IsAiGenerated, Is.True);
        Assert.That(listItem.IsAiVocals, Is.True);
        Assert.That(listItem.IsAiLyrics, Is.True);
        Assert.That(playlistItem.IsAiGenerated, Is.True);
        Assert.That(playlistItem.IsAiVocals, Is.True);
        Assert.That(playlistItem.IsAiLyrics, Is.True);
    }

    // ---------------------------------------------------------------------------------------
    // Persona website
    // ---------------------------------------------------------------------------------------

    [Test]
    public void MapToSongListItem_ExposesPersonaWebsite_WhenThePersonaIsEnabled()
    {
        var song = new SongMetadata
        {
            Id = 1,
            Mp3BlobPath = "folder/test.mp3",
            Persona = new CreatorPersona { IsEnabled = true, WebsiteUrl = "example.com/artist" }
        };

        var mapped = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false));

        Assert.That(mapped.PersonaWebsiteUrl, Is.EqualTo("example.com/artist"),
            "Passed through exactly as stored - the creator typed it and nothing normalises it.");
    }

    [Test]
    public void MapToSongListItem_WithholdsPersonaWebsite_WhenThePersonaIsDisabled()
    {
        // Same gate the bio uses: a disabled persona is not shown to listeners at all.
        var song = new SongMetadata
        {
            Id = 1,
            Mp3BlobPath = "folder/test.mp3",
            Persona = new CreatorPersona { IsEnabled = false, WebsiteUrl = "example.com/artist" }
        };

        var mapped = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false));

        Assert.That(mapped.PersonaWebsiteUrl, Is.Null);
    }

    [Test]
    public void MapToSongListItem_HasNoCreatorFallbackForTheWebsite()
    {
        // PersonaBio falls back to Creator.Bio; the website deliberately cannot, because a
        // Creator has no website column at all. Asserted so nobody "fixes" the asymmetry.
        var song = new SongMetadata
        {
            Id = 1,
            Mp3BlobPath = "folder/test.mp3",
            Creator = new Creator { Id = 5, Bio = "A creator bio." }
        };

        var mapped = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false));

        Assert.Multiple(() =>
        {
            Assert.That(mapped.PersonaBio, Is.EqualTo("A creator bio."), "The bio still falls back.");
            Assert.That(mapped.PersonaWebsiteUrl, Is.Null, "The website has nothing to fall back to.");
        });
    }

    // ---------------------------------------------------------------------------------------
    // Lyric timings
    // ---------------------------------------------------------------------------------------

    [Test]
    public void MapToSongListItem_ExposesTimings_OnlyWhenPublished()
    {
        var song = new SongMetadata { Id = 7, Mp3BlobPath = "folder/test.mp3" };
        var lyrics = new SongLyrics
        {
            SongMetadataId = 7,
            Status = SongLyricsStatus.Published,
            TimingsBlobPath = "abc/abc-lyrics.json",
            Version = 4
        };

        var mapped = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false), lyrics);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.LyricsTimingsPath, Is.EqualTo("abc/abc-lyrics.json"));
            Assert.That(mapped.LyricsVersion, Is.EqualTo(4),
                "The version travels with the path - without it a re-publish is invisible behind the immutable cache header.");
        });
    }

    /// <summary>
    /// Every non-published state must look exactly like a song with no lyrics.
    /// </summary>
    /// <remarks>
    /// NeedsReview is the important one and the easiest to get wrong: since alignment stopped
    /// publishing, it is where EVERY successful run lands, and its timings sit at exactly the
    /// blob path a published song would use. Shipping the path would both 404 on the client and
    /// tell it that a withheld alignment exists.
    /// </remarks>
    [TestCase(SongLyricsStatus.NeedsReview)]
    [TestCase(SongLyricsStatus.Pending)]
    [TestCase(SongLyricsStatus.Failed)]
    public void MapToSongListItem_WithholdsTimings_ForEveryOtherStatus(SongLyricsStatus status)
    {
        var song = new SongMetadata { Id = 7, Mp3BlobPath = "folder/test.mp3" };
        var lyrics = new SongLyrics
        {
            SongMetadataId = 7,
            Status = status,
            TimingsBlobPath = "abc/abc-lyrics.json",
            Version = 4
        };

        var mapped = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false), lyrics);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.LyricsTimingsPath, Is.Null);
            Assert.That(mapped.LyricsVersion, Is.Zero, "Zero whenever there is no path, so the two cannot disagree.");
        });
    }

    /// <summary>
    /// An administrator's takedown reaches the phone, even though Status still says Published.
    /// </summary>
    /// <remarks>
    /// This mapping IS the propagation. The apps have no notion of "disabled" and need none: they
    /// ask for nothing they were not given a path for, so a null here is what puts the cover art
    /// back on a phone that already knows this song - no release, no client change.
    /// </remarks>
    [Test]
    public void MapToSongListItem_WithholdsTimings_WhenAnAdministratorHasDisabledThem()
    {
        var song = new SongMetadata { Id = 7, Mp3BlobPath = "folder/test.mp3" };
        var lyrics = new SongLyrics
        {
            SongMetadataId = 7,
            Status = SongLyricsStatus.Published,
            TimingsBlobPath = "abc/abc-lyrics.json",
            Version = 4,
            DisabledAt = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc),
            DisabledByUserId = 99
        };

        var listItem = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false), lyrics);
        var playlistItem = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), null, new StreamQualifyingSettings(30, false), lyrics);

        Assert.Multiple(() =>
        {
            Assert.That(listItem.LyricsTimingsPath, Is.Null);
            Assert.That(listItem.LyricsVersion, Is.Zero);
            Assert.That(playlistItem.LyricsTimingsPath, Is.Null, "Both entry points, or one leaks it.");
            Assert.That(playlistItem.LyricsVersion, Is.Zero);
        });
    }

    [Test]
    public void MapToSongListItem_WithholdsTimings_WhenPublishedButThePathIsMissing()
    {
        // Defensive: a row could reach Published with its path cleared. Shipping an empty path
        // would have the client build a request for api/music/?v=1.
        var song = new SongMetadata { Id = 7, Mp3BlobPath = "folder/test.mp3" };
        var lyrics = new SongLyrics
        {
            SongMetadataId = 7,
            Status = SongLyricsStatus.Published,
            TimingsBlobPath = null,
            Version = 4
        };

        var mapped = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false), lyrics);

        Assert.That(mapped.LyricsTimingsPath, Is.Null);
    }

    [Test]
    public void MapToSongListItem_HasNoTimings_WhenNoLyricsRowIsPassed()
    {
        // The overwhelmingly common call: most callers have no reason to load lyrics at all.
        var song = new SongMetadata { Id = 7, Mp3BlobPath = "folder/test.mp3" };

        var mapped = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), new StreamQualifyingSettings(30, false));

        Assert.Multiple(() =>
        {
            Assert.That(mapped.LyricsTimingsPath, Is.Null);
            Assert.That(mapped.LyricsVersion, Is.Zero);
        });
    }

    [Test]
    public void MapToPlaylistSong_CarriesTimingsAndWebsiteToo()
    {
        // The playlist player needs both just as much as the song player; the two projections
        // drifting apart is the failure this guards.
        var song = new SongMetadata
        {
            Id = 7,
            Mp3BlobPath = "folder/test.mp3",
            Persona = new CreatorPersona { IsEnabled = true, WebsiteUrl = "example.com/artist" }
        };
        var lyrics = new SongLyrics
        {
            SongMetadataId = 7,
            Status = SongLyricsStatus.Published,
            TimingsBlobPath = "abc/abc-lyrics.json",
            Version = 2
        };

        var mapped = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), userPlaylistId: null, new StreamQualifyingSettings(30, false), lyrics);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.PersonaWebsiteUrl, Is.EqualTo("example.com/artist"));
            Assert.That(mapped.LyricsTimingsPath, Is.EqualTo("abc/abc-lyrics.json"));
            Assert.That(mapped.LyricsVersion, Is.EqualTo(2));
        });
    }
}
