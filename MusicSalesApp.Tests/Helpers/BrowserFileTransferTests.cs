using MusicSalesApp.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class BrowserFileTransferTests
{
    /// <summary>
    /// The message the framework actually produced in production on 2026-09-07, so the test fails
    /// if the match ever stops recognising the real thing.
    /// </summary>
    private const string RealMessage =
        "An error occurred while reading the remote stream: NotReadableError: The requested file "
        + "could not be read, typically due to permission problems that have occurred after a "
        + "reference to a file was acquired.";

    [Test]
    public void IsFileNoLongerReadable_IsTrue_ForTheErrorProductionActuallyThrew()
    {
        Assert.That(
            BrowserFileTransfer.IsFileNoLongerReadable(new InvalidOperationException(RealMessage)),
            Is.True);
    }

    [Test]
    public void IsFileNoLongerReadable_IsFalse_ForAnyOtherInvalidOperationException()
    {
        // The whole point of matching the browser's error name. InvalidOperationException is far
        // too common to downgrade wholesale - doing so would silence real bugs on the upload path.
        Assert.Multiple(() =>
        {
            Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(
                new InvalidOperationException("Sequence contains no elements")), Is.False);
            Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(
                new InvalidOperationException("The stream was already consumed")), Is.False);
        });
    }

    [Test]
    public void IsFileNoLongerReadable_IsFalse_ForOtherExceptionTypesCarryingTheName()
    {
        // The type matters as well as the text: this is what RemoteJSDataStream throws, and
        // widening to any exception mentioning the name would catch unrelated wrapped failures.
        Assert.That(
            BrowserFileTransfer.IsFileNoLongerReadable(new IOException(RealMessage)),
            Is.False);
    }

    [Test]
    public void IsFileNoLongerReadable_IsFalse_ForUnrelatedFailures()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(new IOException("disk full")), Is.False);
            Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(new InvalidDataException()), Is.False);
            Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(new TaskCanceledException()), Is.False);
        });
    }

    [Test]
    public void IsFileNoLongerReadable_IsFalse_ForNull()
    {
        Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(null), Is.False);
    }

    [Test]
    public void IsFileNoLongerReadable_UnwrapsAnAggregate_WhenEveryInnerExceptionQualifies()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException(RealMessage),
            new InvalidOperationException(RealMessage));

        Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(aggregate), Is.True);
    }

    [Test]
    public void IsFileNoLongerReadable_IsFalse_WhenARealFaultTravelsAlongside()
    {
        // One genuine fault in the aggregate has to win, or it hides behind the unreadable file.
        var aggregate = new AggregateException(
            new InvalidOperationException(RealMessage),
            new InvalidOperationException("boom"));

        Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(aggregate), Is.False);
    }

    [Test]
    public void IsFileNoLongerReadable_IsFalse_ForAnEmptyAggregate()
    {
        Assert.That(BrowserFileTransfer.IsFileNoLongerReadable(new AggregateException()), Is.False);
    }

    [Test]
    public void UserMessage_TellsThePersonToSelectTheFileAgain()
    {
        // The remedy is the point: the handle behind the IBrowserFile is dead, so "try again"
        // against the same selection fails identically. Re-selecting is the only way forward.
        Assert.That(BrowserFileTransfer.UserMessage, Does.Contain("select it again"));
    }
}
