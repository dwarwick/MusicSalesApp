# Plan: Static SSR for Sitemap Pages Only

Reset all branch changes and redo the static SSR migration scoped **ONLY to pages in the sitemap.xml**. The original branch accidentally included ManageAccount PayPal refactoring and SubscriptionController changes that are out of scope.

---

### Scope

**IN SCOPE** — sitemap pages + playlist player:
- `/` (Home), `/song/{SongTitle}`, `/artist/{ArtistName}`, `/genre/{GenreName}`, `/playlist/{PlaylistId}`, `/LearnMore`, `/privacy-policy`, `/terms-of-use`, `/creator-agreement`, `/user-refund-policy`
- Shared layout: MainLayout, NavMenu, ThemeProvider

**OUT OF SCOPE** — no code logic changes to:
- ManageAccount.razor.cs (no PayPal refactoring)
- SubscriptionController.cs (no service extraction)
- No new `PayPalSubscriptionApiService`
- No `BlazorBase` additions for `IPurchaseEmailService` / `IPayPalSubscriptionApiService`

---

### Steps

**Phase 1: Reset** *(destructive — requires confirmation)*
1. Identify base branch/commit to reset to
2. `git reset --hard <base-commit>` to undo ALL branch commits and working changes
3. Confirm clean state

**Phase 2: Global Render Mode — Static SSR Default**
4. In App.razor: Remove `@rendermode` from `<Routes>` and `<HeadOutlet>` so all pages default to static SSR

**Phase 3: Non-Sitemap Pages — Add `@rendermode InteractiveServer`** *(parallel with Phase 4)*
5. Add `@rendermode InteractiveServer` (one line, no code logic changes) to ~19 non-sitemap pages: Login, Register, ManageAccount, ForgotPassword, ResetPassword, VerifyEmail, Logout, MyPlaylists, all Admin pages, Creator pages, UploadFiles, SubmitTaxForm, MusicLibrary

**Phase 4: Sitemap Pages — Static Wrapper + Interactive Child**

**SongPlayer (steps 6-8):**
6. Refactor `SongPlayer.razor` to static wrapper: `@page "/song/{SongTitle}"`, SEO-friendly hidden `<h1>` with title/artist/genre
7. Create `SongPlayerInteractive.razor` + `.razor.cs` child component with all player UI, inherits `BlazorBase`
8. Create `SongPlayer.razor.cs` as lightweight `ComponentBase` that fetches metadata for SEO in `OnInitializedAsync`. Rename JS/CSS files to `SongPlayerInteractive.razor.*`

**PlaylistPlayer (steps 9-11):**
9. Refactor `PlaylistPlayer.razor` to static wrapper with all `@page` routes, SEO content
10. Create `PlaylistPlayerInteractive.razor` + `.razor.cs` child component, inherits `BlazorBase`
11. Create `PlaylistPlayer.razor.cs` (`PlaylistPlayerWrapperModel : ComponentBase`) for SEO. Rename JS/CSS files.

**Home (steps 12-13):**
12. Keep `Home.razor` static. Extract "User Playlists" into `HomeUserPlaylists.razor` with `@rendermode="new InteractiveServerRenderMode(prerender: false)"`
13. Ensure `<MusicLibrary ShowHomePageFeatured="true" />` in Home.razor gets explicit `@rendermode` if it needs interactivity

14. Home is mostly static, but has some buttons at the bottom of the page. It also needs to check authentication state and roles. For buttons,
we can use an A tag with href and style it so that it looks exactly like it does now. Use syncfusion classes to ensure it looks like a syncfusion button. This way, we can avoid making Home an interactive page and keep it static for SEO purposes.

**Phase 5: Shared Layout**
15. In `MainLayout.razor`: Ensure `<NavMenu @rendermode="InteractiveServer" />` and `<ThemeProvider @rendermode="InteractiveServer" />`

**Phase 6: LearnMore**
16. LearnMore is mostly static, but has some buttons at the bottom of the page. It also needs to check authentication state and roles.

17. LearnMore is mostly static, but has some buttons at the bottom of the page. It also needs to check authentication state and roles. For buttons,
we can use an A tag with href and style it so that it looks exactly like it does now. Use syncfusion classes to ensure it looks like a syncfusion button. This way, we can avoid making LearnMore an interactive page and keep it static for SEO purposes.

**Phase 6: Verify Static Pages**
18. Confirm TermsOfUse, PrivacyPolicy, CreatorAgreement, UserRefundPolicy have no `@rendermode` — they're already pure static

---

### Verification
1. **View Source** each sitemap page as unauthenticated — HTML should contain real text, not just loading spinners
2. **Interactive features** on SongPlayer/PlaylistPlayer still work (audio, controls, cart, likes)
3. **Home page** hero section visible in source, HomeUserPlaylists loads interactively for logged-in users
4. **Non-sitemap pages** (ManageAccount, Login, Admin) still fully functional
5. **NavMenu** sidebar toggle and theme switching work on all pages
6. `dotnet test` passes on both test projects
7. `git diff` confirms **zero changes** to ManageAccount.razor.cs code logic, SubscriptionController logic, or PayPal files

---

### Decisions
- Playlist player **included** despite not being in sitemap (user request)
- **No PayPal/subscription refactoring** — ManageAccount keeps its existing HTTP-based flow
- Non-sitemap pages get **only** a `@rendermode` directive addition — no code logic changes
- Static wrapper code-behinds use **`ComponentBase`** (not `BlazorBase`) — only inject the specific services needed for SEO metadata

---

### Further Consideration
1. `<MusicLibrary ShowHomePageFeatured="true" />` embedded in Home.razor — does it need interactivity (play buttons)? If yes, it needs `@rendermode="new InteractiveServerRenderMode(prerender: false)"`. If it just shows links, it can stay static.
