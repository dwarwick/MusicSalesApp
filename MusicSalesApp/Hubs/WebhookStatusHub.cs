using Microsoft.AspNetCore.SignalR;

namespace MusicSalesApp.Hubs;

/// <summary>
/// SignalR hub for real-time webhook status updates across connected clients.
/// The hub broadcasts updates when webhook controllers complete processing
/// (e.g., PayPal onboarding, TaxBandits tax form completion).
/// Clients receive updates via the "ReceiveWebhookStatus" message.
/// </summary>
public class WebhookStatusHub : Hub
{
    // This hub primarily serves as a connection point for clients.
    // Webhook status updates are broadcast via IHubContext<WebhookStatusHub> from webhook controllers.
}
