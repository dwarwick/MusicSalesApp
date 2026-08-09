/**
 * Uploads files from the browser straight into Azure Blob Storage, using short-lived write SAS URLs
 * minted server-side.
 *
 * WHY THIS IS HAND-WRITTEN RATHER THAN @azure/storage-blob.
 *
 * That package ships no usable browser artifact. Its `dist/index.js` is CommonJS for Node - it
 * requires 'fs', 'os', 'crypto' and 'stream', so it fails on the first line in a browser - and
 * `dist-esm/` is ~90 unbundled files with bare specifiers that no browser resolves without a
 * bundler. This repo has no npm and no bundler by design. What is implemented here is the small part
 * of that SDK we actually need: Put Block, Put Block List, retry, progress and abort.
 *
 * WHY BLOCKS RATHER THAN ONE PUT.
 *
 * A single PUT works up to 256 MB and is far less code, but a failure at 90% of a 57 MB file costs
 * the whole file. Blocks let a flaky connection retry ~4 MB instead.
 *
 * REST reference: PUT {url}&comp=block&blockid={id} then PUT {url}&comp=blocklist with an XML
 * manifest. Block IDs must be base64 and - the part that silently breaks uploads - every ID in one
 * blob must decode to the SAME byte length, which is why the index is zero-padded before encoding.
 */

const BLOCK_SIZE = 4 * 1024 * 1024;
const MAX_ATTEMPTS_PER_BLOCK = 4;
const RETRY_BASE_DELAY_MS = 500;

/** Files below this go up in one request; blocks would be pure overhead. */
const SINGLE_PUT_THRESHOLD = BLOCK_SIZE;

/** Progress is coalesced to at most one report per file per this interval. */
const PROGRESS_INTERVAL_MS = 250;

/** All files' progress is flushed to .NET in one batched call on this interval. */
const PROGRESS_FLUSH_MS = 500;

/** Live upload sessions, so abortAll can reach them. Keyed by phase id. */
const sessions = new Map();

/**
 * Uploads a set of files. Returns immediately; results arrive via the .NET callbacks.
 *
 * Deliberately fire-and-forget: Blazor's JSInteropDefaultCallTimeout is one minute, so awaiting a
 * 57 MB upload from C# would time out and kill the circuit's call. Everything long comes back the
 * other way, through dotNetRef.
 *
 * @param {string} phaseId              Identifies this batch to the .NET side.
 * @param {Array<{index:number,target:{sasUri:string,contentType:string}}>} items
 * @param {number} concurrency          How many files at once.
 * @param {object} dotNetRef            DotNetObjectReference for the callbacks.
 */
export function uploadFiles(phaseId, items, concurrency, dotNetRef) {
    const input = document.querySelector('input[type=file].direct-upload-input');
    const files = input ? input.files : null;

    if (!files || files.length === 0) {
        // Nothing to work with. Report rather than silently stalling - a phase that never completes
        // leaves the creator watching a bar forever, which is the failure mode this whole change
        // exists to remove.
        dotNetRef.invokeMethodAsync('PhaseCompleted', phaseId, items.map(i => ({
            index: i.index,
            ok: false,
            httpStatus: 0,
            errorCode: 'NoFileList',
            errorMessage: 'The browser no longer has the selected files. Please choose them again.'
        })));
        return;
    }

    const session = {
        aborted: false,
        controllers: new Map(),
        pending: new Map(),
        flushTimer: null
    };
    sessions.set(phaseId, session);

    session.flushTimer = setInterval(() => flushProgress(phaseId, session, dotNetRef), PROGRESS_FLUSH_MS);

    runPhase(phaseId, session, items, files, concurrency, dotNetRef);
}

/** Cancels everything in flight. Without this a cancelled circuit leaves the browser still PUTting. */
export function abortAll() {
    for (const [phaseId, session] of sessions) {
        session.aborted = true;
        for (const controller of session.controllers.values()) {
            try { controller.abort(); } catch { /* already finished */ }
        }
        stopFlush(session);
        sessions.delete(phaseId);
    }
}

async function runPhase(phaseId, session, items, files, concurrency, dotNetRef) {
    const results = [];
    const queue = items.slice();
    const width = Math.max(1, Math.min(concurrency || 4, queue.length));

    const workers = Array.from({ length: width }, async () => {
        while (queue.length > 0 && !session.aborted) {
            const item = queue.shift();
            const file = files[item.index];

            if (!file) {
                results.push(failure(item.index, 0, 'FileMissing', 'That file is no longer available in the browser.'));
                continue;
            }

            const result = await uploadOne(phaseId, session, item, file, dotNetRef);
            results.push(result);

            if (result.ok) {
                // Reported per file rather than only at the end, so .NET can start the next stage for
                // this song immediately instead of waiting for its slowest sibling.
                try { await dotNetRef.invokeMethodAsync('FileUploaded', phaseId, item.index); }
                catch { /* circuit gone; the phase result below is best effort anyway */ }
            }
        }
    });

    try {
        await Promise.all(workers);
    } finally {
        stopFlush(session);
        flushProgress(phaseId, session, dotNetRef);
        sessions.delete(phaseId);
    }

    try {
        await dotNetRef.invokeMethodAsync('PhaseCompleted', phaseId, results);
    } catch { /* circuit gone */ }
}

/**
 * Asks .NET for a fresh write token for this file, and swaps it into the item in place.
 *
 * A write token lives 30 minutes and is minted once, before the transfer starts. The admin audio cap
 * is 150 MB - well over half an hour on a slow residential uplink - so expiry mid-transfer is a
 * reachable state rather than a theoretical one, and without this the whole file is discarded at the
 * moment it fails, having already been sent.
 *
 * Renewal is by SLOT, never by path: .NET holds the paths it minted and the browser can only say
 * "the file at index N", exactly as with the original mint. Returns false when the circuit is gone
 * or the server declines, and the caller then fails the file as before.
 */
async function renewTarget(item, dotNetRef) {
    try {
        const renewed = await dotNetRef.invokeMethodAsync('RenewUploadTarget', item.index);
        if (!renewed || !renewed.sasUri) {
            return false;
        }

        item.target = { sasUri: renewed.sasUri, contentType: renewed.contentType };
        return true;
    } catch {
        return false;
    }
}

async function uploadOne(phaseId, session, item, file, dotNetRef) {
    const controller = new AbortController();
    session.controllers.set(item.index, controller);

    try {
        const attempt = () => file.size <= SINGLE_PUT_THRESHOLD
            ? putWholeBlob(phaseId, session, item, file, controller, dotNetRef)
            : putBlocks(phaseId, session, item, file, controller, dotNetRef);

        const result = await attempt();

        // One renewal, then one retry. A second 403 after a freshly minted token is not an expiry -
        // it is a permission or configuration problem, and retrying it forever would hide that.
        if (!result.ok && result.httpStatus === 403 && !session.aborted) {
            if (await renewTarget(item, dotNetRef)) {
                return await attempt();
            }
        }

        return result;
    } catch (e) {
        if (session.aborted) {
            return failure(item.index, 0, 'Aborted', 'Upload cancelled.');
        }
        return failure(item.index, 0, 'Unexpected', describe(e));
    } finally {
        session.controllers.delete(item.index);
    }
}

async function putWholeBlob(phaseId, session, item, file, controller, dotNetRef) {
    const response = await sendWithRetry(
        () => fetch(item.target.sasUri, {
            method: 'PUT',
            headers: {
                'x-ms-blob-type': 'BlockBlob',
                'Content-Type': item.target.contentType
            },
            body: file,
            signal: controller.signal
        }),
        session);

    if (!response.ok) {
        return await httpFailure(item.index, response);
    }

    noteProgress(session, item.index, 100);
    return { index: item.index, ok: true, httpStatus: response.status, errorCode: null, errorMessage: null };
}

async function putBlocks(phaseId, session, item, file, controller, dotNetRef) {
    const total = Math.ceil(file.size / BLOCK_SIZE);
    const blockIds = [];
    let sent = 0;

    for (let i = 0; i < total; i++) {
        if (session.aborted) {
            return failure(item.index, 0, 'Aborted', 'Upload cancelled.');
        }

        const start = i * BLOCK_SIZE;
        const chunk = file.slice(start, Math.min(start + BLOCK_SIZE, file.size));

        // Zero-padded before encoding: Azure rejects a blob whose block IDs differ in decoded
        // length, and "10" is one byte longer than "9". Six digits covers a 256 GB blob.
        const blockId = btoa(String(i).padStart(6, '0'));
        blockIds.push(blockId);

        const url = `${item.target.sasUri}&comp=block&blockid=${encodeURIComponent(blockId)}`;
        const response = await sendWithRetry(
            () => fetch(url, { method: 'PUT', body: chunk, signal: controller.signal }),
            session);

        if (!response.ok) {
            return await httpFailure(item.index, response);
        }

        sent += chunk.size;
        noteProgress(session, item.index, Math.floor((sent / file.size) * 100));
    }

    const manifest =
        '<?xml version="1.0" encoding="utf-8"?><BlockList>'
        + blockIds.map(id => `<Latest>${id}</Latest>`).join('')
        + '</BlockList>';

    const commit = await sendWithRetry(
        () => fetch(`${item.target.sasUri}&comp=blocklist`, {
            method: 'PUT',
            headers: { 'Content-Type': item.target.contentType },
            body: manifest,
            signal: controller.signal
        }),
        session);

    if (!commit.ok) {
        return await httpFailure(item.index, commit);
    }

    noteProgress(session, item.index, 100);
    return { index: item.index, ok: true, httpStatus: commit.status, errorCode: null, errorMessage: null };
}

/**
 * Retries transient failures with backoff.
 *
 * A 403 is not retried against the same URL - it means the write token expired, and hammering an
 * expired token wastes the creator's time. It is handled one level up by asking .NET for a fresh
 * one; `send` takes the current URL so the caller can swap it.
 */
async function sendWithRetry(send, session) {
    let lastError = null;

    for (let attempt = 1; attempt <= MAX_ATTEMPTS_PER_BLOCK; attempt++) {
        if (session.aborted) {
            throw new DOMException('Aborted', 'AbortError');
        }

        try {
            const response = await send();

            if (response.ok || response.status === 403 || (response.status >= 400 && response.status < 500)) {
                return response;
            }

            lastError = new Error(`HTTP ${response.status}`);
            if (attempt === MAX_ATTEMPTS_PER_BLOCK) {
                return response;
            }
        } catch (e) {
            if (e && e.name === 'AbortError') {
                throw e;
            }
            lastError = e;
            if (attempt === MAX_ATTEMPTS_PER_BLOCK) {
                throw e;
            }
        }

        await delay(RETRY_BASE_DELAY_MS * Math.pow(2, attempt - 1));
    }

    throw lastError || new Error('Upload failed.');
}

/**
 * Azure returns its reason in an XML body and an x-ms-error-code header. Surfacing them is the whole
 * difference between "the upload failed" and knowing a SAS expired or CORS was never configured -
 * the latter being invisible in every server-side log we own.
 */
async function httpFailure(index, response) {
    let code = response.headers.get('x-ms-error-code') || `Http${response.status}`;
    let message = '';

    try {
        const body = await response.text();
        const match = /<Message>([\s\S]*?)<\/Message>/.exec(body);
        if (match) { message = match[1].split('\n')[0].trim(); }
    } catch { /* body already consumed or unreadable */ }

    return failure(index, response.status, code, message || `The upload was rejected (HTTP ${response.status}).`);
}

function failure(index, httpStatus, errorCode, errorMessage) {
    return { index, ok: false, httpStatus, errorCode, errorMessage };
}

function noteProgress(session, index, percent) {
    const existing = session.pending.get(index);
    const now = Date.now();

    // Coalesced per file, then flushed for all files together. A 57 MB file is 15 blocks, but a
    // 24-file batch at 4-wide would still be a steady drip of circuit round trips without this.
    if (existing && existing.percent === percent) {
        return;
    }
    if (existing && percent < 100 && now - existing.at < PROGRESS_INTERVAL_MS) {
        return;
    }

    session.pending.set(index, { percent, at: now });
}

function flushProgress(phaseId, session, dotNetRef) {
    if (session.pending.size === 0) {
        return;
    }

    const indexes = [];
    const percents = [];
    for (const [index, entry] of session.pending) {
        indexes.push(index);
        percents.push(entry.percent);
    }
    session.pending.clear();

    try { dotNetRef.invokeMethodAsync('ReportUploadProgress', phaseId, indexes, percents); }
    catch { /* circuit gone; progress is decoration */ }
}

function stopFlush(session) {
    if (session.flushTimer !== null) {
        clearInterval(session.flushTimer);
        session.flushTimer = null;
    }
}

function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function describe(e) {
    if (!e) { return 'Unknown error.'; }
    if (e.name === 'AbortError') { return 'Upload cancelled.'; }
    // A CORS preflight failure and an offline browser are indistinguishable to fetch - both surface
    // as a TypeError with no status - so the message has to name both possibilities.
    if (e instanceof TypeError) {
        return 'Could not reach storage. This is usually a network drop, or storage CORS not allowing this site.';
    }
    return e.message || String(e);
}
