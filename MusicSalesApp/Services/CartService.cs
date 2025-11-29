using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class CartService : ICartService
{
    private readonly AppDbContext _context;
    private readonly ILogger<CartService> _logger;

    public CartService(AppDbContext context, ILogger<CartService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<CartItem>> GetCartItemsAsync(int userId)
    {
        return await _context.CartItems
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.AddedAt)
            .ToListAsync();
    }

    public async Task<int> GetCartItemCountAsync(int userId)
    {
        return await _context.CartItems
            .CountAsync(c => c.UserId == userId);
    }

    public async Task<CartItem> AddToCartAsync(int userId, string songFileName, decimal price)
    {
        var existingItem = await _context.CartItems
            .FirstOrDefaultAsync(c => c.UserId == userId && c.SongFileName == songFileName);

        if (existingItem != null)
        {
            return existingItem;
        }

        var cartItem = new CartItem
        {
            UserId = userId,
            SongFileName = songFileName,
            Price = price,
            AddedAt = DateTime.UtcNow
        };

        _context.CartItems.Add(cartItem);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Added song {SongFileName} to cart for user {UserId}", songFileName, userId);

        return cartItem;
    }

    public async Task<bool> RemoveFromCartAsync(int userId, string songFileName)
    {
        var cartItem = await _context.CartItems
            .FirstOrDefaultAsync(c => c.UserId == userId && c.SongFileName == songFileName);

        if (cartItem == null)
        {
            return false;
        }

        _context.CartItems.Remove(cartItem);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Removed song {SongFileName} from cart for user {UserId}", songFileName, userId);

        return true;
    }

    public async Task<bool> IsInCartAsync(int userId, string songFileName)
    {
        return await _context.CartItems
            .AnyAsync(c => c.UserId == userId && c.SongFileName == songFileName);
    }

    public async Task ClearCartAsync(int userId)
    {
        var cartItems = await _context.CartItems
            .Where(c => c.UserId == userId)
            .ToListAsync();

        _context.CartItems.RemoveRange(cartItems);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Cleared cart for user {UserId}", userId);
    }

    public async Task<decimal> GetCartTotalAsync(int userId)
    {
        return await _context.CartItems
            .Where(c => c.UserId == userId)
            .SumAsync(c => c.Price);
    }

    public async Task<bool> UserOwnsSongAsync(int userId, string songFileName)
    {
        return await _context.OwnedSongs
            .AnyAsync(o => o.UserId == userId && o.SongFileName == songFileName);
    }

    public async Task<IEnumerable<string>> GetOwnedSongsAsync(int userId)
    {
        return await _context.OwnedSongs
            .Where(o => o.UserId == userId)
            .Select(o => o.SongFileName)
            .ToListAsync();
    }

    public async Task AddOwnedSongsAsync(int userId, IEnumerable<string> songFileNames, string payPalOrderId)
    {
        var songFileNamesList = songFileNames.ToList();
        
        // Fetch all existing owned songs for the user in a single query to avoid N+1
        var existingOwnedSongs = await _context.OwnedSongs
            .Where(o => o.UserId == userId && songFileNamesList.Contains(o.SongFileName))
            .Select(o => o.SongFileName)
            .ToHashSetAsync();

        foreach (var songFileName in songFileNamesList)
        {
            if (!existingOwnedSongs.Contains(songFileName))
            {
                var ownedSong = new OwnedSong
                {
                    UserId = userId,
                    SongFileName = songFileName,
                    PurchasedAt = DateTime.UtcNow,
                    PayPalOrderId = payPalOrderId
                };

                _context.OwnedSongs.Add(ownedSong);
            }
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Added {Count} songs to owned songs for user {UserId}", songFileNamesList.Count, userId);
    }

    public async Task<PayPalOrder> CreatePayPalOrderAsync(int userId, string orderId, decimal totalAmount)
    {
        var order = new PayPalOrder
        {
            UserId = userId,
            OrderId = orderId,
            TotalAmount = totalAmount,
            Status = "CREATED",
            CreatedAt = DateTime.UtcNow
        };

        _context.PayPalOrders.Add(order);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created PayPal order {OrderId} for user {UserId}", orderId, userId);

        return order;
    }

    public async Task<PayPalOrder> GetPayPalOrderAsync(string orderId)
    {
        return await _context.PayPalOrders
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
    }

    public async Task CompletePayPalOrderAsync(string orderId)
    {
        var order = await _context.PayPalOrders
            .FirstOrDefaultAsync(o => o.OrderId == orderId);

        if (order != null)
        {
            order.Status = "COMPLETED";
            order.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Completed PayPal order {OrderId}", orderId);
        }
    }
}
