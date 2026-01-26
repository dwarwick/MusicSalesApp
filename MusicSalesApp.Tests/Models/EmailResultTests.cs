using MusicSalesApp.Models;

namespace MusicSalesApp.Tests.Models;

[TestFixture]
public class EmailResultTests
{
    [Test]
    public void Succeeded_ReturnsSuccessResult()
    {
        // Act
        var result = EmailResult.Succeeded();

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorType, Is.EqualTo(EmailErrorType.None));
        Assert.That(result.ErrorMessage, Is.Empty);
    }

    [Test]
    public void SpamFilterRejected_ReturnsFailedResult()
    {
        // Act
        var result = EmailResult.SpamFilterRejected();

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo(EmailErrorType.SpamFilterRejected));
        Assert.That(result.ErrorMessage, Does.Contain("spam filter"));
    }

    [Test]
    public void MissingConfiguration_ReturnsFailedResult()
    {
        // Act
        var result = EmailResult.MissingConfiguration();

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo(EmailErrorType.MissingConfiguration));
        Assert.That(result.ErrorMessage, Does.Contain("configured"));
    }

    [Test]
    public void SmtpError_ReturnsFailedResultWithMessage()
    {
        // Arrange
        var errorMessage = "Connection refused";

        // Act
        var result = EmailResult.SmtpError(errorMessage);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo(EmailErrorType.SmtpError));
        Assert.That(result.ErrorMessage, Is.EqualTo(errorMessage));
    }

    [Test]
    public void UnexpectedError_ReturnsFailedResultWithMessage()
    {
        // Arrange
        var errorMessage = "Something went wrong";

        // Act
        var result = EmailResult.UnexpectedError(errorMessage);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo(EmailErrorType.UnexpectedError));
        Assert.That(result.ErrorMessage, Is.EqualTo(errorMessage));
    }

    [Test]
    public void SpamFilterRejected_MessageMentionsContactSupport()
    {
        // Act
        var result = EmailResult.SpamFilterRejected();

        // Assert
        Assert.That(result.ErrorMessage, Does.Contain("contact support"));
    }

    [Test]
    public void MissingConfiguration_MessageMentionsContactSupport()
    {
        // Act
        var result = EmailResult.MissingConfiguration();

        // Assert
        Assert.That(result.ErrorMessage, Does.Contain("contact support"));
    }
}
