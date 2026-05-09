using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

public interface IAppleAppStoreVerificationService
{
    Task<AppleAppStoreSubscriptionInfo> VerifySubscriptionAsync(string transactionId, string productId);
}

public record AppleAppStoreSubscriptionInfo(
    string Status,
    DateTime? ExpiryTime,
    string TransactionId,
    string OriginalTransactionId,
    string ProductId,
    string Environment,
    string AppAccountToken);

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