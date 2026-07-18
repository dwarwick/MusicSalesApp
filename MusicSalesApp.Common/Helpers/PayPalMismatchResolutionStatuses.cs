namespace MusicSalesApp.Common.Helpers;

public static class PayPalMismatchResolutionStatuses
{
    public const string EntitlementRestored = "EntitlementRestored";
    public const string Suspended = "Suspended";
    public const string ActiveWithoutEntitlement = "ActiveWithoutEntitlement";
    public const string AgreementEnded = "AgreementEnded";
    public const string NoAgreement = "NoAgreement";
    public const string TemporarilyUnavailable = "TemporarilyUnavailable";
}
