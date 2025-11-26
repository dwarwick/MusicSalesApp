using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class PermissionsTests
{
    [Test]
    public void ManageUsers_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(Permissions.ManageUsers, Is.EqualTo("ManageUsers"));
    }

    [Test]
    public void ValidatedUser_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(Permissions.ValidatedUser, Is.EqualTo("ValidatedUser"));
    }

    [Test]
    public void NonValidatedUser_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(Permissions.NonValidatedUser, Is.EqualTo("NonValidatedUser"));
    }

    [Test]
    public void UploadFiles_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(Permissions.UploadFiles, Is.EqualTo("UploadFiles"));
    }
}
