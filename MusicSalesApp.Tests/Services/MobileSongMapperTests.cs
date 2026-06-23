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

        var mapped = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), userPlaylistId: 9, defaultStreamQualifyingSeconds: 45);

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

        var mapped = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), userPlaylistId: null, defaultStreamQualifyingSeconds: 45);

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

        var listItem = _mapper.MapToSongListItem(song, TimeSpan.FromHours(24), defaultStreamQualifyingSeconds: 45);
        var playlistItem = _mapper.MapToPlaylistSong(song, TimeSpan.FromHours(24), userPlaylistId: 9, defaultStreamQualifyingSeconds: 45);

        Assert.That(listItem.IsAiGenerated, Is.True);
        Assert.That(listItem.IsAiVocals, Is.True);
        Assert.That(listItem.IsAiLyrics, Is.True);
        Assert.That(playlistItem.IsAiGenerated, Is.True);
        Assert.That(playlistItem.IsAiVocals, Is.True);
        Assert.That(playlistItem.IsAiLyrics, Is.True);
    }
}
