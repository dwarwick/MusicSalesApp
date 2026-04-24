using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Hosting;

namespace MusicSalesApp.Services;

/// <summary>
/// Verifies Google Play subscription purchases using the Google Play Developer API (v3).
/// Requires a service account with "Manage orders and subscriptions" permission.
/// </summary>
public class GooglePlayVerificationService : IGooglePlayVerificationService, IDisposable
{
    private readonly AndroidPublisherService _publisherService;
    private readonly string _packageName;
    private readonly ILogger<GooglePlayVerificationService> _logger;
    private readonly string _initializationError;

    internal static string ResolveCredentialsPath(string configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    internal static string DescribeCredentialConfigurationIssue(string credentialsPath, string inlineJson)
    {
        if (!string.IsNullOrWhiteSpace(credentialsPath) && !File.Exists(credentialsPath))
        {
            return "Configured Google Play service account key file was not found on the server.";
        }

        if (string.IsNullOrWhiteSpace(credentialsPath) && string.IsNullOrWhiteSpace(inlineJson))
        {
            return "Google Play service account credentials are not configured on the server.";
        }

        return "Google Play service account credentials could not be loaded on the server.";
    }

    internal static string DescribeGoogleApiAccessIssue(string reason, string message)
    {
        if (string.Equals(reason, "accessNotConfigured", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(message) && message.Contains("has not been used in project", StringComparison.OrdinalIgnoreCase)))
        {
            return "Google Play Android Developer API is disabled for the Google Cloud project behind the service account. Enable the Android Publisher API in Google Cloud Console, wait a few minutes, and retry.";
        }

        return "Google Play API access was denied. Check the service account permissions in Play Console.";
    }

    public GooglePlayVerificationService(IConfiguration configuration, IHostEnvironment environment, ILogger<GooglePlayVerificationService> logger)
    {
        _logger = logger;
        _packageName = configuration["GooglePlay:PackageName"]
            ?? throw new InvalidOperationException("GooglePlay:PackageName is not configured.");

        var credentialsPath = ResolveCredentialsPath(configuration["GooglePlay:ServiceAccountKeyPath"], environment.ContentRootPath);
        var inlineJson = configuration["GooglePlay:ServiceAccountKeyJson"];
        GoogleCredential credential = null;
        _initializationError = DescribeCredentialConfigurationIssue(credentialsPath, inlineJson);

        try
        {
            if (!string.IsNullOrEmpty(credentialsPath) && File.Exists(credentialsPath))
            {
#pragma warning disable CS0618
                using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);
                credential = GoogleCredential.FromStream(stream)
                    .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
#pragma warning restore CS0618
            }
            else
            {
                if (!string.IsNullOrEmpty(inlineJson))
                {
#pragma warning disable CS0618
                    credential = GoogleCredential.FromJson(inlineJson)
                        .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
#pragma warning restore CS0618
                }
                else
                {
                    credential = GoogleCredential.GetApplicationDefault()
                        .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Play credentials not available. {InitializationError}", _initializationError);
        }

        if (credential != null)
        {
            _publisherService = new AndroidPublisherService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "StreamTunes Server"
            });
        }
        else
        {
            _logger.LogWarning("Google Play Publisher Service not initialized — {InitializationError}", _initializationError);
        }
    }

    public async Task<GooglePlaySubscriptionInfo> VerifySubscriptionAsync(string purchaseToken, string productId)
    {
        if (_publisherService == null)
        {
            _logger.LogError("Cannot verify Google Play subscription — service not initialized. {InitializationError}", _initializationError);
            throw new GooglePlayVerificationException(_initializationError ?? "Google Play verification is not configured on the server.");
        }

        try
        {
            var request = _publisherService.Purchases.Subscriptionsv2.Get(_packageName, purchaseToken);
            var subscription = await request.ExecuteAsync();

            if (subscription == null)
            {
                _logger.LogWarning("Google Play returned null for purchase token verification");
                throw new GooglePlayVerificationException("Google Play returned an empty verification response for this purchase.");
            }

            // Parse expiry time from the line items
            DateTimeOffset? expiryTime = null;
            if (subscription.LineItems?.Count > 0)
            {
                var dto = subscription.LineItems[0].ExpiryTimeDateTimeOffset;
                if (dto.HasValue)
                {
                    expiryTime = dto.Value;
                }
            }

            return new GooglePlaySubscriptionInfo(
                SubscriptionState: subscription.SubscriptionState ?? "UNKNOWN",
                ExpiryTime: expiryTime,
                OrderId: subscription.LatestOrderId,
                IsAcknowledged: subscription.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
                LinkedPurchaseToken: subscription.LinkedPurchaseToken);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Google Play subscription not found for token (may be invalid or expired)");
            throw new GooglePlayVerificationException("Google Play could not find this purchase token for the configured app.", ex);
        }
        catch (Google.GoogleApiException ex) when (
            ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden ||
            ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogError(ex, "Google Play API access was denied during subscription verification");
            var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason;
            throw new GooglePlayVerificationException(DescribeGoogleApiAccessIssue(reason, ex.Message), ex);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            _logger.LogError(ex, "Google Play rejected the purchase token or product configuration");
            throw new GooglePlayVerificationException("Google Play rejected the purchase token or product configuration.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Google Play subscription");
            throw new GooglePlayVerificationException("Google Play verification failed on the server.", ex);
        }
    }

    public async Task<bool> AcknowledgeSubscriptionAsync(string purchaseToken, string productId)
    {
        if (_publisherService == null)
        {
            _logger.LogError("Cannot acknowledge Google Play subscription — service not initialized (missing credentials)");
            return false;
        }

        try
        {
            // Use the v1 subscriptions API which has the Acknowledge endpoint
            var request = _publisherService.Purchases.Subscriptions.Acknowledge(
                new Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchasesAcknowledgeRequest(),
                _packageName,
                productId,
                purchaseToken);
            await request.ExecuteAsync();

            _logger.LogInformation("Acknowledged Google Play subscription purchase");
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Already acknowledged — not an error
            _logger.LogInformation("Google Play subscription was already acknowledged");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acknowledge Google Play subscription");
            return false;
        }
    }

    public async Task<bool> CancelSubscriptionAsync(string purchaseToken, string productId)
    {
        if (_publisherService == null)
        {
            _logger.LogError("Cannot cancel Google Play subscription — service not initialized (missing credentials)");
            return false;
        }

        try
        {
            var request = _publisherService.Purchases.Subscriptions.Cancel(
                _packageName,
                productId,
                purchaseToken);
            await request.ExecuteAsync();

            _logger.LogInformation("Cancelled Google Play subscription for product {ProductId}", productId);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Already cancelled or expired — not an error
            _logger.LogInformation("Google Play subscription already cancelled or expired");
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Google Play subscription not found — treating as already cancelled");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel Google Play subscription");
            return false;
        }
    }

    public void Dispose()
    {
        _publisherService?.Dispose();
    }
}
