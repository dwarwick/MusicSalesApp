using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

/// <summary>
/// Turning a creator's filename into a comparable name and a seeded song title.
///
/// <para>
/// Shared by the Azure Function's model-driven matching and the web app's exact-name fallback, which
/// is why it is pinned here rather than alongside either. If the two ever normalized differently,
/// the same upload would arrive with a different title depending on which path ran.
/// </para>
/// </summary>
[TestFixture]
public class FileNameMatchingTests
{
    [TestCase("dark_night", "Dark Night")]
    [TestCase("dark-night", "Dark Night")]
    [TestCase("dark_night-sky", "Dark Night Sky")]
    [TestCase("dark night", "Dark Night")]
    [TestCase("dark__night", "Dark Night")]
    [TestCase("thriller", "Thriller")]
    [TestCase("DARK_NIGHT", "Dark Night")]
    [TestCase("", "")]
    [TestCase(null, "")]
    public void NormalizeBaseName_ProducesATitleCasedName(string input, string expected)
        => Assert.That(FileNameMatching.NormalizeBaseName(input), Is.EqualTo(expected));

    [Test]
    public void GetBaseNameWithoutExtension_StripsTheExtension()
        => Assert.That(
            FileNameMatching.GetBaseNameWithoutExtension("dark_night.mp3"),
            Is.EqualTo("dark_night"));

    [Test]
    public void GetBaseNameWithoutExtension_StripsAMasteredSuffix()
    {
        // Mastering tools append this routinely, and creators do not think of it as part of the
        // song's name - so dark_night_mastered.wav has to pair with dark_night.jpg.
        Assert.That(
            FileNameMatching.GetBaseNameWithoutExtension("dark_night_mastered.wav"),
            Is.EqualTo("dark_night"));
    }

    [Test]
    public void GetBaseNameWithoutExtension_MatchesTheMasteredSuffixCaseInsensitively()
        => Assert.That(
            FileNameMatching.GetBaseNameWithoutExtension("Dark_Night_MASTERED.wav"),
            Is.EqualTo("Dark_Night"));

    [Test]
    public void ToNormalizedName_AppliesBothSteps()
        => Assert.That(
            FileNameMatching.ToNormalizedName("dark_night_mastered.wav"),
            Is.EqualTo("Dark Night"));

    [Test]
    public void ToNormalizedName_AgreesAcrossAnAudioAndImagePair()
    {
        // The property the whole fallback rests on: a song and its art normalize to the same string
        // despite different extensions, casing, separators and the mastered suffix.
        Assert.That(
            FileNameMatching.ToNormalizedName("DARK-night_mastered.wav"),
            Is.EqualTo(FileNameMatching.ToNormalizedName("dark_night.jpg")));
    }
}
