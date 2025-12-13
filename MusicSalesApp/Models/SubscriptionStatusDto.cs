namespace MusicSalesApp.Models;

public class SubscriptionStatusDto
{
    public bool HasSubscription { get; set; }
    public string Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string PaypalSubscriptionId { get; set; }
    public string SubscriptionPrice { get; set; }
}
