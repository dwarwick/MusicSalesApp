using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class GooglePlayVerificationServiceTests
{
    [Test]
    public void ResolveCredentialsPath_ReturnsNull_WhenPathMissing()
    {
        var result = GooglePlayVerificationService.ResolveCredentialsPath(null, @"C:\app-root");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveCredentialsPath_ReturnsAbsolutePath_Unchanged()
    {
        var absolutePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "google-play-service-account.json"));

        var result = GooglePlayVerificationService.ResolveCredentialsPath(absolutePath, Path.GetTempPath());

        Assert.That(result, Is.EqualTo(absolutePath));
    }

    [Test]
    public void ResolveCredentialsPath_CombinesRelativePath_WithContentRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "streamtunes-root");
        var relativePath = Path.Combine("App_Data", "Secrets", "google-play-service-account.json");

        var result = GooglePlayVerificationService.ResolveCredentialsPath(relativePath, contentRoot);

        Assert.That(result, Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, relativePath))));
    }

    [Test]
    public void DescribeCredentialConfigurationIssue_ReturnsMissingFileMessage_WhenPathConfiguredButMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "google-play-service-account.json");

        var result = GooglePlayVerificationService.DescribeCredentialConfigurationIssue(missingPath, null);

        Assert.That(result, Is.EqualTo("Configured Google Play service account key file was not found on the server."));
    }

    [Test]
    public void DescribeCredentialConfigurationIssue_ReturnsMissingCredentialsMessage_WhenConfigurationMissing()
    {
        var result = GooglePlayVerificationService.DescribeCredentialConfigurationIssue(null, null);

        Assert.That(result, Is.EqualTo("Google Play service account credentials are not configured on the server."));
    }

    [Test]
    public void DescribeGoogleApiAccessIssue_ReturnsApiDisabledMessage_WhenReasonIsAccessNotConfigured()
    {
        var result = GooglePlayVerificationService.DescribeGoogleApiAccessIssue(
            "accessNotConfigured",
            "Google Play Android Developer API has not been used in project 594088401955 before or it is disabled.");

        Assert.That(result, Is.EqualTo("Google Play Android Developer API is disabled for the Google Cloud project behind the service account. Enable the Android Publisher API in Google Cloud Console, wait a few minutes, and retry."));
    }

    [Test]
    public void DescribeGoogleApiAccessIssue_ReturnsPermissionMessage_ForOtherForbiddenErrors()
    {
        var result = GooglePlayVerificationService.DescribeGoogleApiAccessIssue(
            "insufficientPermissions",
            "The caller does not have permission.");

        Assert.That(result, Is.EqualTo("Google Play API access was denied. Check the service account permissions in Play Console."));
    }
}
