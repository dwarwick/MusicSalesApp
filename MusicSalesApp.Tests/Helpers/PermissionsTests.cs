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
}
