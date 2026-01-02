# PayPal Fee Responsibility - Clarification

## Question
When a merchant onboarded seller sells their music at checkout, does each seller pay their own PayPal fees, or does the platform pay the PayPal fees?

## Answer
**The platform pays all PayPal fees.**

## Why This Implementation

The application uses PayPal's JavaScript SDK for checkout, which is initialized with the **platform's** PayPal client ID. This means:

1. **All payments go to the platform's PayPal account** (for both platform and seller content)
2. **Platform is the merchant of record** for all transactions
3. **Platform pays all PayPal transaction fees** (typically 2.9% + $0.30)
4. **Platform is responsible for paying sellers** their 85% share

## Payment Flow

### For Platform Content
```
Customer $10 → Platform's PayPal
               → PayPal fee: -$0.59
               → Platform keeps: $9.41
```

### For Seller Content
```
Customer $10 → Platform's PayPal
               → PayPal fee: -$0.59
               → Platform receives: $9.41
               → Platform keeps 15% commission: $1.41
               → Platform must pay seller: $8.00 (80% of gross)
```

## Seller Onboarding

The PayPal Partner Referrals onboarding flow creates seller PayPal accounts, but these are **not used for checkout**. Instead, they would be used for:
- Identifying the seller in PayPal's system
- Potentially for future features (direct payouts, refunds, etc.)
- Compliance and verification purposes

## Why Not Multi-Party Checkout?

Implementing multi-party checkout where the seller is the merchant of record would require:
1. Different checkout flow (not using PayPal JavaScript SDK)
2. Direct redirection to PayPal approval URLs
3. Complex handling of returns and captures
4. Potential issues with user experience (extra redirects)

The current approach is simpler and common for marketplace platforms.

## Commission Structure

With the platform as merchant of record:
- Platform revenue per $10 sale: $1.41 (15% commission - PayPal fees)
- Seller payout per $10 sale: $8.00 (85% of gross, but calculated after platform receives net)
- Platform's effective margin: 14.1% after fees

## Alternative: Adjust Commission Rate

If the platform wants to maintain a 15% net margin, the commission rate could be adjusted to account for PayPal fees:
- Set commission at ~21% to keep ~15% after paying fees and sellers
- Or adjust seller payouts to be based on net received (after PayPal fees)

## Current State

The current implementation:
- ✅ Correctly collects payments through platform's PayPal account
- ✅ Platform pays all PayPal fees
- ✅ Sellers are onboarded and verified through PayPal
- ⚠️  Platform must implement payout system to pay sellers their 85% share
- ⚠️  Platform's profit margin is reduced by PayPal fees

## Recommendation

This is a valid approach used by many marketplace platforms (Etsy, eBay, etc.). The platform:
1. Acts as payment facilitator
2. Takes on payment processing costs
3. Handles seller payouts separately
4. Maintains control over the payment experience
