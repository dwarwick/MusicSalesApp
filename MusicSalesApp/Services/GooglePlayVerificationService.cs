using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;

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

    public GooglePlayVerificationService(IConfiguration configuration, ILogger<GooglePlayVerificationService> logger)
    {
        _logger = logger;
        _packageName = configuration["GooglePlay:PackageName"]
            ?? throw new InvalidOperationException("GooglePlay:PackageName is not configured.");

        var credentialsPath = configuration["GooglePlay:ServiceAccountKeyPath"];
        GoogleCredential credential = null;

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
                var inlineJson = configuration["GooglePlay:ServiceAccountKeyJson"];
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
            _logger.LogWarning(ex, "Google Play credentials not available. Google Play billing features will be disabled.");
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
            _logger.LogWarning("Google Play Publisher Service not initialized — no valid credentials found.");
        }
    }

    public async Task<GooglePlaySubscriptionInfo> VerifySubscriptionAsync(string purchaseToken, string productId)
    {
        if (_publisherService == null)
        {
            _logger.LogError("Cannot verify Google Play subscription — service not initialized (missing credentials)");
            return null;
        }

        try
        {
            var request = _publisherService.Purchases.Subscriptionsv2.Get(_packageName, purchaseToken);
            var subscription = await request.ExecuteAsync();

            if (subscription == null)
            {
                _logger.LogWarning("Google Play returned null for purchase token verification");
                return null;
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
            _logger.LogWarning("Google Play subscription not found for token (may be invalid or expired)");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Google Play subscription");
            return null;
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
