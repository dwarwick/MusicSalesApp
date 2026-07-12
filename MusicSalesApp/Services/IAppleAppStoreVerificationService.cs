using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

public interface IAppleAppStoreVerificationService
{
    Task<AppleAppStoreSubscriptionInfo> VerifySubscriptionAsync(string transactionId, string productId);
    AppleAppStoreServerNotificationInfo VerifyServerNotification(string signedPayload);
}

public sealed record AppleAppStoreServerNotificationInfo(
    string NotificationType,
    string Subtype,
    AppleAppStoreServerTransactionInfo Transaction,
    AppleAppStoreServerRenewalInfo Renewal);

public sealed record AppleAppStoreServerTransactionInfo(
    string TransactionId,
    string OriginalTransactionId,
    string ProductId,
    string BundleId,
    string Environment,
    string AppAccountToken,
    long? ExpiresDate,
    long? RevocationDate,
    long? Price,
    string Currency);

public sealed record AppleAppStoreServerRenewalInfo(
    int? AutoRenewStatus,
    long? RenewalPrice,
    string Currency);

public record AppleAppStoreSubscriptionInfo(
    string Status,
    DateTime? ExpiryTime,
    DateTime? PurchaseTime,
    string TransactionId,
    string OriginalTransactionId,
    string ProductId,
    string Environment,
    string AppAccountToken,
    decimal? Price = null,
    string PriceCurrencyCode = null);

public class AppleAppStoreVerificationException : Exception
{
    public AppleAppStoreVerificationException(string message)
        : base(message)
    {
    }

    public AppleAppStoreVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
