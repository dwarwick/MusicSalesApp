# hls.js

Vendored, not referenced from a CDN — the same treatment `lib/dotlottie` gets, and for the same
reason: the only external script this site loads is Font Awesome, and a player library is a worse
thing to hand to a third party than an icon font.

| | |
|---|---|
| Version | 1.5.17 |
| Source | `https://cdn.jsdelivr.net/npm/hls.js@1.5.17/dist/hls.min.js` |
| SHA-256 | `484054e8cd03d3f6d1781fb7f402bdc318d8a4c527f933a95c624e27cc9a9470` |
| Licence | Apache-2.0 |

## Why it is needed

Encrypted HLS is the only way audio is served now (see `StreamController`). Safari plays an
`.m3u8` natively from an `<audio src>`; **no other browser does**, so without this Chrome, Firefox
and Edge would play nothing at all. `hls-player.js` picks between the two paths at runtime.

## What it must never be asked to do

It fetches the manifest and the AES key through ordinary same-origin requests, so the browser sends
the auth cookie automatically. Do not add `xhrSetup` to attach a token by hand — the credential is
already in the manifest URL for token-authenticated callers and in the cookie for everyone else, and
a second mechanism would be a second thing to get wrong.

## Upgrading

Replace the file, update the version and hash above, and re-run the manual playback checks in both
a Safari and a non-Safari browser. The two code paths are genuinely different: a regression in one
is invisible from the other.
