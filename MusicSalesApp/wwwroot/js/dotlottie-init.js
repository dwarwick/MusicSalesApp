// Boots the DotLottie web component from our own origin instead of unpkg/jsdelivr.
//
// Vendored at @lottiefiles/dotlottie-wc 0.8.11 (wwwroot/lib/dotlottie). The player's
// WASM renderer is NOT resolved relative to the script - chunk-43MLN37R hard-defaults
// _wasmURL to "https://cdn.jsdelivr.net/npm/@lottiefiles/dotlottie-web@0.58.1/dist/
// dotlottie-player.wasm" with unpkg as a backup - so dropping the .wasm beside the js
// does nothing on its own. setWasmUrl() is the only way to redirect it.
//
// `a` is the main-thread player, `b` the worker variant; both carry a static
// setWasmUrl(). chunk-Y4HD3GK2 (the web component itself) imports `a` from this same
// URL, so ES module singleton semantics guarantee it sees what we set here.
// These single-letter names are minification artifacts of 0.8.11 - re-check them
// against the chunk's export list on any upgrade.
import { a as DotLottie, b as DotLottieWorker } from '../lib/dotlottie/chunk-43MLN37R.js';

// App.razor supplies the fingerprinted, immutable URL. The relative path is the
// fallback: it resolves to the same file on the plain no-cache route, so a missing
// global costs a revalidation per load rather than breaking playback.
const wasmUrl = window.__stDotLottieWasmUrl
    || new URL('../lib/dotlottie/dotlottie-player.wasm', import.meta.url).href;

DotLottie.setWasmUrl(wasmUrl);
DotLottieWorker.setWasmUrl(wasmUrl);

// Dynamic rather than static: a static import is hoisted above the calls above, and
// registering <dotlottie-wc> upgrades any element already in the prerendered DOM,
// which starts a load. Register only once the WASM URL is pointed at our origin.
//
// If this ever fails, the loader falls back to jsdelivr then unpkg, so a bad local
// path degrades to the previous CDN behaviour rather than breaking playback.
await import('../lib/dotlottie/dotlottie-wc.js');
