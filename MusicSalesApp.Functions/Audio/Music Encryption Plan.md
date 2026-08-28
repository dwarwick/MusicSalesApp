Here is a complete, production-ready architectural plan for securing your music streaming platform using **Method 1 (HLS \+ Dynamic ClearKey)**. You can copy and paste this directly into your documentation, system designs, or sprint planning tools.

## ---

**Architecture Plan: Secure Music Streaming with Azure Functions & Encrypted HLS**

## **1\. System Overview**

This architecture eliminates static MP3 links by transcoding audio into encrypted, short-segmented HLS data chunks (.ts files) managed via an index file (.m3u8). Encryption keys are hidden behind a token-gated API proxy, ensuring that only authenticated users with an active, short-lived session token can decrypt and stream the music.

`[Artist Uploads MP3] ➔ [Private Azure Blob Container]`  
                                 `│`  
                                 `▼ (Event Trigger)`  
                       `[Azure Function (FFmpeg)]`  
                                 `│`  
        `┌────────────────────────┴────────────────────────┐`  
        `▼ (Saves Audio Chunks)                             ▼ (Saves 16-Byte Secret Key)`  
`[Public Streaming Blob Container]                [Application Database / Key Vault]`  
        `│                                                 ▲`  
        `▼ (Requests Manifest)                             │ (Validates Token & Session)`  
 `[Web HLS Player] ───────────────────(Requests Key)───────┘`

## ---

**2\. Storage Setup (Azure Blob Storage)**

Create two distinct storage containers in your Azure Storage Account:

> 1. **incoming-music (Access Level: Private)**  
   * **Purpose:** Acts as a landing zone for raw, artist-uploaded MP3 files.  
   * **Security:** No public access. Frontend uploads directly to this folder using restricted, short-lived Write-Only SAS tokens.  
> 2. **streaming-media (Access Level: Blob / Anonymous Read Allowed for Chunks)**  
   * **Purpose:** Stores the processed .m3u8 manifests and encrypted .ts media fragments.  
   * **Security:** While the fragments are publicly readable, they are strongly encrypted with AES-128 and completely useless to a scraper without the corresponding key.

## ---

**3\. Media Processing Engine (Azure Function)**

Deploy an **Azure Function** (triggered by a new blob arrival in incoming-music) running a static Linux **FFmpeg** binary.

## **The Execution Workflow:**

> 1. **Key Generation:** The function generates a cryptographically secure random 16-byte key and a random Initialization Vector (IV).  
> 2. **Key Storage:** The function writes the SongID, EncryptionKey (hex format), and IV to your application database or Azure Key Vault.  
> 3. **FFmpeg Encryption Command:** The function spins up an FFmpeg sub-process using the following command structure to cut the audio into 4-second chunks and apply AES-128 encryption:

`ffmpeg -i input.mp3 \`  
  `-c:a aac -b:a 192k \`  
  `-hls_time 4 \`  
  `-hls_key_info_file enc_info.txt \`  
  `-hls_playlist_type vod \`  
  `-hls_segment_filename "streaming-media/song_%03d.ts" \`  
  `streaming-media/playlist.m3u8`

**Note on enc\_info.txt:** This internal file is constructed on-the-fly by your Azure Function before running FFmpeg. It tells FFmpeg where to point the player for the key. It looks exactly like this:

`https://yourdomain.com`  
`/tmp/actual_16_byte_key.key`  
`YOUR_HEX_IV_STRING`

## ---

**4\. The Client Request & Playback Pipeline**

When a listener clicks **"Play"** on a song, the system executes a tightly coordinated backend handshake:

## **Step A: Token-Gated Manifest Generation**

The frontend requests permission to play a track from your primary web backend server (e.g., /api/songs/get-manifest/123).

> 1. Your server verifies the user's login session/JWT token.  
> 2. Your server generates a **Short-Lived Streaming Token** (valid for **5–10 seconds max**) bound to the user's IP address.  
> 3. Your server reads the raw playlist.m3u8 from Azure Blob Storage and dynamically rewrites the key location line in memory before returning it to the browser player:

`# Real-time Manifest Output sent to browser:`  
`#EXTM3U`  
`#EXT-X-VERSION:3`  
`#EXT-X-TARGETDURATION:4`  
`#EXT-X-MEDIA-SEQUENCE:0`  
`#EXT-X-KEY:METHOD=AES-128,URI="https://yourdomain.com",IV=0x0123456789ABCDEF...`  
`#EXTINF:4.000000,`  
`song_000.ts`  
`#EXTINF:4.000000,`  
`song_011.ts`

## **Step B: Strict Key Verification Endpoint**

The HLS browser player (hls.js, Safari, etc.) parses the manifest, spots the custom URI="..." tag, and instantly sends a silent background network request to your key endpoint (/api/v1/keys/123?token=EXPIRED\_IN\_5\_SECONDS\_XYZ).

Your API application implements the following strict filters on this endpoint:

`[Incoming Key Request]`  
         `│`  
         `▼`  
`[Check 1: Request Origin] ─── Doesn't Match yourdomain.com ──➔ [HTTP 403 Forbidden]`  
         `│`  
         `▼ Matches`  
`[Check 2: Stream Token] ──── Token Expired or Invalid ───────➔ [HTTP 401 Unauthorized]`  
         `│`  
         `▼ Valid`  
`[Fetch Key from DB] ──────── Respond with Raw 16-byte Key ───➔ [Stream Decrypts in Browser Memory]`

> 1. **Strict CORS Check:** Reject the request immediately if the Origin or Referer headers do not match your platform's exact domain. Set Access-Control-Allow-Origin strictly to https://yourdomain.com (Never allow \*).  
> 2. **Token Validation:** Verify that the URL query token is present, hasn't expired, and matches the user's active session.  
> 3. **Response Payload:** If all checks pass, respond with an application/octet-stream content type containing the raw 16-byte cryptographic key.

## ---

**5\. Security & Operational Rules Summary**

> * **Ephemeral Tokens:** Because the streaming token expires in 5 seconds, by the time a user opens their Browser Inspector (F12) to copy the key URL from the network tab, the token is already invalid. Refreshing or sharing that URL will result in an immediate 401 Unauthorized error.  
> * **In-Memory Decryption:** The browser uses native Web Cryptography capabilities via the player library to decrypt the .ts files on the fly. The full unencrypted MP3 file never lands on the user's hard drive or network cache.  
> * **Azure Scaling Benefit:** Your expensive media bytes (.ts files) are served directly out of inexpensive Azure Blob Storage / CDN layers. Your web server only spends minimal CPU cycles verifying lightweight 16-byte key authorization requests.

Would you like to review a sample **Node.js script for the Azure Function** to see how to programmatically structure the FFmpeg execution, or would you prefer a **Frontend integration example using HLS.js** to handle the manifest loading?