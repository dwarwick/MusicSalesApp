# PayPal Multi-Party Payment Implementation

## Overview

This implementation enables sellers to be the merchant of record for their music sales. When a customer purchases seller content, payment goes directly to the seller's PayPal account, the seller pays PayPal transaction fees, and the platform receives a 15% commission automatically.

## Payment Flow

### For Seller Content (Multi-Party Payment)

```
Customer $10 → Seller's PayPal Account (merchant of record)
               ├─ PayPal fees: -$0.59 (paid by seller)
               ├─ Platform commission: -$1.50 (15%)
               └─ Seller receives: $7.91

Platform receives: $1.50 (commission only, NO PayPal fees)
```

### For Platform Content (Standard Payment)

```
Customer $10 → Platform's PayPal Account
               ├─ PayPal fees: -$0.59 (paid by platform)
               └─ Platform keeps: $9.41
```

## Implementation Details

### 1. Order Detection

When a customer's cart contains ONLY songs from a single seller:
- Multi-party payment is triggered
- Mixed carts (platform + seller OR multiple sellers) use standard payment

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
            amount: { value: "1.50" }  // 15% commission
        }]
    }
}
```

This returns an `approvalUrl` - the PayPal URL where the customer approves payment.

### 3. Direct PayPal Redirect

**Key difference from standard checkout:**

- **Standard orders**: Use PayPal JavaScript SDK buttons
- **Multi-party orders**: Redirect directly to PayPal approval URL

The `Checkout.razor.cs` component detects multi-party orders and redirects the user to PayPal's approval page instead of showing PayPal buttons. This is necessary because the JavaScript SDK cannot handle orders where the payee differs from the platform.

### 4. Return Handling

After the customer approves payment on PayPal:
1. PayPal redirects back to `/checkout?token={order_id}&PayerID={payer_id}`
2. `Checkout.razor.cs` detects the query parameters
3. Calls `api/cart/capture-order` to capture the payment
4. Multi-party capture uses `PayPalPartnerService.CaptureMultiPartyOrderAsync()`

### 5. Fund Disbursement

PayPal automatically splits funds:
- Seller receives payment minus platform fee and PayPal fees
- Platform receives commission
- Disbursement mode is **INSTANT** - no waiting period

## Seller Onboarding

Sellers must complete PayPal Partner Referrals onboarding:

1. Navigate to Manage Account page
2. Click "Become a Seller"
3. Complete PayPal merchant account setup
4. System stores seller's `PayPalMerchantId`
5. Upon approval, user role changes from "User" to "Seller"

## Commission Structure

Default commission: **15%** (configurable per seller via `Seller.CommissionRate`)

**Example for $10 sale:**
- Gross amount: $10.00
- PayPal fee (seller pays): $0.59
- Platform commission: $1.50
- **Seller net: $7.91**
- **Platform net: $1.50**

## Edge Cases

| Scenario | Payment Method |
|----------|---------------|
| Single seller, all seller content | Multi-party ✅ |
| Platform content only | Standard |
| Mixed (platform + seller) | Standard |
| Multiple sellers | Standard |

Only pure single-seller carts use multi-party payment.

## Benefits

**For Sellers:**
- Direct payment to their PayPal account
- Full control over their merchant account
- Standard PayPal seller protection
- Instant access to funds

**For Platform:**
- No liability for seller transactions
- No PayPal fees on seller sales
- Automatic commission collection
- No manual payouts needed
- Simplified accounting

**For Customers:**
- Same PayPal checkout experience
- Purchase protection applies
- Transparent payment flow

## Technical Requirements

- Platform must have PayPal Platform Partner account
- `PayPal:PartnerId` must be configured
- `PayPal:BNCode` must be configured
- Sellers must complete Partner Referrals onboarding
- Sellers must have active `PayPalMerchantId`

## Limitations

- Multi-party only works for single-seller orders
- Mixed carts fall back to standard payment
- Requires seller onboarding with PayPal
- Platform must manually handle mixed cart scenarios

## Files Modified

- `MusicSalesApp/Controllers/CartController.cs` - Multi-party order creation
- `MusicSalesApp/Components/Pages/Checkout.razor.cs` - Direct redirect and return handling
- `MusicSalesApp/Services/PayPalPartnerService.cs` - Multi-party order/capture methods (already existed)

## Testing

To test multi-party payments:
1. Onboard a test seller in PayPal sandbox
2. Upload music as that seller
3. Add only that seller's music to cart
4. Proceed to checkout
5. Verify redirect to PayPal (not JS buttons)
6. Approve payment
7. Verify funds split correctly in PayPal sandbox accounts
