using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class MetadataNamesTests
{
    [Test]
    public void AlbumName_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(MetadataNames.AlbumName, Is.EqualTo("AlbumName"));
    }

    [Test]
    public void IsAlbumCover_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(MetadataNames.IsAlbumCover, Is.EqualTo("IsAlbumCover"));
    }

    [Test]
    public void Price_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(MetadataNames.Price, Is.EqualTo("Price"));
    }
}
