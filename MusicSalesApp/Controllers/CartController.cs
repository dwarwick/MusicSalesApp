using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
    private readonly IHttpClientFactory _httpClientFactory;

    public CartController(
        ICartService cartService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<CartController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _cartService = cartService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
            albums = Array.Empty<object>(), // Albums are stored as individual tracks
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

        var item = await _cartService.AddToCartAsync(user.Id, request.SongFileName, request.Price, request.SongMetadataId);
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
            await _cartService.AddToCartAsync(user.Id, request.SongFileName, request.Price, request.SongMetadataId);
        }

        var count = await _cartService.GetCartItemCountAsync(user.Id);

        return Ok(new { inCart = !inCart, count });
    }

    [HttpPost("toggle-album")]
    public async Task<IActionResult> ToggleAlbumCart([FromBody] ToggleAlbumRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.AlbumName))
            return BadRequest("Album name is required");

        if (request.TrackFileNames == null || !request.TrackFileNames.Any())
            return BadRequest("Track file names are required");

        // Check if user already owns all tracks in the album
        var ownedSongs = await _cartService.GetOwnedSongsAsync(user.Id);
        var ownedSet = new HashSet<string>(ownedSongs);
        var ownsAll = request.TrackFileNames.All(t => ownedSet.Contains(t));
        if (ownsAll)
            return BadRequest("You already own this album");

        // Check if album is currently in cart (all tracks are in cart)
        var cartItems = await _cartService.GetCartItemsAsync(user.Id);
        var cartItemSet = new HashSet<string>(cartItems.Select(c => c.SongFileName));
        var inCart = request.TrackFileNames.All(t => cartItemSet.Contains(t));

        if (inCart)
        {
            // Remove all album tracks from cart
            foreach (var trackFileName in request.TrackFileNames)
            {
                await _cartService.RemoveFromCartAsync(user.Id, trackFileName);
            }
        }
        else
        {
            // Add all album tracks to cart (skip tracks that are already owned)
            var trackCount = request.TrackFileNames.Count();
            var pricePerTrack = trackCount > 0 ? request.Price / trackCount : 0m;
            foreach (var trackFileName in request.TrackFileNames)
            {
                if (!ownedSet.Contains(trackFileName) && !cartItemSet.Contains(trackFileName))
                {
                    int? metadataId = null;
                    if (request.TrackMetadataIds != null && request.TrackMetadataIds.TryGetValue(trackFileName, out var id))
                    {
                        metadataId = id;
                    }
                    await _cartService.AddToCartAsync(user.Id, trackFileName, pricePerTrack, metadataId);
                }
            }
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
        if (string.IsNullOrWhiteSpace(request.PayPalOrderId))
            return BadRequest("PayPal order ID is required");

        var order = await _cartService.GetPayPalOrderAsync(request.OrderId);
        if (order == null)
            return NotFound("Order not found");

        if (order.UserId != user.Id)
            return Forbid();

        // Verify the capture was successful (already captured on client side with 3D Secure support)
        var verified = await VerifyPayPalCaptureAsync(request.PayPalOrderId);
        if (!verified)
        {
            _logger.LogWarning("PayPal capture verification failed for PayPalOrderId {PayPalOrderId} and internal order {OrderId}", request.PayPalOrderId, request.OrderId);
            return BadRequest("Failed to verify PayPal order capture. Payment may not have been completed.");
        }

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

    private async Task<bool> VerifyPayPalCaptureAsync(string payPalOrderId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Unable to retrieve PayPal access token.");
                return false;
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Get the order details to verify it was captured
            var response = await client.GetAsync($"v2/checkout/orders/{payPalOrderId}");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal order verification failed: {Status} {Body}", response.StatusCode, body);
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();
            var isCompleted = string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
            
            if (!isCompleted)
            {
                _logger.LogWarning("PayPal order status is {Status} for order {OrderId}, expected COMPLETED", status, payPalOrderId);
            }
            
            return isCompleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying PayPal order {OrderId}", payPalOrderId);
            return false;
        }
    }

    private async Task<string> GetPayPalAccessTokenAsync()
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) || clientId.Contains("REPLACE") || secret.Contains("REPLACE"))
        {
            _logger.LogError("PayPal ClientId/Secret not configured");
            return string.Empty;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);

            var authBytes = Encoding.ASCII.GetBytes($"{clientId}:{secret}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
            var response = await client.PostAsync("v1/oauth2/token", content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal token request failed: {Status} {Body}", response.StatusCode, body);
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PayPal access token");
            return string.Empty;
        }
    }
}

public class AddToCartRequest
{
    public string SongFileName { get; set; }
    public decimal Price { get; set; } = 0.99m;
    public int? SongMetadataId { get; set; }
}

public class RemoveFromCartRequest
{
    public string SongFileName { get; set; }
}

public class ToggleAlbumRequest
{
    public string AlbumName { get; set; }
    public IEnumerable<string> TrackFileNames { get; set; }
    public decimal Price { get; set; } = 9.99m;
    public Dictionary<string, int> TrackMetadataIds { get; set; }
}

public class CaptureOrderRequest
{
    public string OrderId { get; set; }
    public string PayPalOrderId { get; set; }
}
