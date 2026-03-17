# Plan: Planned Site Maintenance Notification

Add an admin-managed site maintenance notification that shows all visitors a pop-up dialog and a persistent nav bar warning label. Admin sets start/end times in Pacific Time on the Settings page; times are stored as UTC. A Hangfire job auto-resets expired windows for both site maintenance and TaxBandits.

---

## Phase 1: Service Layer

**Step 1** — Add site maintenance methods to `IAppSettingsService` / `AppSettingsService`
- New constants: `SiteMaintenanceStartUtcKey`, `SiteMaintenanceEndUtcKey`
- New methods: `Get/SetSiteMaintenanceStartUtcAsync`, `Get/SetSiteMaintenanceEndUtcAsync`, `ShouldShowSiteMaintenanceNoticeAsync` (true if end time > UtcNow and not DateTime.MinValue)
- Follows existing TaxBandits maintenance pattern

**Step 2** — Create `IMaintenanceResetService` / `MaintenanceResetService` *(new files)*
- `ResetExpiredMaintenanceWindowsAsync()`: checks both site and TaxBandits end times; if past, resets start/end to `DateTime.MinValue` and disables TaxBandits enabled flag

### Relevant Files (modify)
- `MusicSalesApp/Services/IAppSettingsService.cs` — add site maintenance method signatures
- `MusicSalesApp/Services/AppSettingsService.cs` — add constants + implementations

### Relevant Files (create)
- `MusicSalesApp/Services/IMaintenanceResetService.cs` — interface
- `MusicSalesApp/Services/MaintenanceResetService.cs` — implementation

---

## Phase 2: Timezone Helper Extraction

**Step 3** — Create `TimeZoneHelper` static class
- Move `MaintenanceLocalTimeInfo` class from `SubmitTaxForm.razor.cs` to `MusicSalesApp/Helpers/TimeZoneHelper.cs`
- Add static method `GetUserLocalTimeAsync(IJSRuntime js, DateTime? startUtc, DateTime? endUtc)` → returns `MaintenanceLocalTimeInfo`
- Calls existing JS function `getMaintenanceLocalTime` from `dashboard-helper.js`
- Includes try/catch with UTC fallback (same logic currently in SubmitTaxForm)

**Step 4** — Refactor `SubmitTaxForm.razor.cs` to use `TimeZoneHelper`
- Remove `MaintenanceLocalTimeInfo` class definition
- Replace inline JS interop block with call to `TimeZoneHelper.GetUserLocalTimeAsync(JS, ...)`

### Relevant Files (modify)
- `MusicSalesApp/Components/Pages/SubmitTaxForm.razor.cs` — refactor to use helper

### Relevant Files (create)
- `MusicSalesApp/Helpers/TimeZoneHelper.cs` — shared timezone conversion helper

---

## Phase 3: Admin Settings UI

**Step 5** — Add "Planned Site Maintenance" card to `AdminSettings.razor`
- New card section below TaxBandits Maintenance Window card
- Two `SfDateTimePicker` fields: Start Date/Time (Pacific) and End Date/Time (Pacific)
- Label: "All times are entered in Pacific Time (PT) and stored as UTC internally."
- Save/Cancel buttons following existing pattern
- Check if we can remove the Tax Bandits and Site Maintenance "enabled" toggle — if end time is in the future, maintenance is active; if end time is past or DateTime.MinValue, it's inactive
- Validation: end must be after start; both required

**Step 6** — Add code-behind fields and methods to `AdminSettings.razor.cs`
- Pacific timezone: `TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")`
- Fields: `_siteMaintenanceStartPacific`, `_siteMaintenanceEndPacific`, original values for change tracking, validation errors, success message, saving flag
- `_hasSiteMaintenanceChanges` computed property
- `LoadSettingsAsync` — load site maintenance times, convert UTC→Pacific
- `SaveSiteMaintenanceSettings()` — validate, convert Pacific→UTC, save via `AppSettingsService`
- `CancelSiteMaintenanceChanges()`

### Relevant Files (modify)
- `MusicSalesApp/Components/Pages/AdminSettings.razor` — new card section
- `MusicSalesApp/Components/Pages/AdminSettings.razor.cs` — new fields + save methods

---

## Phase 4: User-Facing Notifications

**Step 7** — Add centered warning label to `NavMenu.razor`
- On load, check `ShouldShowSiteMaintenanceNoticeAsync()`; if true, convert times to user's local TZ via `TimeZoneHelper`
- Render `<span class="maintenance-warning-label">` between logo and AppBarSpacer, centered via dual spacers
- Format: "⚠ Planned maintenance: {start} – {end} {TZ}"

**Step 8** — Add pop-up dialog in NavMenu
- `SfDialog` with `IsModal="true"`, `ShowCloseIcon="false"`, width ~450px
- Content: heading "Planned Maintenance Notice", maintenance times in user's timezone, acknowledge button (`SfButton`)
- On load: check `localStorage` key `maintenance_ack_{startISO}_{endISO}` via JS; if not set, show dialog
- On acknowledge: set localStorage key, close dialog — won't reappear even across sessions
- JS helper functions added to `dashboard-helper.js`:
  - `checkMaintenanceAcknowledged(key)` — returns bool from localStorage
  - `acknowledgeMaintenanceNotice(key)` — sets localStorage key

### Relevant Files (modify)
- `MusicSalesApp/Components/Layout/NavMenu.razor` — warning label + SfDialog
- `MusicSalesApp/Components/Layout/NavMenu.razor.cs` — maintenance state + JS interop
- `MusicSalesApp/wwwroot/js/dashboard-helper.js` — add localStorage helpers

---

## Phase 5: Auto-Reset via Hangfire

**Step 9** — Register hourly recurring job `"reset-expired-maintenance-windows"` in `BackgroundJobService.cs`

**Step 10** — Register DI: `IMaintenanceResetService` → `MaintenanceResetService` in `Program.cs`

### Relevant Files (modify)
- `MusicSalesApp/Services/BackgroundJobService.cs` — add recurring job
- `MusicSalesApp/Program.cs` — DI registration

---

## Phase 6: Styling (Light/Dark Mode)

**Step 11** — Add CSS styles
- **`light.css`**: `.maintenance-warning-label` color (dark text), `.maintenance-notice-dialog` background/text colors (use #1a1a2e text, #fff background, #1db954 accent)
- **`dark.css`**: `.maintenance-warning-label` color (light text), `.maintenance-notice-dialog` background (#2a2a2a), text (#e0e0e0), accent (#1db954)
- **`app.css`**: `.maintenance-warning-label` layout (font-size, font-weight, display flex, align-items center, gap), `.maintenance-notice-dialog` layout and button styling
- Nav bar warning label: small font, centered, with ⚠ icon, truncates on small screens
- **`sm_app.css`** / **`xs_app.css`**: possibly hide or shrink the label on very small screens

### Relevant Files (modify)
- `MusicSalesApp/wwwroot/light.css` — light theme colors
- `MusicSalesApp/wwwroot/dark.css` — dark theme colors
- `MusicSalesApp/wwwroot/app.css` — layout styles
- `MusicSalesApp/wwwroot/sm_app.css` — small screen responsive
- `MusicSalesApp/wwwroot/xs_app.css` — extra-small screen responsive

---

## Phase 7: Testing

**Step 12** — Unit tests for `MaintenanceResetService`
- Test: expired site maintenance times are reset to DateTime.MinValue
- Test: expired TaxBandits maintenance times are reset and disabled
- Test: future maintenance times are NOT reset
- Test: null/missing times handled gracefully

**Step 13** — Unit tests for new `AppSettingsService` methods
- Test: get/set site maintenance start/end UTC round-trip
- Test: `ShouldShowSiteMaintenanceNoticeAsync` returns correct status

**Step 14** — Component tests for NavMenu changes
- Test: warning label appears when maintenance is upcoming
- Test: warning label hidden when no maintenance
- Test: dialog visibility logic

**Step 15** — Verify existing `SubmitTaxForm` tests pass after refactor

### Relevant Files (create)
- `MusicSalesApp.Tests/Services/MaintenanceResetServiceTests.cs`

### Relevant Files (extend or create)
- `MusicSalesApp.Tests/Services/AppSettingsServiceTests.cs`
- `MusicSalesApp.ComponentTests/Components/NavMenuTests.cs`

---

## Decisions

- **Pacific Time** for admin input (user's timezone); TaxBandits stays Eastern
- **No "enabled" flag** for site maintenance — future end time = active
- **localStorage** keyed by `maintenance_ack_{startISO}_{endISO}` prevents re-showing
- **All visitors** (anonymous + authenticated) see notification
- **Show immediately** once admin saves, not just when start time arrives
- **Single Hangfire job** resets both site and TaxBandits expired maintenance windows

---

## Verification Checklist

1. `dotnet build` — no compilation errors
2. `dotnet test` — all existing + new tests pass
3. Admin > Settings → set Pacific times → verify UTC in DB
4. Other browser → pop-up appears with local time → acknowledge → doesn't reappear → nav bar warning visible
5. Set end time to past → trigger Hangfire job → times reset to DateTime.MinValue
6. Light/dark mode toggle → pop-up + warning look correct in both
7. TaxBandits maintenance auto-reset works the same way
