using System.Text.Json.Serialization;

#nullable enable

namespace MusicSalesApp.Models;

/// <summary>
/// An atomic snapshot of the PayPal plans currently offered by the web app.
/// </summary>
public sealed record PayPalWebSubscriptionOffer
{
    [JsonPropertyName("v")]
    public int Version { get; init; }

    [JsonPropertyName("u")]
    public DateTime UpdatedAtUtc { get; init; }

    [JsonPropertyName("p")]
    public required PayPalWebPlanSnapshot PrimaryPlan { get; init; }

    [JsonPropertyName("r")]
    public PayPalWebPlanSnapshot? ResubscriberPlan { get; init; }
}

/// <summary>
/// The provider-confirmed terms cached for one PayPal plan.
/// </summary>
public sealed record PayPalWebPlanSnapshot
{
    [JsonPropertyName("i")]
    public required string Id { get; init; }

    [JsonPropertyName("n")]
    public required string Name { get; init; }

    [JsonPropertyName("s")]
    public required string Status { get; init; }

    [JsonPropertyName("a")]
    public decimal RegularPrice { get; init; }

    [JsonPropertyName("c")]
    public required string CurrencyCode { get; init; }

    [JsonPropertyName("u")]
    public required string IntervalUnit { get; init; }

    [JsonPropertyName("q")]
    public int IntervalCount { get; init; }

    [JsonPropertyName("t")]
    public int? TrialDays { get; init; }

    [JsonIgnore]
    public bool HasFreeTrial => TrialDays > 0;
}
