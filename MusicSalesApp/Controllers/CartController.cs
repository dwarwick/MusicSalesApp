using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin,User")]
public class CartController : ControllerBase
{
    private readonly ICartService _cartService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CartController> _logger;

    public CartController(
        ICartService cartService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<CartController> logger)
    {
        _cartService = cartService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetCart()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var items = await _cartService.GetCartItemsAsync(user.Id);
        var total = await _cartService.GetCartTotalAsync(user.Id);

        return Ok(new
        {
            items = items.Select(i => new
            {
                songFileName = i.SongFileName,
                songTitle = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                price = i.Price,
                addedAt = i.AddedAt
            }),
            total
        });
    }

    [HttpGet("count")]
    public async Task<IActionResult> GetCartCount()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var count = await _cartService.GetCartItemCountAsync(user.Id);
        return Ok(new { count });
    }

    [HttpPost("add")]
    public async Task<IActionResult> AddToCart([FromBody] AddToCartRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.SongFileName))
            return BadRequest("Song file name is required");

        var owns = await _cartService.UserOwnsSongAsync(user.Id, request.SongFileName);
        if (owns)
            return BadRequest("You already own this song");

        var item = await _cartService.AddToCartAsync(user.Id, request.SongFileName, request.Price);
        var count = await _cartService.GetCartItemCountAsync(user.Id);

        return Ok(new { success = true, count });
    }

    [HttpPost("remove")]
    public async Task<IActionResult> RemoveFromCart([FromBody] RemoveFromCartRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.SongFileName))
            return BadRequest("Song file name is required");

        var removed = await _cartService.RemoveFromCartAsync(user.Id, request.SongFileName);
        var count = await _cartService.GetCartItemCountAsync(user.Id);

        return Ok(new { success = removed, count });
    }

    [HttpPost("toggle")]
    public async Task<IActionResult> ToggleCart([FromBody] AddToCartRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.SongFileName))
            return BadRequest("Song file name is required");

        var owns = await _cartService.UserOwnsSongAsync(user.Id, request.SongFileName);
        if (owns)
            return BadRequest("You already own this song");

        var inCart = await _cartService.IsInCartAsync(user.Id, request.SongFileName);

        if (inCart)
        {
            await _cartService.RemoveFromCartAsync(user.Id, request.SongFileName);
        }
        else
        {
            await _cartService.AddToCartAsync(user.Id, request.SongFileName, request.Price);
        }

        var count = await _cartService.GetCartItemCountAsync(user.Id);

        return Ok(new { inCart = !inCart, count });
    }

    [HttpGet("status/{*songFileName}")]
    public async Task<IActionResult> GetSongStatus(string songFileName)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(songFileName))
            return BadRequest("Song file name is required");

        var owns = await _cartService.UserOwnsSongAsync(user.Id, songFileName);
        var inCart = await _cartService.IsInCartAsync(user.Id, songFileName);

        return Ok(new { owns, inCart });
    }

    [HttpGet("owned")]
    public async Task<IActionResult> GetOwnedSongs()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var ownedSongs = await _cartService.GetOwnedSongsAsync(user.Id);
        return Ok(ownedSongs);
    }

    [HttpGet("paypal-client-id")]
    [AllowAnonymous]
    public IActionResult GetPayPalClientId()
    {
        var clientId = _configuration["PayPal:ClientId"];
        return Ok(new { clientId });
    }

    [HttpPost("create-order")]
    public async Task<IActionResult> CreatePayPalOrder()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var items = await _cartService.GetCartItemsAsync(user.Id);
        if (!items.Any())
            return BadRequest("Cart is empty");

        var total = await _cartService.GetCartTotalAsync(user.Id);

        // Generate a unique order ID
        var orderId = Guid.NewGuid().ToString();

        // Save the order to database
        await _cartService.CreatePayPalOrderAsync(user.Id, orderId, total);

        return Ok(new
        {
            orderId,
            amount = total.ToString("F2"),
            items = items.Select(i => new
            {
                name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                unit_amount = i.Price.ToString("F2"),
                quantity = 1
            })
        });
    }

    [HttpPost("capture-order")]
    public async Task<IActionResult> CapturePayPalOrder([FromBody] CaptureOrderRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.OrderId))
            return BadRequest("Order ID is required");

        var order = await _cartService.GetPayPalOrderAsync(request.OrderId);
        if (order == null)
            return NotFound("Order not found");

        if (order.UserId != user.Id)
            return Forbid();

        // Get cart items before clearing
        var cartItems = await _cartService.GetCartItemsAsync(user.Id);
        var songFileNames = cartItems.Select(c => c.SongFileName).ToList();

        // Add songs to owned songs
        await _cartService.AddOwnedSongsAsync(user.Id, songFileNames, request.OrderId);

        // Complete the PayPal order
        await _cartService.CompletePayPalOrderAsync(request.OrderId);

        // Clear the cart
        await _cartService.ClearCartAsync(user.Id);

        _logger.LogInformation("User {UserId} completed purchase of {Count} songs", user.Id, songFileNames.Count);

        return Ok(new { success = true, purchasedCount = songFileNames.Count });
    }
}

public class AddToCartRequest
{
    public string SongFileName { get; set; }
    public decimal Price { get; set; } = 0.99m;
}

public class RemoveFromCartRequest
{
    public string SongFileName { get; set; }
}

public class CaptureOrderRequest
{
    public string OrderId { get; set; }
}
