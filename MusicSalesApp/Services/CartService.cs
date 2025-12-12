using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class CartService : ICartService
{
    public event Action OnCartUpdated;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<CartService> _logger;

    public CartService(IDbContextFactory<AppDbContext> contextFactory, ILogger<CartService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public void NotifyCartUpdated()
    {
        OnCartUpdated?.Invoke();
    }

    public async Task<IEnumerable<CartItem>> GetCartItemsAsync(int userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.CartItems
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.AddedAt)
            .ToListAsync();
    }

    public async Task<int> GetCartItemCountAsync(int userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.CartItems
            .CountAsync(c => c.UserId == userId);
    }

    public async Task<CartItem> AddToCartAsync(int userId, string songFileName, decimal price)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var existingItem = await context.CartItems
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

        context.CartItems.Add(cartItem);
        await context.SaveChangesAsync();

        NotifyCartUpdated();

        _logger.LogInformation("Added song {SongFileName} to cart for user {UserId}", songFileName, userId);

        return cartItem;
    }

    public async Task<bool> RemoveFromCartAsync(int userId, string songFileName)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var cartItem = await context.CartItems
            .FirstOrDefaultAsync(c => c.UserId == userId && c.SongFileName == songFileName);

        if (cartItem == null)
        {
            return false;
        }

        context.CartItems.Remove(cartItem);
        await context.SaveChangesAsync();
        NotifyCartUpdated();
        _logger.LogInformation("Removed song {SongFileName} from cart for user {UserId}", songFileName, userId);

        return true;
    }

    public async Task<bool> IsInCartAsync(int userId, string songFileName)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.CartItems
            .AnyAsync(c => c.UserId == userId && c.SongFileName == songFileName);
    }

    public async Task ClearCartAsync(int userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var cartItems = await context.CartItems
            .Where(c => c.UserId == userId)
            .ToListAsync();

        context.CartItems.RemoveRange(cartItems);
        await context.SaveChangesAsync();
        NotifyCartUpdated();

        _logger.LogInformation("Cleared cart for user {UserId}", userId);
    }

    public async Task<decimal> GetCartTotalAsync(int userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.CartItems
            .Where(c => c.UserId == userId)
            .SumAsync(c => c.Price);
    }

    public async Task<bool> UserOwnsSongAsync(int userId, string songFileName)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.OwnedSongs
            .AnyAsync(o => o.UserId == userId && o.SongFileName == songFileName);
    }

    public async Task<IEnumerable<string>> GetOwnedSongsAsync(int userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.OwnedSongs
            .Where(o => o.UserId == userId)
            .Select(o => o.SongFileName)
            .ToListAsync();
    }

    public async Task AddOwnedSongsAsync(int userId, IEnumerable<string> songFileNames, string payPalOrderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var songFileNamesList = songFileNames.ToList();
        
        // Fetch all existing owned songs for the user in a single query to avoid N+1
        var existingOwnedSongs = await context.OwnedSongs
            .Where(o => o.UserId == userId && songFileNamesList.Contains(o.SongFileName))
            .Select(o => o.SongFileName)
            .ToHashSetAsync();

        // Fetch SongMetadata for all songs being purchased to populate SongMetadataId
        // We need to match by filename, which could be in Mp3BlobPath or BlobPath
        var songMetadataLookup = await context.SongMetadata
            .Where(sm => songFileNamesList.Any(sfn => 
                (sm.Mp3BlobPath != null && sm.Mp3BlobPath.Contains(sfn)) ||
                (sm.BlobPath != null && sm.BlobPath.Contains(sfn))))
            .ToDictionaryAsync(
                sm => sm.Mp3BlobPath ?? sm.BlobPath,
                sm => sm.Id);

        foreach (var songFileName in songFileNamesList)
        {
            if (!existingOwnedSongs.Contains(songFileName))
            {
                // Try to find the SongMetadataId for this song
                int? songMetadataId = null;
                var matchingMetadata = songMetadataLookup.FirstOrDefault(kvp => 
                    kvp.Key != null && kvp.Key.Contains(songFileName));
                if (matchingMetadata.Key != null)
                {
                    songMetadataId = matchingMetadata.Value;
                }

                var ownedSong = new OwnedSong
                {
                    UserId = userId,
                    SongFileName = songFileName,
                    PurchasedAt = DateTime.UtcNow,
                    PayPalOrderId = payPalOrderId,
                    SongMetadataId = songMetadataId
                };

                context.OwnedSongs.Add(ownedSong);
            }
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("Added {Count} songs to owned songs for user {UserId}", songFileNamesList.Count, userId);
    }

    public async Task<PayPalOrder> CreatePayPalOrderAsync(int userId, string orderId, decimal totalAmount)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var order = new PayPalOrder
        {
            UserId = userId,
            OrderId = orderId,
            TotalAmount = totalAmount,
            Status = "CREATED",
            CreatedAt = DateTime.UtcNow
        };

        context.PayPalOrders.Add(order);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created PayPal order {OrderId} for user {UserId}", orderId, userId);

        return order;
    }

    public async Task<PayPalOrder> GetPayPalOrderAsync(string orderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PayPalOrders
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
    }

    public async Task CompletePayPalOrderAsync(string orderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var order = await context.PayPalOrders
            .FirstOrDefaultAsync(o => o.OrderId == orderId);

        if (order != null)
        {
            order.Status = "COMPLETED";
            order.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation("Completed PayPal order {OrderId}", orderId);
        }
    }
}
