namespace MusicSalesApp.Models;

public class SubscriptionStatusDto
{
    public bool HasSubscription { get; set; }
    public bool IsOnTrial { get; set; }
    public string Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public DateTime? TrialStartDate { get; set; }
    public DateTime? TrialEndDate { get; set; }
    public DateTime? TrialConvertedAt { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string PaypalSubscriptionId { get; set; }
    public string BillingSource { get; set; }
    public bool IsSubscriptionBlocked { get; set; }
    public string SubscriptionPrice { get; set; }
}
