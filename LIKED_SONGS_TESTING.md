# Manual Testing Guide: Liked Songs Auto-Playlist Feature

## Overview
This feature automatically creates and maintains a "Liked Songs" system playlist that contains all songs a user has liked. The playlist is automatically updated when users like or unlike songs.

## Prerequisites
1. Application running with database connection configured
2. At least one test user account
3. Some songs uploaded to the music library

## Test Scenarios

### Test 1: Liked Songs Playlist Auto-Creation

**Objective:** Verify that the Liked Songs playlist is created automatically when a user likes their first song.

**Steps:**
1. Log in as a test user (e.g., `user@app.com` / `Password_123`)
2. Navigate to the Music Library
3. Find a song and click the "Like" button (thumbs up icon)
4. Navigate to "My Playlists" page
5. **Expected Result:**
   - A "Liked Songs" playlist should appear in the playlist list
   - The playlist should show "1 song(s)"
   - The playlist card should have a special CSS class `liked-songs-playlist-card`
   - NO edit or delete buttons should be visible on this playlist card

### Test 2: Liked Songs Playlist on Home Page

**Objective:** Verify that the Liked Songs playlist is displayed on the Home page when it contains songs.

**Steps:**
1. Ensure you have at least one liked song (from Test 1)
2. Navigate to the Home page
3. **Expected Result:**
   - A "Liked Songs" playlist card should appear in the "Your Playlists" section
   - The card should show the number of liked songs
   - The description should say "Songs you've liked"
   - The card should be displayed next to the "Recommended For You" playlist (if available)

### Test 3: Adding Songs to Liked Songs Playlist

**Objective:** Verify that liking additional songs adds them to the Liked Songs playlist.

**Steps:**
1. Navigate to Music Library
2. Like 2-3 more songs
3. Navigate to "My Playlists"
4. Click the "View" (eye icon) button on the Liked Songs playlist
5. **Expected Result:**
   - All liked songs should be listed in the playlist
   - Songs should appear in the order they were liked (most recent first)

### Test 4: Removing Songs from Liked Songs Playlist

**Objective:** Verify that unliking a song removes it from the Liked Songs playlist.

**Steps:**
1. Navigate to Music Library
2. Find a song you previously liked
3. Click the "Like" button again to unlike it
4. Navigate to "My Playlists"
5. Click "View" on the Liked Songs playlist
6. **Expected Result:**
   - The unliked song should no longer appear in the playlist
   - The song count should be decreased by 1

### Test 5: Liked Songs Playlist Cannot Be Edited

**Objective:** Verify that users cannot rename the Liked Songs playlist.

**Steps:**
1. Navigate to "My Playlists"
2. Locate the Liked Songs playlist card
3. **Expected Result:**
   - NO "Edit" button should be visible on the playlist card
   - The system should not allow renaming this playlist

**Technical Verification:**
- If attempting to call the API directly: `UpdatePlaylistAsync` should return `false` for system-generated playlists
- Check server logs for warning: "Cannot update system-generated playlist {PlaylistId}"

### Test 6: Liked Songs Playlist Cannot Be Deleted

**Objective:** Verify that users cannot delete the Liked Songs playlist.

**Steps:**
1. Navigate to "My Playlists"
2. Locate the Liked Songs playlist card
3. **Expected Result:**
   - NO "Delete" button should be visible on the playlist card
   - The system should not allow deleting this playlist

**Technical Verification:**
- If attempting to call the API directly: `DeletePlaylistAsync` should return `false` for system-generated playlists
- Check server logs for warning: "Cannot delete system-generated playlist {PlaylistId}"

### Test 7: Playing Liked Songs Playlist

**Objective:** Verify that clicking the play button navigates to the album player with the liked songs.

**Steps:**
1. Navigate to "My Playlists" or Home page
2. Click the "Play" button on the Liked Songs playlist card
3. **Expected Result:**
   - Should navigate to `/playlist/{playlistId}` URL
   - Album player should load with all liked songs
   - Songs should be playable in sequence
   - Track list should show all liked songs

### Test 8: Disliking a Song

**Objective:** Verify that disliking a previously liked song removes it from the Liked Songs playlist.

**Steps:**
1. Navigate to Music Library
2. Find a song you've liked
3. Click the "Dislike" button (thumbs down icon)
4. Navigate to "My Playlists"
5. View the Liked Songs playlist
6. **Expected Result:**
   - The disliked song should be removed from the Liked Songs playlist
   - Song count should decrease accordingly

### Test 9: Liked Songs with Subscription

**Objective:** Verify that subscribers can like songs they don't own and have them added to the playlist.

**Steps:**
1. Log in as a user with an active subscription
2. Navigate to Music Library
3. Like a song you haven't purchased
4. Navigate to "My Playlists"
5. View the Liked Songs playlist
6. **Expected Result:**
   - The song should appear in the Liked Songs playlist
   - A virtual `OwnedSong` record should be created with `PayPalOrderId = null`
   - User should be able to play the song from the playlist

### Test 10: Liked Songs After Subscription Expires

**Objective:** Verify that when a subscription expires, songs that were only accessible through subscription are removed from the Liked Songs playlist.

**Steps:**
1. As a subscriber, like some songs you don't own (from Test 9)
2. Cancel or let the subscription expire (48-hour grace period)
3. Wait for the PlaylistCleanupService background job to run
4. Navigate to "My Playlists"
5. View the Liked Songs playlist
6. **Expected Result:**
   - Songs that were only accessible through subscription (PayPalOrderId = null) should be removed
   - Songs that were purchased (PayPalOrderId set) should remain
   - The Liked Songs playlist should only contain owned songs

### Test 11: Empty Liked Songs Playlist Visibility

**Objective:** Verify that an empty Liked Songs playlist is handled appropriately.

**Steps:**
1. Unlike all your liked songs
2. Navigate to Home page
3. Navigate to "My Playlists"
4. **Expected Result:**
   - On Home page: The Liked Songs playlist card should NOT be displayed
   - On My Playlists page: The Liked Songs playlist should still appear but with "0 song(s)"

## Database Verification

After testing, you can verify the database state:

```sql
-- Check Liked Songs playlist exists and is marked as system-generated
SELECT * FROM Playlists WHERE PlaylistName = 'Liked Songs' AND IsSystemGenerated = 1;

-- Check user's liked songs
SELECT sl.*, sm.Mp3BlobPath 
FROM SongLikes sl 
JOIN SongMetadata sm ON sl.SongMetadataId = sm.Id 
WHERE sl.UserId = [USER_ID] AND sl.IsLike = 1;

-- Check songs in Liked Songs playlist
SELECT up.*, os.SongFileName, os.PayPalOrderId
FROM UserPlaylists up
JOIN OwnedSongs os ON up.OwnedSongId = os.Id
JOIN Playlists p ON up.PlaylistId = p.Id
WHERE p.PlaylistName = 'Liked Songs' 
  AND p.IsSystemGenerated = 1
  AND up.UserId = [USER_ID];
```

## Known Limitations

1. **UI Testing**: Full component tests with Syncfusion dialogs require additional mocking setup
2. **Background Jobs**: Subscription cleanup requires the background service to be running
3. **Real-time Updates**: If using multiple tabs/sessions, playlist may not update immediately without refresh

## Success Criteria

- ✅ Liked Songs playlist is created automatically on first like
- ✅ Liked Songs playlist is displayed on Home and My Playlists pages
- ✅ Playlist updates automatically when songs are liked/unliked
- ✅ Playlist cannot be edited or deleted by users
- ✅ Playlist is playable via the album player
- ✅ Subscription-based access is properly managed
- ✅ All service-level tests pass (7/7 passing)
- ✅ Database migration applies successfully

## Test Results

**Service Tests:** ✅ All passing (7/7)
**Integration Tests:** ✅ PlaylistService tests passing
**Component Tests:** ⚠️ Partial (2/6 passing - Syncfusion dialog mocking challenges)
**Manual Tests:** ⏳ Pending (requires database setup)
