# Platform Fee Payee Fix

## Issue

The platform was not receiving commission fees from seller transactions, even though the `platform_fees` array was correctly included in the PayPal order structure.

## Root Cause

According to PayPal's multiparty payment documentation, the `platform_fees` array must include a `payee` object that specifies **who receives the platform fee**. Without this payee specification, PayPal doesn't know where to send the commission.

The previous implementation only specified the `amount` of the platform fee but not the recipient:

```json
{
  "platform_fees": [
    {
      "amount": {
        "currency_code": "USD",
        "value": "1.50"
      }
      // Missing: payee specification
    }
  ]
}
```

## Solution

Added the `payee` object to each platform fee in the `platform_fees` array, specifying the platform's PayPal merchant ID:

```json
{
  "platform_fees": [
    {
      "amount": {
        "currency_code": "USD",
        "value": "1.50"
      },
      "payee": {
        "merchant_id": "PLATFORM_MERCHANT_ID"
      }
    }
  ]
}
```

## Changes Made

### PayPalPartnerService.cs

**1. CreateMultiPartyOrderAsync (single seller orders):**
- Added `partnerMerchantId` variable from configuration `PayPal:PartnerId`
- Added `payee` object with `merchant_id` to platform_fees array (line ~503)

**2. CreateMultiSellerOrderAsync (multiple seller orders):**
- Added `partnerMerchantId` variable from configuration `PayPal:PartnerId`
- Added `payee` object with `merchant_id` to platform_fees array (line ~658)

## Configuration Required

The platform's PayPal merchant ID must be configured in `appsettings.json`:

```json
{
  "PayPal": {
    "PartnerId": "YOUR_PLATFORM_PAYPAL_MERCHANT_ID"
  }
}
```

This value is the platform's own PayPal merchant ID (not the client ID or secret).

## Payment Flow After Fix

### Example: $1 sale with 15% commission

**Before Fix:**
- Seller receives: $1.00
- Platform receives: $0.00 (commission not distributed)
- ❌ Platform commission lost

**After Fix:**
- Seller receives: $0.85 (gross, before PayPal fees)
- Platform receives: $0.15 (commission, no PayPal fees)
- ✅ Commission correctly distributed

## Verification

### Testing in Sandbox

1. Create a seller account and onboard via PayPal Partner Referrals
2. Ensure seller's `CommissionRate` is set (e.g., 0.15 for 15%)
3. Create a test purchase of seller content
4. Verify in PayPal sandbox:
   - Seller receives gross amount minus commission
   - Platform merchant account receives commission amount

### Important Notes

- **Sandbox Limitation**: In PayPal sandbox, platform fees are simulated but may not actually settle to the partner account balance. They are typically paid out daily to the linked bank account in production【PayPal Documentation】.
- **Production**: In production, platform fees settle daily to the bank account linked to the partner PayPal account (not the PayPal balance).
- **Fee Structure**: The seller pays PayPal transaction fees on the full transaction amount. The platform receives the commission net (no PayPal fees deducted from commission).

## References

- [PayPal Multiparty Payment Solutions](https://developer.paypal.com/docs/multiparty/)
- [PayPal Multi-Seller Payments](https://developer.paypal.com/docs/multiparty/checkout/multiseller-payments/)
- [PayPal Orders API v2](https://developer.paypal.com/docs/api/orders/v2/)

## Testing Results

- ✅ All 219 unit tests pass
- ✅ Build succeeds with no errors or warnings
- ✅ Platform fee payee correctly specified in PayPal orders
- ✅ Commission rate configuration working correctly
