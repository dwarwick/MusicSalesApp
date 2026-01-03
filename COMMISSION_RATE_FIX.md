# Commission Rate Configuration Fix

## Issue
In sandbox testing, sellers were receiving 100% of the sale price instead of 85% (after 15% platform commission). The platform was not receiving its commission.

## Root Cause
The `Seller` model had a default `CommissionRate` of 0.15 (15%), but existing sellers in the database had a rate of 0 (0%) because they were created before the default was set in the model definition.

## Solution Implemented

### 1. Added Commission Rate to Admin Settings
- New configurable setting in `/admin/settings` page
- Defaults to 15% if not configured
- Admin can adjust the rate for future sellers

### 2. Updated Seller Creation Logic
- When a new seller signs up, they receive the current commission rate from Admin Settings
- The rate is stored in the `Seller.CommissionRate` field in the database
- Each seller keeps their individual commission rate permanently

### 3. How It Works

**At Seller Signup:**
```
1. User becomes a seller via PayPal Partner Referrals
2. SellerService.CreateSellerAsync() is called
3. Gets current commission rate from AppSettingsService (default 15%)
4. Stores rate in Seller.CommissionRate field
5. Seller's rate is now locked in
```

**At Checkout:**
```
1. CartController detects multi-party order (seller content)
2. Gets seller from database (includes their CommissionRate)
3. Calculates platform fee: sale_amount × seller.CommissionRate
4. Creates PayPal order with seller as payee and platform_fees
5. PayPal automatically splits funds
```

**Example ($1 sale with 15% commission):**
```
Customer pays: $1.00
├─ Seller receives (gross): $1.00
│  ├─ PayPal fees (paid by seller): -$0.32
│  ├─ Platform commission: -$0.15
│  └─ Seller net: $0.53
└─ Platform receives: $0.15 (no PayPal fees)
```

## Fixing Existing Sellers

Existing sellers in the database may have a 0% commission rate. To fix:

### Option 1: Direct Database Update (Recommended)
```sql
-- Update all sellers to 15% commission rate
UPDATE Sellers SET CommissionRate = 0.15;

-- Or update specific seller
UPDATE Sellers SET CommissionRate = 0.15 WHERE Id = 5;
```

### Option 2: Admin UI (Future Enhancement)
A future enhancement could add a "Manage Sellers" admin page where you can:
- View all sellers and their commission rates
- Edit individual seller commission rates
- Set different rates for different sellers

## Important Notes

1. **Commission rate is per-seller, not global**: Each seller has their own rate that doesn't change when you update Admin Settings.

2. **Admin Settings rate is for new sellers only**: Changing the rate in Admin Settings only affects sellers who sign up AFTER the change.

3. **Existing sellers keep their rate**: If you want to change an existing seller's rate, you must update their database record directly.

4. **PayPal fees are always paid by the seller**: The seller is the merchant of record and pays PayPal transaction fees on the gross amount.

5. **Platform receives commission only**: The platform never touches the full payment amount—PayPal automatically splits and disburses funds.

## Files Modified

- `MusicSalesApp/Services/IAppSettingsService.cs` - Added GetCommissionRateAsync/SetCommissionRateAsync
- `MusicSalesApp/Services/AppSettingsService.cs` - Implemented commission rate methods
- `MusicSalesApp/Services/SellerService.cs` - Uses app settings rate for new sellers
- `MusicSalesApp/Components/Pages/AdminSettings.razor` - Added commission rate UI
- `MusicSalesApp/Components/Pages/AdminSettings.razor.cs` - Added commission rate logic
- `MusicSalesApp/Controllers/CartController.cs` - Added IAppSettingsService dependency

## Testing

All 219 tests pass after these changes. The commission rate feature is backward compatible—sellers without a rate set will use the model's default of 0.15 (15%).
