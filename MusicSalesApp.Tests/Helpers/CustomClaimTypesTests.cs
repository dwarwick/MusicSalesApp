using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class CustomClaimTypesTests
{
    [Test]
    public void Permission_ShouldHaveCorrectValue()
    {
        // Assert
        Assert.That(CustomClaimTypes.Permission, Is.EqualTo("Permission"));
    }
}
