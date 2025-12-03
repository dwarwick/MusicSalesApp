using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface ICartService
{
    event Action OnCartUpdated;

    void NotifyCartUpdated();

    Task<IEnumerable<CartItem>> GetCartItemsAsync(int userId);
    Task<int> GetCartItemCountAsync(int userId);
    Task<CartItem> AddToCartAsync(int userId, string songFileName, decimal price);
    Task<bool> RemoveFromCartAsync(int userId, string songFileName);
    Task<bool> IsInCartAsync(int userId, string songFileName);
    Task ClearCartAsync(int userId);
    Task<decimal> GetCartTotalAsync(int userId);
    Task<bool> UserOwnsSongAsync(int userId, string songFileName);
    Task<IEnumerable<string>> GetOwnedSongsAsync(int userId);
    Task AddOwnedSongsAsync(int userId, IEnumerable<string> songFileNames, string payPalOrderId);
    Task<PayPalOrder> CreatePayPalOrderAsync(int userId, string orderId, decimal totalAmount);
    Task<PayPalOrder> GetPayPalOrderAsync(string orderId);
    Task CompletePayPalOrderAsync(string orderId);
}
