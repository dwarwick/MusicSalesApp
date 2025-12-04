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

    [Test]
    public void AlbumPrice_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.AlbumPrice, Is.EqualTo("AlbumPrice"));
    }

    [Test]
    public void SongPrice_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.SongPrice, Is.EqualTo("SongPrice"));
    }

    [Test]
    public void Genre_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.Genre, Is.EqualTo("Genre"));
    }

    [Test]
    public void TrackNumber_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.TrackNumber, Is.EqualTo("TrackNumber"));
    }

    [Test]
    public void TrackLength_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(IndexTagNames.TrackLength, Is.EqualTo("TrackLength"));
    }
}
