# PayPal Multi-Party Payment with Expanded Checkout

## Overview

This implementation enables sellers to be the merchant of record for their music sales using PayPal's JavaScript SDK with Expanded Checkout features. Payment goes directly to the seller's PayPal account, the seller pays PayPal transaction fees, and the platform receives a 15% commission automatically.

## Payment Flow

### For Seller Content (Multi-Party Payment)

```
Customer $10 → Seller's PayPal (merchant of record)
              → Seller pays PayPal fees ($0.59)
              → Platform receives commission ($1.50)
              → Seller nets $7.91

Platform receives: $1.50 (commission only, NO PayPal fees)
```

### For Platform Content (Standard Payment)

```
Customer $10 → Platform's PayPal
              → Platform pays PayPal fees ($0.59)
              → Platform keeps: $9.41
```

## Implementation Details

### 1. SDK Loading with merchant-id Parameter

**Key Insight:** PayPal's JavaScript SDK supports multi-party payments when you include the seller's `merchant-id` parameter in the SDK URL.

**Checkout.razor.cs** checks if the cart is multi-party and passes the seller's merchant ID to JavaScript:

```csharp
// Get seller merchant ID if this is a multi-party order
var cartCheckResponse = await Http.GetFromJsonAsync<CartCheckResponse>("api/cart/check-multiparty");
if (cartCheckResponse != null && cartCheckResponse.IsMultiParty)
{
    sellerMerchantId = cartCheckResponse.SellerMerchantId;
}

// Initialize SDK with seller merchant ID for multi-party
await _jsModule.InvokeVoidAsync("initPayPal", clientId, sellerMerchantId, amount, _dotNetRef);
```

**Checkout.razor.js** loads the SDK with the merchant-id parameter:

```javascript
let sdkUrl = `https://www.paypal.com/sdk/js?client-id=${clientId}&currency=USD&enable-funding=venmo,paylater&intent=capture`;

// For multi-party payments, add the seller's merchant ID
if (sellerMerchantId) {
    sdkUrl += `&merchant-id=${sellerMerchantId}`;
}
```

### 2. Server-Side Order Creation

For multi-party orders, `CartController.CreatePayPalOrder()` calls `PayPalPartnerService.CreateMultiPartyOrderAsync()` which creates a PayPal order with:

```csharp
{
    payee: {
        merchant_id: seller.PayPalMerchantId  // Seller is merchant of record
    },
    payment_instruction: {
        disbursement_mode: "INSTANT",
        platform_fees: [{
            amount: { value: platformFee }  // 15% commission
        }]
    }
}
```

This returns a PayPal order ID that is compatible with the SDK loaded with the seller's merchant-id.

### 3. JavaScript Handles Both Flows

The `createOrder` callback detects whether it's a multi-party order:

```javascript
createOrder: async function (data, actions) {
    const orderId = await dotNetRef.invokeMethodAsync('CreateOrder');
    
    if (sellerMerchantId) {
        // Multi-party: server pre-created the order, just return the ID
        return orderId;  // This is the PayPal order ID
    }
    
    // Standard: create PayPal order client-side
    return await actions.order.create({ /* ... */ });
}
```

### 4. Expanded Checkout Features

The SDK is loaded with Expanded Checkout parameters:
- `enable-funding=venmo,paylater` - Additional payment options
- `intent=capture` - Immediate payment capture
- Supports 3D Secure for card payments
- Allows customers to review cart before approval

### 5. Fund Disbursement

PayPal automatically splits funds upon capture:
- Seller receives net amount (gross - PayPal fees - platform fee)
- Platform receives commission
- Disbursement mode is **INSTANT** - no waiting period

## Order Type Detection

| Scenario | Payment Method |
|----------|---------------|
| Single seller, all seller content | Multi-party ✅ |
| Platform content only | Standard |
| Mixed (platform + seller) | Standard |
| Multiple sellers | Standard |

Only pure single-seller carts use multi-party payment.

## API Endpoints

### GET /api/cart/check-multiparty
Check if the current cart is multi-party without creating an order.

**Response:**
```json
{
  "isMultiParty": true,
  "sellerMerchantId": "SELLER_MERCHANT_ID"
}
```

### POST /api/cart/create-order
Create the order (internal database record and PayPal order if multi-party).

**For multi-party orders, returns:**
```json
{
  "orderId": "internal-guid",
  "payPalOrderId": "PAYPAL-ORDER-ID",
  "isMultiParty": true,
  "sellerMerchantId": "SELLER_MERCHANT_ID",
  "platformFee": "1.50",
  "sellerAmount": "8.50"
}
```

## Seller Onboarding

Sellers must complete PayPal Partner Referrals onboarding:

1. Navigate to Manage Account page
2. Click "Become a Seller"
3. Complete PayPal merchant account setup
4. System stores seller's `PayPalMerchantId`
5. Upon approval, user role changes to "Seller"

## Commission Structure

Default commission: **15%** (configurable per seller via `Seller.CommissionRate`)

**Example for $10 sale:**
- Gross amount: $10.00
- PayPal fee (seller pays): $0.59
- Platform commission: $1.50
- **Seller net: $7.91**
- **Platform net: $1.50**

## Benefits

**For Sellers:**
- Direct payment to their PayPal account
- Merchant of record for their sales
- Instant access to funds
- Standard PayPal seller protection
- Customer can review cart before approval

**For Platform:**
- No liability for seller transactions
- No PayPal fees on seller sales
- Automatic commission collection
- No manual payouts needed
- Simplified accounting

**For Customers:**
- Expanded payment options (Venmo, Pay Later, cards)
- Can review cart before completing payment
- 3D Secure protection for card payments
- Standard PayPal buyer protection

## Technical Requirements

- Platform must have PayPal Platform Partner account
- `PayPal:PartnerId` must be configured
- `PayPal:BNCode` must be configured
- Sellers must complete Partner Referrals onboarding
- Sellers must have active `PayPalMerchantId`

## Files Modified

- `MusicSalesApp/Controllers/CartController.cs` - Added check-multiparty endpoint, multi-party order creation
- `MusicSalesApp/Components/Pages/Checkout.razor.cs` - SDK initialization with seller merchant ID
- `MusicSalesApp/Components/Pages/Checkout.razor.js` - SDK loading with merchant-id parameter, order creation logic

## Why This Works

According to PayPal's documentation, when you load the SDK with a `merchant-id` parameter that differs from the platform's client-id, the SDK is configured to handle orders where that merchant is the payee. This is specifically designed for marketplace/partner scenarios and is the recommended approach for Expanded Checkout with multi-party payments.

**References:**
- [PayPal Multi-Party Checkout](https://developer.paypal.com/docs/multiparty/checkout/)
- [PayPal JavaScript SDK](https://developer.paypal.com/sdk/js/)
- [PayPal SDK npm package](https://www.npmjs.com/package/@paypal/paypal-js)

