namespace MusicSalesApp.Common.Helpers;

public static class MobileTipResultKinds
{
    public const string RequiresApproval = "RequiresApproval";
    public const string Succeeded = "Succeeded";
    public const string Cancelled = "Cancelled";
    public const string ValidationBlocked = "ValidationBlocked";
    public const string FraudPrevented = "FraudPrevented";
    public const string PaymentFailure = "PaymentFailure";
}