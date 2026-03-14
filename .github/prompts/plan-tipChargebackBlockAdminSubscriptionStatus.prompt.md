## Plan: Tip Chargeback Block + Admin Subscription Status

When a tipper files a chargeback, block them from tipping again by adding an `IsTipBlocked` flag to `ApplicationUser`. Send the tipper an email explaining the chargeback and the permanent tipping ban. Expose tip-block status and subscription status on the Admin Manage Users page, with the ability for admin to re-enable tipping.

---

### Phase 1: Database & Model Changes

1. **Add `IsTipBlocked` and `TipBlockedAt` to `ApplicationUser`** — follows the exact pattern of existing `IsSubscriptionBlocked`/`SubscriptionBlockedAt` fields.
   - File: [ApplicationUser.cs](MusicSalesApp/Models/ApplicationUser.cs)

2. **Create EF migration** for the two new columns.

---

### Phase 2: Tip Chargeback Logic — Block Tipper & Send Email

3. **Block tipper in `TryHandleTipChargebackAsync`** — after updating tip status to Chargeback, set `user.IsTipBlocked = true` and `user.TipBlockedAt = DateTime.UtcNow` on the `TipperUser`. Follows the same pattern used in `ProcessSubscriptionChargebackAsync` where `IsSubscriptionBlocked` is set. *(depends on step 1)*
   - File: [PayPalWebhookController.cs](MusicSalesApp/Controllers/PayPalWebhookController.cs) — `TryHandleTipChargebackAsync` method

4. **Send tipper chargeback email** — new method `SendTipperChargebackEmailAsync` following the pattern of existing `SendSubscriberChargebackEmailAsync`. Includes: chargeback notification, statement that tipping is permanently revoked, support contact. Called from `TryHandleTipChargebackAsync` for ALL tip chargebacks (not just paid-out ones). *(parallel with step 3)*
   - File: [PayPalWebhookController.cs](MusicSalesApp/Controllers/PayPalWebhookController.cs)

5. **Add tip-block check in `ValidateTipAsync`** — after loading the user, check `user.IsTipBlocked` and return a rejection message. *(depends on step 1)*
   - File: [TipService.cs](MusicSalesApp/Services/TipService.cs) — `ValidateTipAsync` method

---

### Phase 3: Admin Manage Users — Tip Block Status & Toggle

6. **Add `IsTipBlocked`/`TipBlockedAt` to `UserViewModel`** — map in `LoadUsersAsync`, add `_editIsTipBlocked` field, wire into `EditUser`/`SaveEdit`. *(depends on step 1)*
   - File: [AdminUserManagement.razor.cs](MusicSalesApp/Components/Pages/AdminUserManagement.razor.cs)

7. **Add grid columns "Tip Blocked" and "Tip Blocked At"** — badge template (Yes=red, No=gray), follows exact pattern of "Sub Blocked" column. *(depends on step 6)*
   - File: [AdminUserManagement.razor](MusicSalesApp/Components/Pages/AdminUserManagement.razor)

8. **Add "Tip Blocked" checkbox** in the edit modal — next to existing "Subscription Blocked" checkbox. *(depends on step 6)*
   - File: [AdminUserManagement.razor](MusicSalesApp/Components/Pages/AdminUserManagement.razor)

---

### Phase 4: Admin Manage Users — Subscription Status Column

9. **Add `SubscriptionStatus` display string to `UserViewModel`** — derived at load time from `Subscriptions` table + `IsSubscriptionBlocked`:
   - If `IsSubscriptionBlocked` → **"Blocked"** (red badge)
   - Else if latest subscription is `ACTIVE` and not expired → **"Active"** (green)
   - Else if latest subscription is `CANCELLED`/`SUSPENDED`/`EXPIRED` → **"Cancelled"** (yellow)
   - Else (no subscription) → **"Not Subscribed"** (gray)
   - File: [AdminUserManagement.razor.cs](MusicSalesApp/Components/Pages/AdminUserManagement.razor.cs) — `LoadUsersAsync`

10. **Add "Sub Status" grid column** — color-coded badge column placed near existing "Sub Blocked" column.
    - File: [AdminUserManagement.razor](MusicSalesApp/Components/Pages/AdminUserManagement.razor)

---

### Phase 5: Tests & Verification

11. **Update `AdminUserManagementTests`** — verify new columns render ("Tip Blocked", "Tip Blocked At", "Sub Status").
    - File: [AdminUserManagementTests.cs](MusicSalesApp.ComponentTests/Components/AdminUserManagementTests.cs)

12. **Add/update TipService tests** — test `ValidateTipAsync` rejects when `IsTipBlocked = true`.
    - File: `MusicSalesApp.Tests/Services/TipServiceTests.cs`

13. **Run all existing tests** to catch regressions.

---

### Verification

1. `dotnet build MusicSalesApp.slnx` — solution compiles
2. `dotnet test MusicSalesApp.slnx` — all tests pass
3. Migration generates correctly with the two new columns
4. Manual: Admin Manage Users page shows new "Tip Blocked", "Tip Blocked At", and "Sub Status" columns
5. Manual: Edit modal allows toggling "Tip Blocked" and it persists on save

### Decisions

- `IsTipBlocked`/`TipBlockedAt` follows the exact same model pattern as `IsSubscriptionBlocked`/`SubscriptionBlockedAt`
- Subscription status is **derived** at query time (not a stored field) to avoid staleness
- "Blocked" status takes priority over actual subscription state
- The tipper email is sent for **all** tip chargebacks (not just ones already paid out — the existing creator email is only for paid-out tips)
- No additional constants needed in Common/Helpers since the tip-block message is a one-time validation string, not written/read in separate places

### Further Considerations

1. **Should re-enabling tipping send an email to the user?** Yes, I think it's good to notify the user when their tipping privileges are restored. I can add a new `SendTipReenabledEmailAsync` method following the pattern of the chargeback email, and call it from `SaveEdit` when an admin unchecks "Tip Blocked". The email would explain that their tipping privileges have been restored and they can tip again.
2. **Should "Blocked" subscription status be a separate value in the grid vs the existing "Sub Blocked" column?** My plan adds a new "Sub Status" column with derived status (which subsumes the info from "Sub Blocked"), but keeps the existing "Sub Blocked" column for backward compatibility. If you'd prefer to remove the redundancy, I can adjust. We do not need a redundant "Tip Blocked" column since the "Tip Blocked At" column already indicates if they are blocked (null = not blocked, timestamp = blocked).

We also need to update terms of service and the creator agreement to reflect the chargeback policy for subscriptions and tips. Ensure t he dates are updated in these documents. 