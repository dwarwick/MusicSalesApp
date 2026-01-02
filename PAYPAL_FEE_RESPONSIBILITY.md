# PayPal Fee Responsibility Documentation

## Overview
This document explains how PayPal transaction fees are handled in the MusicSalesApp platform for both standard (platform) content and multi-party (seller) content.

## Payment Flow Types

### 1. Standard Payment Flow (Platform Content)
**Applies to:** Songs uploaded by the platform (no seller association)

**Flow:**
1. Customer purchases platform content
2. Payment goes to **platform's PayPal account**
3. **Platform pays PayPal transaction fees** on the full amount
4. Platform receives net amount after PayPal fees
5. Platform keeps 100% of the sale (after PayPal fees)

**Example:**
- Sale price: $10.00
- PayPal fee (2.9% + $0.30): -$0.59
- Platform receives: $9.41

**Merchant of Record:** Platform

### 2. Multi-Party Payment Flow (Seller Content)
**Applies to:** Songs uploaded by onboarded sellers

**Flow:**
1. Customer purchases seller's content
2. Payment goes to **seller's PayPal account** (seller is merchant of record)
3. **Seller pays PayPal transaction fees** on the full amount
4. Platform fee (commission) is automatically split to platform's account
5. Seller receives net amount after both PayPal fees AND platform commission

**Example:**
- Sale price: $10.00
- PayPal fee (2.9% + $0.30): -$0.59 (paid by seller)
- Platform commission (15%): -$1.50
- Seller receives: $7.91
- Platform receives: $1.50 (commission only, no PayPal fees)

**Merchant of Record:** Seller

**Key Points:**
- Seller's PayPal account is charged for the transaction fees
- Platform receives only the commission fee (15% by default)
- Platform does NOT pay PayPal fees for seller content
- Disbursement is INSTANT - funds are split immediately upon capture

## Implementation Details

### Server-Side Order Creation (Multi-Party)
Multi-party orders are created server-side using `PayPalPartnerService.CreateMultiPartyOrderAsync()`:

```csharp
var orderData = new
{
    intent = "CAPTURE",
    purchase_units = new[]
    {
        new
        {
            amount = new { /* total amount */ },
            payee = new
            {
                merchant_id = seller.PayPalMerchantId  // Seller receives payment
            },
            payment_instruction = new
            {
                disbursement_mode = "INSTANT",
                platform_fees = new[]
                {
                    new { amount = new { value = platformFee } }  // Platform commission
                }
            }
        }
    }
};
```

**Critical Fields:**
- `payee.merchant_id`: Specifies seller as merchant of record
- `platform_fees`: Defines the commission amount that goes to platform
- `disbursement_mode = "INSTANT"`: Funds are split immediately

### Client-Side Handling
The JavaScript (Checkout.razor.js) detects multi-party orders and uses the server-created PayPal order ID directly:

```javascript
const isMultiParty = await dotNetRef.invokeMethodAsync('GetIsMultiParty');

if (isMultiParty) {
    // Use server-created PayPal order (already has payee and platform_fees)
    return orderId;
}
else {
    // Create standard PayPal order client-side (payment goes to platform)
    return await actions.order.create({ /* standard order */ });
}
```

### Order Type Determination
Multi-party payment is used ONLY when:
1. Cart contains seller content (SongMetadata.SellerId is set)
2. All items are from a SINGLE seller
3. No platform content is mixed in
4. Seller has valid PayPalMerchantId and is active

**Mixed carts** (platform + seller OR multiple sellers) use standard payment flow to platform account.

## Commission Rate
Default platform commission is **15%** (configurable per seller via `Seller.CommissionRate`).

## Why This Approach?

### Benefits for Platform:
- No liability for seller payment disputes
- No PayPal fees on seller transactions
- Simplified accounting (receive commission only)
- No need to manually distribute funds to sellers

### Benefits for Sellers:
- Direct access to funds in their PayPal account
- Full control over their PayPal account
- Standard PayPal buyer/seller protection
- Instant payment receipt

### Compliance:
- Sellers are properly designated as merchants of record
- Platform acts as facilitator, not reseller
- Tax reporting responsibility is correctly assigned to sellers

## Testing
To verify the payment flow:
1. Create a test seller account with PayPal sandbox credentials
2. Upload content as that seller
3. Purchase the content and check PayPal sandbox dashboard
4. Verify payment goes to seller's account
5. Verify platform fee appears in platform's account
6. Check that seller's account shows PayPal fees deducted

## References
- PayPal Partner Referrals API: https://developer.paypal.com/docs/multiparty/checkout/
- Platform Fees: https://developer.paypal.com/docs/multiparty/checkout/set-up-platform-fees/
