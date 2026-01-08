using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// Cart controller - Individual song purchases have been removed.
/// Users can only access full songs through a subscription.
/// This controller is deprecated but kept for backward compatibility.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Admin,User,Seller")]
public class CartController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CartController> _logger;

    public CartController(
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<CartController> logger)
    {
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Returns an empty cart - individual song purchases have been removed.
    /// </summary>
    [HttpGet]
    public IActionResult GetCart()
    {
        // Individual song purchases have been removed. Return empty cart.
        return Ok(new
        {
            items = Array.Empty<object>(),
            albums = Array.Empty<object>(),
            total = 0m,
            message = "Individual song purchases have been removed. Please subscribe to access full songs."
        });
    }

    [HttpGet("count")]
    public IActionResult GetCartCount()
    {
        return Ok(new { count = 0 });
    }

    [HttpPost("add")]
    public IActionResult AddToCart([FromBody] AddToCartRequest request)
    {
        return BadRequest(new { error = "Individual song purchases have been removed. Please subscribe to access full songs." });
    }

    [HttpPost("remove")]
    public IActionResult RemoveFromCart([FromBody] RemoveFromCartRequest request)
    {
        return BadRequest(new { error = "Individual song purchases have been removed. Please subscribe to access full songs." });
    }

    [HttpPost("toggle")]
    public IActionResult ToggleCart([FromBody] AddToCartRequest request)
    {
        return BadRequest(new { error = "Individual song purchases have been removed. Please subscribe to access full songs." });
    }

    [HttpPost("toggle-album")]
    public IActionResult ToggleAlbumCart([FromBody] ToggleAlbumRequest request)
    {
        return BadRequest(new { error = "Individual album purchases have been removed. Please subscribe to access full songs." });
    }

    [HttpGet("status/{*songFileName}")]
    public IActionResult GetSongStatus(string songFileName)
    {
        // No song ownership - all access is through subscription
        return Ok(new { owns = false, inCart = false });
    }

    [HttpGet("owned")]
    public IActionResult GetOwnedSongs()
    {
        // No song ownership - all access is through subscription
        return Ok(Array.Empty<string>());
    }

    /// <summary>
    /// Returns the PayPal client ID for subscription payments.
    /// This endpoint is still active.
    /// </summary>
    [HttpGet("paypal-client-id")]
    [AllowAnonymous]
    public IActionResult GetPayPalClientId()
    {
        var clientId = _configuration["PayPal:ClientId"];
        return Ok(new { clientId });
    }

    [HttpGet("check-multiparty")]
    public IActionResult CheckMultiParty()
    {
        return Ok(new { isMultiParty = false, message = "Individual song purchases have been removed." });
    }

    [HttpPost("create-order")]
    public IActionResult CreatePayPalOrder()
    {
        return BadRequest(new { error = "Individual song purchases have been removed. Please subscribe to access full songs." });
    }

    [HttpPost("capture-order")]
    public IActionResult CapturePayPalOrder([FromBody] CaptureOrderRequest request)
    {
        return BadRequest(new { error = "Individual song purchases have been removed. Please subscribe to access full songs." });
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
    public bool IsMultiParty { get; set; } = false;
}
