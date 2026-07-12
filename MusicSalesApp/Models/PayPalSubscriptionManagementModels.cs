#nullable enable

namespace MusicSalesApp.Models;

/// <summary>
/// The exact PayPal web offer that should be shown to, and used for, one account.
/// </summary>
public sealed record PayPalWebOfferQuote
{
    public string? PlanId { get; init; }
    public required string PlanName { get; init; }
    public decimal RegularPrice { get; init; }
    public required string CurrencyCode { get; init; }
    public required string IntervalUnit { get; init; }
    public int IntervalCount { get; init; }
    public int? TrialDays { get; init; }
    public int? SettingsVersion { get; init; }
    public bool IsFirstTimeSubscriber { get; init; }
    public bool IsConfigured { get; init; }

    public bool HasFreeTrial => TrialDays > 0;
}

public sealed record PayPalCheckoutResult(
    bool Success,
    string? ApprovalUrl = null,
    string? Error = null,
    string? SubscriptionId = null)
{
    public static PayPalCheckoutResult Failed(string error) => new(false, Error: error);
}

public sealed record PayPalActivationResult(bool Success, string? Error = null, bool IsTrial = false);

public sealed record PayPalCancellationResult(bool Success, DateTime? EndDate = null, string? Error = null);
