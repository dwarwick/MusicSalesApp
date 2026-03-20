## Plan: Dynamic Page Titles for Player Pages

**TL;DR:** Add dynamic browser tab titles to the playlist player and song player interactive components in the format `{Artist Name} - {Song Name} - StreamTunes`. The interactive components will add `<PageTitle>` tags that override the SSR fallback titles and update automatically as tracks change.

---

**Steps**

### Phase 1: PlaylistPlayerInteractive (steps 1-2)

1. **Add `<PageTitle>` to PlaylistPlayerInteractive.razor** — Insert at the very top of the markup (before the container `<div>`). Use a `GetPageTitle()` helper method.
2. **Add `GetPageTitle()` to PlaylistPlayerInteractive.razor.cs** — When a track is loaded: `"{artist} - {song} - StreamTunes"`. When no artist: `"{song} - StreamTunes"`. During loading: `GetDisplayTitle() + " - StreamTunes"` (keeps current fallback). Uses existing `GetArtistDisplayName(GetCurrentTrackMetadata())` and `GetTrackTitle(_currentTrackIndex)`. No extra `StateHasChanged()` needed — track changes already trigger re-renders.

### Phase 2: SongPlayerInteractive (steps 3-4, *parallel with Phase 1*)

3. **Add `<PageTitle>` to SongPlayerInteractive.razor** — Same pattern, insert at top.
4. **Add `GetPageTitle()` to SongPlayerInteractive.razor.cs** — Uses existing `GetArtistDisplayName()` and `GetDisplayTitle()`. Format: `"{artist} - {song} - StreamTunes"`, falls back to `"{song} - StreamTunes"` when artist is null.

### Phase 3: SSR consistency (step 5, *parallel with Phases 1-2*)

5. **Update SongPlayer.razor SSR `<PageTitle>`** — Currently `@_displayTitle - Stream on StreamTunes`. Update to `@_artistName - @_displayTitle - StreamTunes` when artist is available (improves SEO consistency with the interactive title). No changes needed on PlaylistPlayer.razor — its fallback `@_displayTitle - StreamTunes` is already correct.

---

**Relevant files**
- `MusicSalesApp/Components/Pages/PlaylistPlayerInteractive.razor` — Add `<PageTitle>` markup
- `MusicSalesApp/Components/Pages/PlaylistPlayerInteractive.razor.cs` — Add `GetPageTitle()` helper; reuses `GetArtistDisplayName()`, `GetCurrentTrackMetadata()`, `GetTrackTitle()`
- `MusicSalesApp/Components/Pages/SongPlayerInteractive.razor` — Add `<PageTitle>` markup
- `MusicSalesApp/Components/Pages/SongPlayerInteractive.razor.cs` — Add `GetPageTitle()` helper; reuses `GetArtistDisplayName()`, `GetDisplayTitle()`
- `MusicSalesApp/Components/Pages/SongPlayer.razor` — Update SSR title to include artist
- `MusicSalesApp/Components/App.razor` — Already has `<HeadOutlet @rendermode="InteractiveServer" />` (no changes)

**Verification**
1. `dotnet build` — confirm no compile errors
2. `dotnet test` on both test projects — ensure no regressions
3. Manual: navigate to `/genre/Country` → title starts as "Country - StreamTunes", updates to "Chris Warwick - When Nobody's Watching - StreamTunes" when track plays; click next → title updates
4. Manual: navigate to `/song/{title}` → title shows "{Artist} - {Song} - StreamTunes"

**Decisions**
- Title includes `- StreamTunes` suffix (confirmed)
- Fallback before playback keeps current behavior (confirmed)
- No new string constants needed — titles are dynamically composed display text, not lookup keys
