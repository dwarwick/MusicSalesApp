# PayPal Multi-Party Payment with Expanded Checkout

## Overview

This implementation enables sellers to be the merchant of record for their music sales using PayPal's JavaScript SDK with Expanded Checkout features. Payment goes directly to the seller's PayPal account(s), each seller pays their own PayPal transaction fees, and the platform receives a 15% commission automatically.

**Supports both single-seller and multi-seller transactions (up to 10 sellers per transaction).**

## Payment Flow

### For Seller Content (Multi-Party Payment)

**Single Seller:**
```
Customer $10 → Seller's PayPal (merchant of record)
              → Seller pays PayPal fees ($0.59)
              → Platform receives commission ($1.50)
              → Seller nets $7.91

Platform receives: $1.50 (commission only, NO PayPal fees)
```

**Multiple Sellers:**
```
Customer pays $25 total

Seller A ($15):
→ Seller A's PayPal (merchant of record)
→ Seller A pays fees ($0.74)
→ Platform receives $2.25 commission
→ Seller A nets $12.01

Seller B ($10):
→ Seller B's PayPal (merchant of record)
→ Seller B pays fees ($0.59)
→ Platform receives $1.50 commission
→ Seller B nets $7.91

Platform receives: $3.75 total commission (NO PayPal fees)
```

### For Platform Content (Standard Payment)

```
Customer $10 → Platform's PayPal
              → Platform pays PayPal fees ($0.59)
              → Platform keeps: $9.41
```

## Implementation Details

### 1. SDK Loading with merchant-id Parameter

**Key Insight:** PayPal's JavaScript SDK supports multi-seller payments when you include multiple seller `merchant-id` values (comma-separated) in the SDK URL.

**Checkout.razor.cs** checks if the cart has seller content and passes all seller merchant IDs to JavaScript:

```csharp
var cartCheckResponse = await Http.GetFromJsonAsync<CartCheckResponse>("api/cart/check-multiparty");
if (cartCheckResponse != null && cartCheckResponse.IsMultiParty)
{
    // Join multiple merchant IDs with comma (PayPal SDK supports up to 10)
    sellerMerchantIds = string.Join(",", cartCheckResponse.SellerMerchantIds);
}
```

**Checkout.razor.js** loads the SDK with all merchant IDs:

```javascript
let sdkUrl = `https://www.paypal.com/sdk/js?client-id=${clientId}&currency=USD&enable-funding=venmo,paylater&intent=capture`;

// For multi-party payments, add merchant IDs (comma-separated for multiple sellers)
if (sellerMerchantIds) {
    sdkUrl += `&merchant-id=${sellerMerchantIds}`;  // e.g., "SELLER1,SELLER2,SELLER3"
}
```

### 2. Server-Side Order Creation

**Single Seller:** Uses `PayPalPartnerService.CreateMultiPartyOrderAsync()` to create an order with one purchase unit.

**Multiple Sellers:** Uses `PayPalPartnerService.CreateMultiSellerOrderAsync()` to create an order with multiple purchase units (one per seller):

```csharp
{
    intent: "CAPTURE",
    purchase_units: [
        {
            reference_id: "SELLER_123",
            payee: { merchant_id: seller1.PayPalMerchantId },
            amount: { value: "15.00" },
            payment_instruction: {
                disbursement_mode: "INSTANT",
                platform_fees: [{ amount: { value: "2.25" } }]
            },
            items: [...]
        },
        {
            reference_id: "SELLER_456",
            payee: { merchant_id: seller2.PayPalMerchantId },
            amount: { value: "10.00" },
            payment_instruction: {
                disbursement_mode: "INSTANT",
                platform_fees: [{ amount: { value: "1.50" } }]
            },
            items: [...]
        }
    ]
}
```

### 3. JavaScript Handles Both Flows

The `createOrder` callback detects whether it's a multi-party order:

```javascript
createOrder: async function (data, actions) {
    const orderId = await dotNetRef.invokeMethodAsync('CreateOrder');
    
    if (sellerMerchantIds) {
        // Multi-party (single or multiple sellers): server pre-created the order
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
- Each seller receives their net amount (gross - PayPal fees - platform fee)
- Platform receives sum of all platform fees
- Disbursement mode is **INSTANT** - no waiting period
- Buyer may see separate transactions on their statement (one per seller)

## Order Type Detection

| Scenario | Payment Method | Seller Limit |
|----------|---------------|--------------|
| Single seller, all seller content | Multi-party ✅ | 1 seller |
| Multiple sellers, all seller content | Multi-party ✅ | 2-10 sellers |
| Platform content only | Standard | N/A |
| Mixed (platform + seller) | Standard (fallback) | N/A |
| More than 10 sellers | Standard (fallback) | Exceeds limit |

Multi-party payment is used when the cart contains ONLY seller content (no platform content) and has 1-10 sellers.

## API Endpoints

### GET /api/cart/check-multiparty
Check if the current cart qualifies for multi-party payment and get seller merchant IDs.

**Response:**
```json
{
  "isMultiParty": true,
  "sellerMerchantIds": ["SELLER1", "SELLER2"],
  "sellerCount": 2
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
  "sellerCount": 2,
  "sellerMerchantIds": ["SELLER1", "SELLER2"],
  "platformFee": "3.75",
  "items": [...]
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

**Example for $25 sale (2 sellers):**

**Seller A ($15):**
- Gross amount: $15.00
- PayPal fee (seller pays): $0.74
- Platform commission: $2.25
- **Seller A net: $12.01**

**Seller B ($10):**
- Gross amount: $10.00
- PayPal fee (seller pays): $0.59
- Platform commission: $1.50
- **Seller B net: $7.91**

**Platform total: $3.75**

## Benefits

**For Sellers:**
- Direct payment to their PayPal accounts
- Merchant of record for their sales
- Instant access to funds
- Standard PayPal seller protection
- Customer can review cart before approval

**For Platform:**
- No liability for seller transactions
- No PayPal fees on seller sales
- Automatic commission collection for all sellers
- No manual payouts needed
- Simplified accounting (single transaction, multiple settlements)

**For Customers:**
- Expanded payment options (Venmo, Pay Later, cards)
- Can review full cart before completing payment
- 3D Secure protection for card payments
- Standard PayPal buyer protection
- Single checkout experience (even with multiple sellers)

## Technical Requirements

- Platform must have PayPal Platform Partner account
- `PayPal:PartnerId` must be configured
- `PayPal:BNCode` must be configured
- Sellers must complete Partner Referrals onboarding
- Sellers must have active `PayPalMerchantId`
- Maximum 10 sellers per transaction (PayPal limitation)

## Files Modified

- `MusicSalesApp/Services/IPayPalPartnerService.cs` - Added CreateMultiSellerOrderAsync method
- `MusicSalesApp/Services/PayPalPartnerService.cs` - Implemented multi-seller order creation
- `MusicSalesApp/Controllers/CartController.cs` - Updated to support 1-10 sellers
- `MusicSalesApp/Components/Pages/Checkout.razor.cs` - SDK initialization with multiple merchant IDs
- `MusicSalesApp/Components/Pages/Checkout.razor.js` - Support comma-separated merchant IDs

## Why This Works

According to PayPal's Commerce Platform documentation, the `purchase_units` array can contain up to 10 units, each with its own `payee` (merchant) and `platform_fees`. When the SDK is loaded with multiple `merchant-id` values (comma-separated), it configures the checkout to handle orders where those merchants are payees.

Behind the scenes, PayPal creates separate settlement transactions for each seller, but the buyer experiences a single checkout flow with one approval.

**References:**
- [PayPal Multi-Seller Payments](https://developer.paypal.com/docs/multiparty/checkout/multiseller-payments/)
- [PayPal Orders API v2](https://developer.paypal.com/docs/api/orders/v2/)
- [PayPal JavaScript SDK](https://developer.paypal.com/sdk/js/)
- [PayPal SDK npm package](https://www.npmjs.com/package/@paypal/paypal-js)

## Limitations

- Maximum 10 sellers per transaction (PayPal limit)
- Cannot mix platform and seller content in multi-party orders
- Buyer may see multiple transactions on their bank statement (one per seller)
- All sellers must have completed PayPal onboarding and have active merchant IDs
