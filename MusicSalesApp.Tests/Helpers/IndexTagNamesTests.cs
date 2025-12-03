using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class IndexTagNamesTests
{
    [Test]
    public void AlbumName_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.AlbumName, Is.EqualTo("AlbumName"));
    }

    [Test]
    public void IsAlbumCover_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.IsAlbumCover, Is.EqualTo("IsAlbumCover"));
    }

    [Test]
    public void Price_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.Price, Is.EqualTo("Price"));
    }
}
