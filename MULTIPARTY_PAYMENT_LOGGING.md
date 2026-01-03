# Multi-Party Payment Logging (Development Mode)

## Overview

Comprehensive logging has been added to track money distribution in multi-party PayPal transactions. These logs are **only enabled in Development mode** to help with debugging and verification during testing.

## What's Logged

### Order Creation (Single Seller)

When a multi-party order is created for a single seller, the following is logged:

```
=== MULTI-PARTY ORDER MONEY DISTRIBUTION (Development) ===
PayPal Order ID: {OrderId}
Seller ID: {SellerId} (PayPal Merchant ID: {MerchantId})
Total Amount (Buyer Pays): ${TotalAmount}
Platform Commission (15%): ${PlatformFee}
Seller Receives (Gross, 85%): ${SellerAmount}
Seller Will Pay PayPal Fees: ~${PayPalFees} (2.9% + $0.30)
Seller Net (After PayPal Fees): ~${SellerNet}
Platform Receives (Commission Only): ${PlatformFee} (NO PayPal fees deducted)
===============================================
```

**Example Output:**
```
PayPal Order ID: 88F18931JY794935N
Seller ID: 5 (PayPal Merchant ID: F6KSTJ4Z6L58U)
Total Amount (Buyer Pays): $1.00
Platform Commission (15%): $0.15
Seller Receives (Gross, 85%): $0.85
Seller Will Pay PayPal Fees: ~$0.33 (2.9% + $0.30)
Seller Net (After PayPal Fees): ~$0.52
Platform Receives (Commission Only): $0.15 (NO PayPal fees deducted)
```

### Order Creation (Multiple Sellers)

When a multi-seller order is created (2-10 sellers), the following is logged:

```
=== MULTI-SELLER ORDER MONEY DISTRIBUTION (Development) ===
PayPal Order ID: {OrderId}
Number of Sellers: {SellerCount}
Total Cart Amount (Buyer Pays): ${Total}
Total Platform Commission: ${TotalCommission}
Total PayPal Fees (Paid by Sellers): ~${TotalPayPalFees}
--- Seller 1: ID {SellerId} (PayPal: {MerchantId}) ---
  Items: {ItemCount} songs totaling ${Amount}
  Seller Receives (Gross): ${Amount}
  Platform Commission: ${PlatformFee}
  Seller After Commission: ${AfterCommission}
  Estimated PayPal Fees (Seller Pays): ~${PayPalFees}
  Seller Net (After All Fees): ~${Net}
--- Seller 2: ... ---
Platform Receives (Total Commission): ${TotalCommission} (NO PayPal fees)
===============================================
```

### Order Capture

When a multi-party order is captured (payment completed), the following **actual amounts from PayPal's response** are logged:

```
=== MULTI-PARTY CAPTURE RESULTS (Development) ===
PayPal Order ID: {OrderId}
Capture ID: {CaptureId}
Status: COMPLETED
--- Purchase Unit 1 ---
  Payee (Merchant of Record): {SellerMerchantId}
  Total Amount: ${Amount}
  Captured Amount: ${CapturedAmount}
  Seller Gross Amount: ${Gross}
  PayPal Fee (Paid by Seller): ${Fee}
  Seller Net Amount: ${Net}
  Platform Fee (Commission): ${PlatformFee}
  Platform Fee Recipient: {PlatformMerchantId}
--- Purchase Unit 2 (if multi-seller) ---
  ...
===============================================
```

## Key Insights from Logs

### Order Creation Logs Show:
- **Estimated amounts** before PayPal processes the payment
- How the buyer's payment is split
- Seller commission rate (from their database record)
- Estimated PayPal fees (2.9% + $0.30 per transaction)
- Expected net amounts for sellers and platform

### Capture Logs Show:
- **Actual amounts** from PayPal after processing
- Real PayPal fees charged (not estimates)
- Actual net amounts received by sellers
- Confirmation that platform fee was distributed
- Merchant IDs of all parties involved

## How to Enable

Logging is automatically enabled when:
- The application is running in **Development environment**
- Set via `ASPNETCORE_ENVIRONMENT=Development`

In production, these detailed logs are **NOT** generated to avoid log clutter and potential security concerns.

## Where to Find Logs

When running the application locally:
1. Check the console output where the app is running
2. Look for lines starting with `=== MULTI-PARTY` or `=== MULTI-SELLER`
3. Logs appear at INFO level

## Money Flow Summary

### For Each Seller Transaction:

**What the Buyer Pays:**
- Example: $1.00

**What the Seller Receives (Gross):**
- Example: $0.85 (85% after 15% commission)

**What the Seller Pays:**
- PayPal Fees: $0.33 (2.9% of $1.00 + $0.30)
- Leaves seller with: $0.52 net

**What the Platform Receives:**
- Commission: $0.15 (15% of $1.00)
- NO PayPal fees deducted
- Platform receives the full $0.15

### Important Notes:

1. **Seller is Merchant of Record**: The seller's PayPal account receives the full transaction amount
2. **Platform Fee is Deducted Automatically**: PayPal splits the commission to the platform's account
3. **Seller Pays All PayPal Fees**: Transaction fees are deducted from the seller's gross amount
4. **Platform Pays Zero Fees**: The platform commission has no PayPal fees deducted

## Verification Steps

To verify the logging is working:

1. **Set environment to Development**:
   ```bash
   export ASPNETCORE_ENVIRONMENT=Development
   ```

2. **Create a test order** with seller content

3. **Check logs** during order creation for money distribution breakdown

4. **Complete the purchase** and check capture logs for actual PayPal amounts

5. **Compare estimates vs actuals** to understand PayPal fee calculation

## Example Complete Flow

```
[INFO] Created multi-party order 88F18931JY794935N for seller 5
[INFO] === MULTI-PARTY ORDER MONEY DISTRIBUTION (Development) ===
[INFO] PayPal Order ID: 88F18931JY794935N
[INFO] Seller ID: 5 (PayPal Merchant ID: F6KSTJ4Z6L58U)
[INFO] Total Amount (Buyer Pays): $1.00
[INFO] Platform Commission (15%): $0.15
[INFO] Seller Receives (Gross, 85%): $0.85
[INFO] Seller Will Pay PayPal Fees: ~$0.33 (2.9% + $0.30)
[INFO] Seller Net (After PayPal Fees): ~$0.52
[INFO] Platform Receives (Commission Only): $0.15 (NO PayPal fees deducted)
[INFO] ===============================================

... (user approves payment) ...

[INFO] === MULTI-PARTY CAPTURE RESULTS (Development) ===
[INFO] PayPal Order ID: 88F18931JY794935N
[INFO] Capture ID: 3AB12345CD678901E
[INFO] Status: COMPLETED
[INFO] --- Purchase Unit 1 ---
[INFO]   Payee (Merchant of Record): F6KSTJ4Z6L58U
[INFO]   Total Amount: $1.00
[INFO]   Captured Amount: $1.00
[INFO]   Seller Gross Amount: $1.00
[INFO]   PayPal Fee (Paid by Seller): $0.33
[INFO]   Seller Net Amount: $0.52
[INFO]   Platform Fee (Commission): $0.15
[INFO]   Platform Fee Recipient: PLATFORM_MERCHANT_ID
[INFO] ===============================================
```

## Troubleshooting

### Logs Not Appearing?

1. **Check environment**: Ensure `ASPNETCORE_ENVIRONMENT=Development`
2. **Check log level**: Logs are at INFO level, ensure logging is configured to show INFO
3. **Check transaction type**: Logs only appear for multi-party orders (seller content only)

### Understanding Discrepancies

- **Estimated vs Actual Fees**: Estimates use 2.9% + $0.30, but actual fees may vary slightly
- **Currency Conversion**: All amounts shown in USD
- **Rounding**: Some amounts may be rounded differently in logs vs PayPal

## References

- PayPal Multi-Party Documentation: https://developer.paypal.com/docs/multiparty/
- Platform Fees: https://developer.paypal.com/docs/multiparty/#-partner-fees
- Seller Receivable Breakdown: https://developer.paypal.com/docs/api/orders/v2/#definition-capture
