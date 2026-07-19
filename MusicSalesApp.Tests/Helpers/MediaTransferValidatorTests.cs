using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class MediaTransferValidatorTests
{
    [Test]
    public void MatchingDeclaredAndReceivedBytes_AreAccepted()
        => Assert.DoesNotThrow(() => MediaTransferValidator.RequireComplete("Song.wav", 100, 100));

    [TestCase(100, 99)]
    [TestCase(100, 101)]
    public void MismatchedDeclaredAndReceivedBytes_AreRejected(long declared, long received)
        => Assert.Throws<InvalidDataException>(() =>
            MediaTransferValidator.RequireComplete("Song.wav", declared, received));
}
