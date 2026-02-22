using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class FileMatchingServiceTests
{
    private Mock<ILogger<FileMatchingService>> _mockLogger;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<FileMatchingService>>();
    }

    private FileMatchingService CreateService(string openAiApiKey = "__REPLACE_WITH_OPENAI_API_KEY__")
    {
        var configData = new Dictionary<string, string>
        {
            ["OpenAI:ApiKey"] = openAiApiKey
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();
        return new FileMatchingService(configuration, _mockLogger.Object);
    }

    #region NormalizeBaseName Tests

    [Test]
    public void NormalizeBaseName_WithUnderscores_ReplacesWithSpaces()
    {
        var result = FileMatchingService.NormalizeBaseName("dark_night");
        Assert.That(result, Is.EqualTo("Dark Night"));
    }

    [Test]
    public void NormalizeBaseName_WithHyphens_ReplacesWithSpaces()
    {
        var result = FileMatchingService.NormalizeBaseName("dark-night");
        Assert.That(result, Is.EqualTo("Dark Night"));
    }

    [Test]
    public void NormalizeBaseName_WithMixedSeparators_NormalizesCorrectly()
    {
        var result = FileMatchingService.NormalizeBaseName("dark_night-sky");
        Assert.That(result, Is.EqualTo("Dark Night Sky"));
    }

    [Test]
    public void NormalizeBaseName_AlreadySpaced_TitleCases()
    {
        var result = FileMatchingService.NormalizeBaseName("dark night");
        Assert.That(result, Is.EqualTo("Dark Night"));
    }

    [Test]
    public void NormalizeBaseName_EmptyString_ReturnsEmpty()
    {
        var result = FileMatchingService.NormalizeBaseName(string.Empty);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void NormalizeBaseName_WithMultipleConsecutiveUnderscores_CollapsesSpaces()
    {
        var result = FileMatchingService.NormalizeBaseName("dark__night");
        Assert.That(result, Is.EqualTo("Dark Night"));
    }

    [Test]
    public void NormalizeBaseName_SingleWord_TitleCases()
    {
        var result = FileMatchingService.NormalizeBaseName("thriller");
        Assert.That(result, Is.EqualTo("Thriller"));
    }

    [Test]
    public void NormalizeBaseName_AllCapsWord_TitleCases()
    {
        var result = FileMatchingService.NormalizeBaseName("DARK_NIGHT");
        Assert.That(result, Is.EqualTo("Dark Night"));
    }

    #endregion

    #region MatchFilesAsync - No OpenAI (fallback) Tests

    [Test]
    public async Task MatchFilesAsync_NoImages_ReturnsAllAudioWithNullImage()
    {
        var service = CreateService();
        var audioFiles = new[] { "dark_night.mp3", "sunny_day.wav" };
        var imageFiles = Array.Empty<string>();

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        Assert.That(result.Pairs, Has.Count.EqualTo(2));
        Assert.That(result.Pairs.All(p => p.ImageFileName == null), Is.True);
        Assert.That(result.UnmatchedImageFiles, Is.Empty);
    }

    [Test]
    public async Task MatchFilesAsync_NoAudio_ReturnsEmptyPairsWithAllImagesUnmatched()
    {
        var service = CreateService();
        var audioFiles = Array.Empty<string>();
        var imageFiles = new[] { "dark_night.jpg" };

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        Assert.That(result.Pairs, Is.Empty);
        Assert.That(result.UnmatchedImageFiles, Has.Count.EqualTo(1));
        Assert.That(result.UnmatchedImageFiles[0], Is.EqualTo("dark_night.jpg"));
    }

    [Test]
    public async Task MatchFilesAsync_ExactMatchFallback_PairsCorrectly()
    {
        var service = CreateService(); // No OpenAI configured
        var audioFiles = new[] { "dark_night.mp3" };
        var imageFiles = new[] { "dark_night.jpg" };

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        Assert.That(result.Pairs, Has.Count.EqualTo(1));
        Assert.That(result.Pairs[0].AudioFileName, Is.EqualTo("dark_night.mp3"));
        Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("dark_night.jpg"));
        Assert.That(result.Pairs[0].NormalizedName, Is.EqualTo("Dark Night"));
        Assert.That(result.UnmatchedImageFiles, Is.Empty);
    }

    [Test]
    public async Task MatchFilesAsync_ExactMatchFallback_CaseInsensitive()
    {
        var service = CreateService();
        var audioFiles = new[] { "DarkNight.mp3" };
        var imageFiles = new[] { "darknight.jpg" };

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        // Fallback uses exact normalized base name match
        // "DarkNight" (after removing extension) vs "darknight" — same when case-insensitive
        Assert.That(result.Pairs, Has.Count.EqualTo(1));
        Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("darknight.jpg"));
    }

    [Test]
    public async Task MatchFilesAsync_MasteredSuffixFallback_StripsAndMatches()
    {
        var service = CreateService();
        var audioFiles = new[] { "dark_night_mastered.mp3" };
        var imageFiles = new[] { "dark_night.jpg" };

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        Assert.That(result.Pairs, Has.Count.EqualTo(1));
        Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("dark_night.jpg"));
        Assert.That(result.Pairs[0].NormalizedName, Is.EqualTo("Dark Night"));
    }

    [Test]
    public async Task MatchFilesAsync_UnmatchedImageFallback_ReturnsInUnmatchedList()
    {
        var service = CreateService();
        var audioFiles = new[] { "dark_night.mp3" };
        var imageFiles = new[] { "dark_night.jpg", "unrelated_cover.png" };

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        Assert.That(result.Pairs, Has.Count.EqualTo(1));
        Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("dark_night.jpg"));
        Assert.That(result.UnmatchedImageFiles, Has.Count.EqualTo(1));
        Assert.That(result.UnmatchedImageFiles[0], Is.EqualTo("unrelated_cover.png"));
    }

    [Test]
    public async Task MatchFilesAsync_AudioWithoutMatchFallback_HasNullImage()
    {
        var service = CreateService();
        var audioFiles = new[] { "dark_night.mp3", "sunny_day.mp3" };
        var imageFiles = new[] { "dark_night.jpg" };

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        Assert.That(result.Pairs, Has.Count.EqualTo(2));
        var unmatchedAudio = result.Pairs.FirstOrDefault(p => p.ImageFileName == null);
        Assert.That(unmatchedAudio, Is.Not.Null);
        Assert.That(unmatchedAudio.AudioFileName, Is.EqualTo("sunny_day.mp3"));
        Assert.That(unmatchedAudio.NormalizedName, Is.EqualTo("Sunny Day"));
    }

    [Test]
    public async Task MatchFilesAsync_NormalizedNameUsesTitleCase_ForAllPairs()
    {
        var service = CreateService();
        var audioFiles = new[] { "my_awesome_song.mp3" };
        var imageFiles = new[] { "my_awesome_song.jpg" };

        var result = await service.MatchFilesAsync(audioFiles, imageFiles);

        Assert.That(result.Pairs[0].NormalizedName, Is.EqualTo("My Awesome Song"));
    }

    #endregion
}
