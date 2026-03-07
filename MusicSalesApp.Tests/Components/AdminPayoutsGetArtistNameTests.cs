using MusicSalesApp.Components.Pages;
using MusicSalesApp.Models;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class AdminPayoutsGetArtistNameTests
{
    [Test]
    public void GetArtistName_PrioritizesSongMetadataArtistName()
    {
        // Arrange
        var creator = new Creator
        {
            Id = 1, DisplayName = "Creator Display Name",
            User = new ApplicationUser { Email = "creator@example.com" }
        };
        var metadata = new SongMetadata { ArtistName = "Song Artist Name" };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, metadata, 1);

        // Assert - should use SongMetadata.ArtistName as priority 1
        Assert.That(result, Is.EqualTo("Song Artist Name"));
    }

    [Test]
    public void GetArtistName_FallsBackToCreatorDisplayName()
    {
        // Arrange
        var creator = new Creator
        {
            Id = 1, DisplayName = "Creator Display Name",
            User = new ApplicationUser { Email = "creator@example.com" }
        };
        var metadata = new SongMetadata { ArtistName = null };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, metadata, 1);

        // Assert - should use Creator.DisplayName as priority 2
        Assert.That(result, Is.EqualTo("Creator Display Name"));
    }

    [Test]
    public void GetArtistName_FallsBackToEmailUsername()
    {
        // Arrange
        var creator = new Creator
        {
            Id = 1, DisplayName = null,
            User = new ApplicationUser { Email = "myartist@example.com" }
        };
        var metadata = new SongMetadata();

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, metadata, 1);

        // Assert - should use email username as priority 3
        Assert.That(result, Is.EqualTo("myartist"));
    }

    [Test]
    public void GetArtistName_FallsBackToCreatorId()
    {
        // Arrange - no useful data available
        var creator = new Creator { Id = 42, DisplayName = null, User = null };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, null, 42);

        // Assert - should use fallback "Creator #id"
        Assert.That(result, Is.EqualTo("Creator #42"));
    }

    [Test]
    public void GetArtistName_HandlesNullCreator()
    {
        // Act
        var result = AdminPayoutsModel.GetArtistName(null, null, 99);

        // Assert
        Assert.That(result, Is.EqualTo("Creator #99"));
    }

    [Test]
    public void GetArtistName_HandlesEmptyStringDisplayName()
    {
        // Arrange - empty string DisplayName (the original bug: "" != null was true)
        var creator = new Creator
        {
            Id = 1, DisplayName = "",
            User = new ApplicationUser { Email = "fallback@example.com" }
        };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, null, 1);

        // Assert - should NOT return empty string, should fall back to email username
        Assert.That(result, Is.EqualTo("fallback"));
    }

    [Test]
    public void GetArtistName_HandlesWhitespaceDisplayName()
    {
        // Arrange
        var creator = new Creator
        {
            Id = 1, DisplayName = "   ",
            User = new ApplicationUser { Email = "user@example.com" }
        };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, null, 1);

        // Assert - whitespace should fall through to email
        Assert.That(result, Is.EqualTo("user"));
    }

    [Test]
    public void GetArtistName_HandlesEmptyStringArtistName()
    {
        // Arrange
        var creator = new Creator
        {
            Id = 1, DisplayName = "Good Display Name",
            User = new ApplicationUser { Email = "user@example.com" }
        };
        var metadata = new SongMetadata { ArtistName = "" };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, metadata, 1);

        // Assert - empty ArtistName should fall through to DisplayName
        Assert.That(result, Is.EqualTo("Good Display Name"));
    }

    [Test]
    public void GetArtistName_HandlesWhitespaceArtistName()
    {
        // Arrange
        var creator = new Creator
        {
            Id = 1, DisplayName = "Creator Name",
            User = new ApplicationUser { Email = "user@example.com" }
        };
        var metadata = new SongMetadata { ArtistName = "   " };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, metadata, 1);

        // Assert - whitespace ArtistName should fall through to DisplayName
        Assert.That(result, Is.EqualTo("Creator Name"));
    }

    [Test]
    public void GetArtistName_HandlesNullUserOnCreator()
    {
        // Arrange - creator exists but has no User navigation property loaded
        var creator = new Creator { Id = 5, DisplayName = null, User = null };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, null, 5);

        // Assert - should fall back to Creator #id
        Assert.That(result, Is.EqualTo("Creator #5"));
    }

    [Test]
    public void GetArtistName_HandlesNullEmailOnUser()
    {
        // Arrange
        var creator = new Creator
        {
            Id = 3, DisplayName = null,
            User = new ApplicationUser { Email = null }
        };

        // Act
        var result = AdminPayoutsModel.GetArtistName(creator, null, 3);

        // Assert
        Assert.That(result, Is.EqualTo("Creator #3"));
    }
}
