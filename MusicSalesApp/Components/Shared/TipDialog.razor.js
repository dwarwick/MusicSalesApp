// Machine fingerprint generation for fraud detection
// Collects non-PII browser characteristics to create a unique-ish device identifier

async function hashString(str) {
    const encoder = new TextEncoder();
    const data = encoder.encode(str);
    const hashBuffer = await crypto.subtle.digest('SHA-256', data);
    const hashArray = Array.from(new Uint8Array(hashBuffer));
    return hashArray.map(b => b.toString(16).padStart(2, '0')).join('');
}

function getCanvasFingerprint() {
    try {
        const canvas = document.createElement('canvas');
        canvas.width = 200;
        canvas.height = 50;
        const ctx = canvas.getContext('2d');
        ctx.textBaseline = 'top';
        ctx.font = '14px Arial';
        ctx.fillStyle = '#f60';
        ctx.fillRect(125, 1, 62, 20);
        ctx.fillStyle = '#069';
        ctx.fillText('StreamTunes', 2, 15);
        ctx.fillStyle = 'rgba(102, 204, 0, 0.7)';
        ctx.fillText('StreamTunes', 4, 17);
        return canvas.toDataURL();
    } catch (e) {
        return '';
    }
}

export async function getMachineFingerprint() {
    try {
        const components = [];

        // User agent
        components.push(navigator.userAgent || '');

        // Screen properties
        components.push(`${screen.width}x${screen.height}`);
        components.push(`${screen.colorDepth}`);
        components.push(`${window.devicePixelRatio || 1}`);

        // Timezone
        components.push(Intl.DateTimeFormat().resolvedOptions().timeZone || '');
        components.push(`${new Date().getTimezoneOffset()}`);

        // Language
        components.push(navigator.language || '');
        components.push((navigator.languages || []).join(','));

        // Platform
        components.push(navigator.platform || '');

        // Hardware concurrency
        components.push(`${navigator.hardwareConcurrency || 0}`);

        // Canvas fingerprint
        components.push(getCanvasFingerprint());

        // WebGL renderer (GPU info)
        try {
            const gl = document.createElement('canvas').getContext('webgl');
            if (gl) {
                const debugInfo = gl.getExtension('WEBGL_debug_renderer_info');
                if (debugInfo) {
                    components.push(gl.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL) || '');
                }
            }
        } catch (e) {
            // WebGL not available
        }

        // Touch support
        components.push(`${navigator.maxTouchPoints || 0}`);

        const raw = components.join('|');
        return await hashString(raw);
    } catch (e) {
        console.warn('Failed to generate fingerprint:', e);
        return null;
    }
}
