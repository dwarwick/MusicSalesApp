# PayPal Payment Flow Diagrams

## Standard Payment Flow (Platform Content)

```
┌─────────────┐
│  Customer   │
└──────┬──────┘
       │ $10.00
       │ (purchases platform song)
       ▼
┌──────────────────────────────────────┐
│         PayPal Checkout              │
│  (Standard Order - Client-side)      │
└──────┬───────────────────────────────┘
       │ $10.00
       ▼
┌──────────────────────────────────────┐
│    Platform's PayPal Account         │
│                                      │
│  Receives: $10.00                    │
│  PayPal Fees: -$0.59                 │
│  Net: $9.41                          │
└──────────────────────────────────────┘

RESULT: Platform pays PayPal fees and keeps net amount
```

## Multi-Party Payment Flow (Seller Content) - FIXED ✅

```
┌─────────────┐
│  Customer   │
└──────┬──────┘
       │ $10.00
       │ (purchases seller song)
       ▼
┌──────────────────────────────────────────────────┐
│         PayPal Checkout                          │
│  (Multi-Party Order - Server-side)               │
│                                                  │
│  Order Structure:                                │
│  {                                               │
│    payee: { merchant_id: "SELLER_ID" }          │
│    platform_fees: [{ value: "1.50" }]           │
│    disbursement_mode: "INSTANT"                  │
│  }                                               │
└──────┬───────────────────────────────────────────┘
       │
       │ $10.00 (total payment)
       │
       ├─────────────────────────────────────────┐
       │                                         │
       ▼                                         ▼
┌──────────────────────────┐         ┌───────────────────────┐
│ Seller's PayPal Account  │         │  Platform's Account   │
│                          │         │                       │
│ Receives: $10.00         │         │  Commission: $1.50    │
│ PayPal Fees: -$0.59      │         │  PayPal Fees: $0.00   │
│ Platform Fee: -$1.50     │         │  Net: $1.50          │
│ Net: $7.91              │         │                       │
└──────────────────────────┘         └───────────────────────┘

RESULT: Seller pays PayPal fees, Platform receives commission only
```

## Key Differences

| Aspect | Platform Content | Seller Content (Multi-Party) |
|--------|-----------------|------------------------------|
| **Merchant of Record** | Platform | Seller |
| **Payment Destination** | Platform's PayPal account | Seller's PayPal account |
| **Who Pays PayPal Fees** | Platform | Seller ✅ |
| **Platform Revenue** | 100% - PayPal fees | 15% commission (no fees) |
| **Order Creation** | Client-side (JavaScript) | Server-side (PayPalPartnerService) |
| **Order Type** | Standard | Multi-party with platform_fees |
| **Fund Distribution** | Immediate | INSTANT disbursement |

## Commission Calculation

For a $10 seller transaction:

```
Gross Amount:              $10.00
PayPal Fee (2.9% + $0.30): -$0.59  (paid by seller)
Platform Commission (15%): -$1.50  (goes to platform)
─────────────────────────────────
Seller Net:                $7.91

Platform Receives:         $1.50  (no PayPal fees)
```

## Code Flow

### 1. Cart with Seller Content

```
User adds seller song to cart
    ↓
CartController.CreatePayPalOrder() called
    ↓
Detects: Single seller, no platform content
    ↓
isMultiParty = true
    ↓
PayPalPartnerService.CreateMultiPartyOrderAsync()
    ↓
Creates PayPal order with:
  - payee: seller's merchant ID
  - platform_fees: $1.50
    ↓
Returns PayPal Order ID to frontend
```

### 2. JavaScript Checkout Flow

```
createOrder() called by PayPal button
    ↓
Calls dotNetRef.CreateOrder()
    ↓
Server returns PayPal Order ID (already created)
    ↓
Checks: dotNetRef.GetIsMultiParty() = true
    ↓
Returns server-created PayPal Order ID
    ↓
PayPal approval flow
    ↓
OnApprove() called
    ↓
Server captures multi-party order
    ↓
Funds split instantly:
  - $8.50 → Seller (after fees)
  - $1.50 → Platform
```

## Verification Commands

### Check Order Structure in Logs

```bash
# Look for multi-party order creation
grep "Created multi-party PayPal order" application.log

# Expected output:
# Created multi-party PayPal order {orderId} for seller {sellerId}, platform fee: $1.50
```

### PayPal Sandbox Verification

1. Check Seller's PayPal dashboard:
   - Transaction should show as "Payment Received"
   - Should show platform fee deduction
   - Should show PayPal fee deduction

2. Check Platform's PayPal dashboard:
   - Transaction should show as "Platform Fee Received"
   - No PayPal fees charged

## References

- PayPal Multi-Party Payments: https://developer.paypal.com/docs/multiparty/checkout/
- Platform Fees: https://developer.paypal.com/docs/multiparty/checkout/set-up-platform-fees/
- Disbursement Modes: https://developer.paypal.com/docs/multiparty/checkout/pay-sellers/
