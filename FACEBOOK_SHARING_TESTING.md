# Facebook Sharing Feature - Testing Guide

## Overview
This document describes the Facebook sharing feature that has been implemented and how to test it.

## What Was Implemented

### 1. Configuration
- Added `Facebook:AppId` configuration to `appsettings.json`
- This should be replaced with your actual Facebook App ID

### 2. Open Graph Meta Tags
- Created `OpenGraphService` to generate Open Graph meta tags dynamically
- Meta tags are generated for:
  - Song pages: `/song/{song-title}`
  - Album pages: `/album/{album-name}`
- Tags include:
  - `fb:app_id` - Facebook App ID
  - `og:url` - Current page URL
  - `og:type` - music.song or music.album
  - `og:title` - Song/Album title
  - `og:image` - Album art image
  - `og:description` - Description text
  - `music:genre` - Genre (for songs/albums)
  - `music:duration` - Duration in seconds (for songs)

### 3. Facebook SDK Integration
- Added Facebook JavaScript SDK to `App.razor`
- SDK version: v18.0
- Supports Facebook's XFBML parsing for share buttons

### 4. FacebookShareButton Component
- Reusable Blazor component in `Components/Shared/FacebookShareButton.razor`
- Supports different layouts: button, button_count, box_count, icon_link
- Supports different sizes: small, medium, large
- Fallback to direct Facebook share link if SDK not loaded

### 5. Share Buttons Added To:
- **Music Library cards** (both album and song cards)
  - Positioned between Like/Dislike and View buttons
  - Uses icon_link layout for compact display
- **Song Player page**
  - Positioned in action controls area
  - Uses button_count layout with large size
- **Album Player page**
  - Positioned in action controls area
  - Uses button_count layout with large size
  - Only shown for albums (not playlists)

## Prerequisites for Testing

1. **Facebook App Setup**
   - Create a Facebook App at https://developers.facebook.com
   - Get your App ID
   - Add your domain to the app's allowed domains
   - Enable Facebook Login product

2. **Update Configuration**
   ```json
   "Facebook": {
     "AppId": "YOUR_ACTUAL_FACEBOOK_APP_ID"
   }
   ```

3. **Public Domain**
   - Facebook Open Graph requires a publicly accessible domain
   - **IMPORTANT**: Localhost testing will show the favicon instead of actual images
     - This is expected: Facebook's crawler cannot access `https://localhost:7173` URLs
     - Images will work correctly when deployed to production (`https://streamtunes.net`)
   - For pre-production testing, you can use:
     - ngrok or similar tunneling service to expose localhost
     - Or deploy to a staging environment
   - The domain must match what's configured in your Facebook App

## Manual Testing Steps

### Test 1: Verify Meta Tags Generation

1. Navigate to a song page: `https://streamtunes.net/song/Test%20Song`
2. View page source (Ctrl+U or Cmd+Option+U)
3. Verify the following meta tags exist in `<head>`:
   ```html
   <meta property="fb:app_id" content="YOUR_APP_ID">
   <meta property="og:url" content="https://streamtunes.net/song/Test%20Song">
   <meta property="og:type" content="music.song">
   <meta property="og:title" content="Test Song">
   <meta property="og:image" content="https://streamtunes.net/api/music/stream?path=...">
   <meta property="og:description" content="Listen to Test Song on StreamTunes">
   <meta property="music:genre" content="Rock">
   <meta property="music:duration" content="245">
   ```

4. Repeat for an album page: `https://streamtunes.net/album/Test%20Album`
   - Verify `og:type` is "music.album"
   - Verify track count appears in description

### Test 2: Verify Share Buttons Render

1. **Music Library**:
   - Navigate to `/music-library`
   - Verify share button appears on each album card
   - Verify share button appears on each song card
   - Buttons should be between like/dislike and view buttons

2. **Song Player**:
   - Navigate to any song page (e.g., `/song/Test%20Song`)
   - Verify share button appears next to the play button
   - Button should show share count if any shares exist

3. **Album Player**:
   - Navigate to any album page (e.g., `/album/Test%20Album`)
   - Verify share button appears next to the play button
   - Button should NOT appear on playlist pages

### Test 3: Verify Share Functionality

1. Click a share button
2. Facebook share dialog should open in a popup
3. Verify the preview shows:
   - Correct page title
   - Album/song artwork
   - Description text
   - Correct URL

4. Complete the share (or cancel)
5. If completed, the share count should increment

### Test 4: Facebook Debugger Tool

1. Go to https://developers.facebook.com/tools/debug/
2. Enter a song or album URL (e.g., `https://streamtunes.net/song/Test%20Song`)
3. Click "Fetch new information"
4. Verify:
   - All Open Graph tags are detected
   - Image preview displays correctly
   - No warnings or errors
5. Use "See exactly what our scraper sees" to view raw HTML

### Test 5: Mobile Responsiveness

1. Test share buttons on mobile devices
2. Verify buttons are appropriately sized
3. Verify share dialog works on mobile browsers

## Troubleshooting

### Facebook Shows Favicon Instead of Album/Song Art (Localhost Only)
**This is expected behavior when testing from localhost!**
- Facebook's Open Graph crawler cannot access `https://localhost:7173` URLs
- The image URLs in the meta tags are correct, but Facebook can't fetch them
- **Solution**: This will work correctly when deployed to production at `https://streamtunes.net`
- **Verification**: Check page source to confirm `og:image` meta tag has correct image path
- **Pre-production testing**: Use ngrok to temporarily expose localhost publicly

### Meta Tags Not Showing
- Check that `OpenGraphService` is registered in `Program.cs`
- Verify `App.razor.cs` is generating meta tags in `OnInitializedAsync`
- Check browser dev tools for any JavaScript errors

### Share Button Not Appearing
- Verify `FacebookShareButton` component import in `_Imports.razor`
- Check that Facebook SDK is loading (look for `fb-root` div in page source)
- Check browser console for SDK loading errors

### Share Preview Incorrect (Production)
- Run URL through Facebook Debugger
- Facebook caches share previews - use "Fetch new information" to update
- Verify image URLs are publicly accessible (not localhost)
- Check that meta tags contain proper HTML entity encoding

### Share Button Shows Wrong URL
- Verify `GetShareUrl()` methods in code-behind files
- Check `NavigationManager.BaseUri` is correct
- Verify URL encoding for special characters

## Code Locations

- **Configuration**: `MusicSalesApp/appsettings.json`
- **OpenGraph Service**: `MusicSalesApp/Services/OpenGraphService.cs`
- **App Component**: `MusicSalesApp/Components/App.razor` and `App.razor.cs`
- **Share Button Component**: `MusicSalesApp/Components/Shared/FacebookShareButton.razor`
- **CSS Styles**: `MusicSalesApp/wwwroot/app.css` (search for "Facebook Share")
- **Unit Tests**: `MusicSalesApp.Tests/Services/OpenGraphServiceTests.cs`

## Expected Behavior

### Share URLs
- Song: `https://streamtunes.net/song/{url-encoded-song-title}`
- Album: `https://streamtunes.net/album/{url-encoded-album-name}`
- Playlist: `https://streamtunes.net/playlist/{playlist-id}` (no share button shown)

### Share Button Layouts
- Music Library cards: icon_link (small, icon only)
- Song/Album players: button_count (shows share count)

### Open Graph Types
- Songs: `music.song`
- Albums: `music.album`

## Notes

- Share counts are maintained by Facebook, not stored in the application
- Meta tags are generated server-side during page render
- Facebook SDK loads asynchronously and doesn't block page rendering
- Share functionality works without JavaScript using fallback link
- Special characters in titles are URL-encoded properly
- HTML special characters in meta tags are properly escaped

## Success Criteria

✅ Meta tags render correctly for song and album pages
✅ Share buttons appear in all specified locations
✅ Clicking share button opens Facebook share dialog
✅ Share preview shows correct information
✅ Share count increments after successful share
✅ All unit tests pass (8/8 for OpenGraphService)
✅ Feature works on desktop and mobile browsers
