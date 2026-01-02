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
[Authorize(Roles = "Admin,User,Seller")]
public class CartController : ControllerBase
{
    private readonly ICartService _cartService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CartController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPurchaseEmailService _purchaseEmailService;
    private readonly IPayPalPartnerService _payPalPartnerService;
    private readonly ISellerService _sellerService;

    public CartController(
        ICartService cartService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger<CartController> logger,
        IHttpClientFactory httpClientFactory,
        IPurchaseEmailService purchaseEmailService,
        IPayPalPartnerService payPalPartnerService,
        ISellerService sellerService)
    {
        _cartService = cartService;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _purchaseEmailService = purchaseEmailService;
        _payPalPartnerService = payPalPartnerService;
        _sellerService = sellerService;
    }

    [HttpGet]
    public async Task<IActionResult> GetCart()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var items = await _cartService.GetCartItemsWithMetadataAsync(user.Id);
        var total = await _cartService.GetCartTotalAsync(user.Id);

        return Ok(new
        {
            items = items.Select(i => new
            {
                songFileName = i.SongFileName,
                songTitle = GetSongTitle(i),
                price = i.Price,
                addedAt = i.AddedAt
            }),
            albums = Array.Empty<object>(), // Albums are stored as individual tracks
            total
        });
    }
    
    /// <summary>
    /// Gets the song title from CartItemWithMetadata.
    /// Prefers stored SongTitle, falls back to extracting from file name.
    /// </summary>
    private static string GetSongTitle(CartItemWithMetadata item)
    {
        // Prefer stored SongTitle from metadata
        if (!string.IsNullOrEmpty(item.SongMetadata?.SongTitle))
        {
            return item.SongMetadata.SongTitle;
        }
        
        // Fall back to extracting from file name
        return Path.GetFileNameWithoutExtension(Path.GetFileName(item.SongFileName));
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

    [HttpGet("check-multiparty")]
    public async Task<IActionResult> CheckMultiParty()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var cartItemsWithMetadata = (await _cartService.GetCartItemsWithMetadataAsync(user.Id)).ToList();
        if (!cartItemsWithMetadata.Any())
            return Ok(new { isMultiParty = false });

        // Group items by seller (null = platform content)
        var itemsBySeller = cartItemsWithMetadata
            .GroupBy(c => c.SongMetadata?.SellerId)
            .ToList();

        // Check if there's any seller content
        var hasSellerContent = itemsBySeller.Any(g => g.Key != null);
        var hasPlatformContent = itemsBySeller.Any(g => g.Key == null);

        // Multi-party payment is used when there is ONLY seller content (no platform content)
        // Supports up to 10 sellers
        if (hasSellerContent && !hasPlatformContent)
        {
            var sellerGroups = itemsBySeller.Where(g => g.Key != null).ToList();
            
            if (sellerGroups.Count > 10)
            {
                return Ok(new { isMultiParty = false }); // Too many sellers
            }

            var sellerMerchantIds = new List<string>();
            
            foreach (var sellerGroup in sellerGroups)
            {
                var sellerId = sellerGroup.Key!.Value;
                var seller = await _sellerService.GetSellerByIdAsync(sellerId);
                
                if (seller == null || !seller.IsActive || string.IsNullOrWhiteSpace(seller.PayPalMerchantId))
                {
                    return Ok(new { isMultiParty = false }); // Invalid seller
                }
                
                sellerMerchantIds.Add(seller.PayPalMerchantId);
            }
            
            return Ok(new 
            { 
                isMultiParty = true, 
                sellerMerchantIds = sellerMerchantIds,
                sellerCount = sellerMerchantIds.Count 
            });
        }

        return Ok(new { isMultiParty = false });
    }

    [HttpPost("create-order")]
    public async Task<IActionResult> CreatePayPalOrder()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var cartItemsWithMetadata = (await _cartService.GetCartItemsWithMetadataAsync(user.Id)).ToList();
        if (!cartItemsWithMetadata.Any())
            return BadRequest("Cart is empty");

        var total = cartItemsWithMetadata.Sum(c => c.Price);

        // Group items by seller (null = platform content)
        var itemsBySeller = cartItemsWithMetadata
            .GroupBy(c => c.SongMetadata?.SellerId)
            .ToList();

        // Check if there's any seller content
        var hasSellerContent = itemsBySeller.Any(g => g.Key != null);
        var hasPlatformContent = itemsBySeller.Any(g => g.Key == null);

        // Generate a unique order ID
        var orderId = Guid.NewGuid().ToString();

        // Save the order to database
        await _cartService.CreatePayPalOrderAsync(user.Id, orderId, total);

        // Check if order should use multi-party payment
        // Multi-party is used when there is ONLY seller content (no platform content)
        // Supports single seller OR multiple sellers (up to 10)
        if (hasSellerContent && !hasPlatformContent)
        {
            // Get all sellers
            var sellerGroups = itemsBySeller.Where(g => g.Key != null).ToList();
            
            if (sellerGroups.Count > 10)
            {
                _logger.LogWarning("Cart has {Count} sellers, exceeding PayPal's 10 seller limit. Falling back to standard checkout.", sellerGroups.Count);
                // Fall back to standard order
                return Ok(new
                {
                    orderId,
                    amount = total.ToString("F2"),
                    isMultiParty = false,
                    items = cartItemsWithMetadata.Select(i => new
                    {
                        name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                        unit_amount = i.Price.ToString("F2"),
                        quantity = 1
                    })
                });
            }

            // Build seller orders dictionary
            var sellerOrders = new Dictionary<Seller, (IEnumerable<OrderItem> Items, decimal Amount, decimal PlatformFee)>();
            var allSellerMerchantIds = new List<string>();

            foreach (var sellerGroup in sellerGroups)
            {
                var sellerId = sellerGroup.Key!.Value;
                var seller = await _sellerService.GetSellerByIdAsync(sellerId);
                
                if (seller == null || !seller.IsActive || string.IsNullOrWhiteSpace(seller.PayPalMerchantId))
                {
                    _logger.LogWarning("Seller {SellerId} is not active or has no PayPal merchant ID. Falling back to standard checkout.", sellerId);
                    // Fall back to standard order if any seller is invalid
                    return Ok(new
                    {
                        orderId,
                        amount = total.ToString("F2"),
                        isMultiParty = false,
                        items = cartItemsWithMetadata.Select(i => new
                        {
                            name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                            unit_amount = i.Price.ToString("F2"),
                            quantity = 1
                        })
                    });
                }

                var sellerAmount = sellerGroup.Sum(item => item.Price);
                var platformFee = Math.Round(sellerAmount * seller.CommissionRate, 2);
                
                var orderItems = sellerGroup.Select(i => new OrderItem
                {
                    Name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                    UnitAmount = i.Price,
                    Quantity = 1
                });

                sellerOrders[seller] = (orderItems, sellerAmount, platformFee);
                allSellerMerchantIds.Add(seller.PayPalMerchantId);
            }

            // Create multi-seller order (supports 1-10 sellers)
            if (sellerOrders.Count == 1)
            {
                // Single seller - use existing method
                var seller = sellerOrders.Keys.First();
                var (items, amount, platformFee) = sellerOrders[seller];
                
                var multiPartyResult = await _payPalPartnerService.CreateMultiPartyOrderAsync(
                    seller,
                    items,
                    amount,
                    platformFee);

                if (multiPartyResult == null || !multiPartyResult.Success)
                {
                    _logger.LogError("Failed to create multi-party PayPal order: {Error}", multiPartyResult?.ErrorMessage);
                    // Fall back to standard order
                    return Ok(new
                    {
                        orderId,
                        amount = total.ToString("F2"),
                        isMultiParty = false,
                        items = cartItemsWithMetadata.Select(i => new
                        {
                            name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                            unit_amount = i.Price.ToString("F2"),
                            quantity = 1
                        })
                    });
                }

                _logger.LogInformation("Created single-seller PayPal order {PayPalOrderId}, platform fee: ${PlatformFee}",
                    multiPartyResult.OrderId, platformFee);

                return Ok(new
                {
                    orderId,
                    payPalOrderId = multiPartyResult.OrderId,
                    amount = total.ToString("F2"),
                    isMultiParty = true,
                    sellerCount = 1,
                    sellerMerchantIds = allSellerMerchantIds,
                    platformFee = platformFee.ToString("F2"),
                    items = cartItemsWithMetadata.Select(i => new
                    {
                        name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                        unit_amount = i.Price.ToString("F2"),
                        quantity = 1
                    })
                });
            }
            else
            {
                // Multiple sellers - use multi-seller method
                var multiSellerResult = await _payPalPartnerService.CreateMultiSellerOrderAsync(sellerOrders);

                if (multiSellerResult == null || !multiSellerResult.Success)
                {
                    _logger.LogError("Failed to create multi-seller PayPal order: {Error}", multiSellerResult?.ErrorMessage);
                    // Fall back to standard order
                    return Ok(new
                    {
                        orderId,
                        amount = total.ToString("F2"),
                        isMultiParty = false,
                        items = cartItemsWithMetadata.Select(i => new
                        {
                            name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                            unit_amount = i.Price.ToString("F2"),
                            quantity = 1
                        })
                    });
                }

                var totalPlatformFee = sellerOrders.Sum(kvp => kvp.Value.PlatformFee);
                _logger.LogInformation("Created multi-seller PayPal order {PayPalOrderId} with {SellerCount} sellers, total platform fee: ${TotalFee}",
                    multiSellerResult.OrderId, sellerOrders.Count, totalPlatformFee);

                return Ok(new
                {
                    orderId,
                    payPalOrderId = multiSellerResult.OrderId,
                    amount = total.ToString("F2"),
                    isMultiParty = true,
                    sellerCount = sellerOrders.Count,
                    sellerMerchantIds = allSellerMerchantIds,
                    platformFee = totalPlatformFee.ToString("F2"),
                    items = cartItemsWithMetadata.Select(i => new
                    {
                        name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                        unit_amount = i.Price.ToString("F2"),
                        quantity = 1
                    })
                });
            }
        }

        // Check if old single-seller logic (deprecated, handled above)
        if (ShouldUseMultiPartyPayment(hasSellerContent, hasPlatformContent, itemsBySeller.Count))
        {
            // This should not be reached anymore but keeping for backwards compatibility
            var sellerGroup = itemsBySeller.First();
            var sellerId = sellerGroup.Key!.Value;
            
            // Get seller from database by seller ID
            var seller = await _sellerService.GetSellerByIdAsync(sellerId);
            
            if (seller == null || !seller.IsActive || string.IsNullOrWhiteSpace(seller.PayPalMerchantId))
            {
                _logger.LogWarning("Seller {SellerId} is not active or has no PayPal merchant ID", sellerId);
                // Fall back to standard order
                return Ok(new
                {
                    orderId,
                    amount = total.ToString("F2"),
                    isMultiParty = false,
                    items = cartItemsWithMetadata.Select(i => new
                    {
                        name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                        unit_amount = i.Price.ToString("F2"),
                        quantity = 1
                    })
                });
            }

            // Calculate platform fee (commission taken by the platform)
            // seller.CommissionRate represents the platform's percentage (e.g., 0.15 = 15%)
            // Example: $10 total * 0.15 = $1.50 platform fee, seller receives $8.50
            var platformFee = Math.Round(total * seller.CommissionRate, 2);

            // Create multi-party PayPal order on server-side
            // This creates an order where the seller is the merchant of record
            // The seller receives payment and pays PayPal fees
            // Platform receives commission via platform_fees
            var orderItems = cartItemsWithMetadata.Select(i => new OrderItem
            {
                Name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                UnitAmount = i.Price,
                Quantity = 1
            });

            var multiPartyResult = await _payPalPartnerService.CreateMultiPartyOrderAsync(
                seller,
                orderItems,
                total,
                platformFee);

            if (multiPartyResult == null || !multiPartyResult.Success)
            {
                _logger.LogError("Failed to create multi-party PayPal order: {Error}", multiPartyResult?.ErrorMessage);
                // Fall back to standard order
                return Ok(new
                {
                    orderId,
                    amount = total.ToString("F2"),
                    isMultiParty = false,
                    items = cartItemsWithMetadata.Select(i => new
                    {
                        name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                        unit_amount = i.Price.ToString("F2"),
                        quantity = 1
                    })
                });
            }

            _logger.LogInformation("Created multi-party PayPal order {PayPalOrderId} for seller {SellerId}, platform fee: ${PlatformFee}",
                multiPartyResult.OrderId, seller.Id, platformFee);

            // Return the PayPal order ID for the JavaScript SDK to use
            // The SDK will handle approval flow when merchant-id is set correctly
            return Ok(new
            {
                orderId,
                payPalOrderId = multiPartyResult.OrderId,
                amount = total.ToString("F2"),
                isMultiParty = true,
                sellerId = seller.Id,
                sellerMerchantId = seller.PayPalMerchantId,
                platformFee = platformFee.ToString("F2"),
                sellerAmount = (total - platformFee).ToString("F2"),
                items = cartItemsWithMetadata.Select(i => new
                {
                    name = Path.GetFileNameWithoutExtension(Path.GetFileName(i.SongFileName)),
                    unit_amount = i.Price.ToString("F2"),
                    quantity = 1
                })
            });
        }

        // Standard order (platform content or mixed content)
        return Ok(new
        {
            orderId,
            amount = total.ToString("F2"),
            isMultiParty = false,
            items = cartItemsWithMetadata.Select(i => new
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

        bool captured;
        string errorMessage;

        // Check if this is a multi-party order
        if (request.IsMultiParty)
        {
            // Use multi-party capture
            var result = await _payPalPartnerService.CaptureMultiPartyOrderAsync(request.PayPalOrderId);
            captured = result.Success;
            errorMessage = result.ErrorMessage ?? "Failed to capture multi-party order";
        }
        else
        {
            // Use standard capture
            (captured, errorMessage) = await CaptureWithPayPalAsync(request.PayPalOrderId);
        }

        if (!captured)
        {
            _logger.LogWarning("PayPal capture failed for PayPalOrderId {PayPalOrderId} and internal order {OrderId}: {ErrorMessage}", 
                request.PayPalOrderId, request.OrderId, errorMessage);
            return BadRequest(new { success = false, error = errorMessage ?? "Failed to capture PayPal order. Payment may not have been completed." });
        }

        // Get cart items with metadata before clearing (for email)
        var cartItemsWithMetadata = (await _cartService.GetCartItemsWithMetadataAsync(user.Id)).ToList();
        var songFileNames = cartItemsWithMetadata.Select(c => c.SongFileName).ToList();
        var totalAmount = cartItemsWithMetadata.Sum(c => c.Price);

        // Add songs to owned songs
        await _cartService.AddOwnedSongsAsync(user.Id, songFileNames, request.OrderId);

        // Complete the PayPal order
        await _cartService.CompletePayPalOrderAsync(request.OrderId);

        // Clear the cart
        await _cartService.ClearCartAsync(user.Id);

        _logger.LogInformation("User {UserId} completed purchase of {Count} songs (multi-party: {IsMultiParty})", 
            user.Id, songFileNames.Count, request.IsMultiParty);

        // Send purchase confirmation email (fire and forget - don't block the response)
        _ = Task.Run(async () =>
        {
            try
            {
                var baseUrl = GetBaseUrl();
                var userName = user.UserName ?? user.Email;
                await _purchaseEmailService.SendSongPurchaseConfirmationAsync(
                    user.Email,
                    userName,
                    request.OrderId,
                    request.PayPalOrderId,
                    cartItemsWithMetadata,
                    totalAmount,
                    baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send purchase confirmation email to user {UserId}", user.Id);
            }
        });

        return Ok(new { success = true, purchasedCount = songFileNames.Count });
    }

    private string GetBaseUrl()
    {
        // Use configured return URL if available, otherwise construct from request
        var returnBaseUrl = _configuration["PayPal:ReturnBaseUrl"];
        if (!string.IsNullOrEmpty(returnBaseUrl))
        {
            return returnBaseUrl;
        }
        return $"{Request.Scheme}://{Request.Host}";
    }

    private async Task<(bool success, string errorMessage)> CaptureWithPayPalAsync(string payPalOrderId)
    {
        try
        {
            var token = await GetPayPalAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Unable to retrieve PayPal access token.");
                return (false, "Unable to connect to payment processor. Please try again later.");
            }

            var baseUrl = _configuration["PayPal:ApiBaseUrl"] ?? "https://api-m.sandbox.paypal.com/";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");

            // Capture the order (3D Secure authentication already completed during approval flow)
            var response = await client.PostAsync($"v2/checkout/orders/{payPalOrderId}/capture", new StringContent("{}", Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PayPal capture failed: {Status} {Body}", response.StatusCode, body);
                
                // Parse PayPal error response for user-friendly message
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    
                    // Check for details array with specific error issues
                    if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in details.EnumerateArray())
                        {
                            if (detail.TryGetProperty("issue", out var issue))
                            {
                                var issueCode = issue.GetString();
                                var description = detail.TryGetProperty("description", out var desc) ? desc.GetString() : null;
                                
                                return issueCode switch
                                {
                                    "INSTRUMENT_DECLINED" => (false, "Your payment method was declined. Please try a different payment method or contact your bank."),
                                    "INSUFFICIENT_FUNDS" => (false, "Insufficient funds. Please try a different payment method."),
                                    "EXPIRED_CARD" => (false, "Your card has expired. Please use a different payment method."),
                                    "INVALID_CVV" => (false, "Invalid security code (CVV). Please check your card details and try again."),
                                    "CARD_SECURITY_CODE_MISMATCH" => (false, "Card security code mismatch. Please check your CVV and try again."),
                                    "AVS_FAILURE" => (false, "Card verification failed. Please check your billing address and try again."),
                                    "PAYER_ACCOUNT_LOCKED_OR_CLOSED" => (false, "Your PayPal account is locked or closed. Please contact PayPal support."),
                                    "TRANSACTION_REFUSED" => (false, "Transaction was refused. Please try a different payment method."),
                                    "DUPLICATE_TRANSACTION" => (false, "This appears to be a duplicate transaction. Please check your account."),
                                    _ => (false, description ?? "Payment could not be processed. Please try again or use a different payment method.")
                                };
                            }
                        }
                    }
                    
                    // Fallback to general message from PayPal
                    if (root.TryGetProperty("message", out var message))
                    {
                        return (false, $"Payment error: {message.GetString()}");
                    }
                }
                catch (JsonException)
                {
                    // If we can't parse the error, return generic message
                }
                
                return (false, "Payment could not be processed. Please try again or contact support.");
            }

            using var successDoc = JsonDocument.Parse(body);
            var status = successDoc.RootElement.GetProperty("status").GetString();
            var succeeded = string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
            
            if (!succeeded)
            {
                _logger.LogWarning("PayPal capture returned non-completed status {Status} for order {OrderId}", status, payPalOrderId);
                return (false, $"Payment is in {status} status. Please contact support if you have been charged.");
            }
            
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing PayPal order {OrderId}", payPalOrderId);
            return (false, "An unexpected error occurred. Please try again later.");
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

    /// <summary>
    /// Determines if a multi-party payment should be used for the order.
    /// Multi-party payments are used only when all items are from a single seller.
    /// </summary>
    /// <param name="hasSellerContent">Whether the cart contains any seller content</param>
    /// <param name="hasPlatformContent">Whether the cart contains any platform content</param>
    /// <param name="sellerGroupCount">Number of distinct sellers in the cart</param>
    /// <returns>True if multi-party payment should be used</returns>
    private static bool ShouldUseMultiPartyPayment(bool hasSellerContent, bool hasPlatformContent, int sellerGroupCount)
    {
        // Multi-party payment is only used when:
        // 1. There is seller content in the cart
        // 2. There is NO platform content (all items are from sellers)
        // 3. All items are from exactly ONE seller (single seller group)
        return hasSellerContent && !hasPlatformContent && sellerGroupCount == 1;
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
