// Image Crop Helper Module
// Provides image dimension checking and square crop functionality

/**
 * Check image dimensions by loading the image from a URL.
 * Does not use crossOrigin so the image loads regardless of CORS.
 * @param {string} imageUrl - URL of the image to check
 * @returns {Promise<{width: number, height: number}>} Image dimensions
 */
export function checkImageDimensions(imageUrl) {
    return new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => {
            resolve({ width: img.naturalWidth, height: img.naturalHeight });
        };
        img.onerror = () => {
            reject("Failed to load image");
        };
        img.src = imageUrl;
    });
}

// Internal state for the crop tool
let cropState = null;

/**
 * Initialize the crop tool with an image.
 * Uses a same-origin proxy URL so the canvas is not tainted by CORS.
 * @param {string} canvasId - ID of the canvas element
 * @param {string} imageUrl - Same-origin URL of the image to crop
 * @param {DotNetObjectReference} dotNetRef - Reference to call back into C#
 */
export function initCropTool(canvasId, imageUrl, dotNetRef) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const ctx = canvas.getContext('2d');
    const img = new Image();

    img.onload = () => {
        // Calculate initial scale to fit the image in the canvas with the crop area visible
        const canvasSize = canvas.width; // Canvas is square
        const minDim = Math.min(img.naturalWidth, img.naturalHeight);
        const initialScale = canvasSize / minDim;

        cropState = {
            canvas,
            ctx,
            img,
            dotNetRef,
            scale: initialScale,
            minScale: initialScale * 0.5,
            maxScale: initialScale * 3,
            offsetX: (canvasSize - img.naturalWidth * initialScale) / 2,
            offsetY: (canvasSize - img.naturalHeight * initialScale) / 2,
            isDragging: false,
            lastX: 0,
            lastY: 0,
            canvasSize
        };

        drawCropPreview();
        setupEventListeners();
    };

    img.onerror = () => {
        console.error('Failed to load image for cropping');
    };

    img.src = imageUrl;
}

function drawCropPreview() {
    if (!cropState) return;

    const { canvas, ctx, img, scale, offsetX, offsetY, canvasSize } = cropState;

    // Clear canvas
    ctx.clearRect(0, 0, canvasSize, canvasSize);

    // Draw the image with current scale and offset
    ctx.drawImage(img, offsetX, offsetY, img.naturalWidth * scale, img.naturalHeight * scale);

    // Draw semi-transparent overlay outside the crop area (the entire canvas is the crop area)
    // The crop area is the full canvas, so we just draw a border
    ctx.strokeStyle = '#00ff00';
    ctx.lineWidth = 2;
    ctx.strokeRect(1, 1, canvasSize - 2, canvasSize - 2);
}

function setupEventListeners() {
    if (!cropState) return;

    const { canvas } = cropState;

    // Mouse events
    canvas.addEventListener('mousedown', onMouseDown);
    canvas.addEventListener('mousemove', onMouseMove);
    canvas.addEventListener('mouseup', onMouseUp);
    canvas.addEventListener('mouseleave', onMouseUp);

    // Touch events
    canvas.addEventListener('touchstart', onTouchStart, { passive: false });
    canvas.addEventListener('touchmove', onTouchMove, { passive: false });
    canvas.addEventListener('touchend', onTouchEnd);
}

function onMouseDown(e) {
    if (!cropState) return;
    cropState.isDragging = true;
    cropState.lastX = e.clientX;
    cropState.lastY = e.clientY;
    cropState.canvas.style.cursor = 'grabbing';
}

function onMouseMove(e) {
    if (!cropState || !cropState.isDragging) return;
    const dx = e.clientX - cropState.lastX;
    const dy = e.clientY - cropState.lastY;
    cropState.offsetX += dx;
    cropState.offsetY += dy;
    cropState.lastX = e.clientX;
    cropState.lastY = e.clientY;
    drawCropPreview();
}

function onMouseUp() {
    if (!cropState) return;
    cropState.isDragging = false;
    cropState.canvas.style.cursor = 'grab';
}

function onTouchStart(e) {
    if (!cropState || e.touches.length !== 1) return;
    e.preventDefault();
    cropState.isDragging = true;
    cropState.lastX = e.touches[0].clientX;
    cropState.lastY = e.touches[0].clientY;
}

function onTouchMove(e) {
    if (!cropState || !cropState.isDragging || e.touches.length !== 1) return;
    e.preventDefault();
    const dx = e.touches[0].clientX - cropState.lastX;
    const dy = e.touches[0].clientY - cropState.lastY;
    cropState.offsetX += dx;
    cropState.offsetY += dy;
    cropState.lastX = e.touches[0].clientX;
    cropState.lastY = e.touches[0].clientY;
    drawCropPreview();
}

function onTouchEnd() {
    if (!cropState) return;
    cropState.isDragging = false;
}

/**
 * Set the zoom level for the crop tool
 * @param {number} zoomValue - Zoom value (0-100)
 */
export function setZoom(zoomValue) {
    if (!cropState) return;

    const { canvasSize, img } = cropState;
    const minDim = Math.min(img.naturalWidth, img.naturalHeight);
    const baseScale = canvasSize / minDim;

    // Map 0-100 to 0.5x-3x of baseScale
    const normalizedZoom = zoomValue / 100;
    const newScale = baseScale * (0.5 + normalizedZoom * 2.5);

    // Adjust offsets to zoom centered
    const centerX = canvasSize / 2;
    const centerY = canvasSize / 2;

    // Calculate the image point at the center of the canvas
    const imgCenterX = (centerX - cropState.offsetX) / cropState.scale;
    const imgCenterY = (centerY - cropState.offsetY) / cropState.scale;

    // Apply new scale and recalculate offset to keep the center point
    cropState.scale = newScale;
    cropState.offsetX = centerX - imgCenterX * newScale;
    cropState.offsetY = centerY - imgCenterY * newScale;

    drawCropPreview();
}

/**
 * Get the cropped image as a Blob, upload it directly to the server,
 * and return a small success indicator (avoids SignalR message-size limit).
 * @param {string} uploadUrl - Server URL to POST the cropped image to
 * @returns {Promise<boolean>} true if upload succeeded
 */
export function getCroppedImageAndUpload(uploadUrl) {
    return new Promise((resolve) => {
        if (!cropState) { resolve(false); return; }

        const { img, scale, offsetX, offsetY, canvasSize } = cropState;

        // Output at 800x800 regardless of display canvas size
        const outputSize = 800;
        const tempCanvas = document.createElement('canvas');
        tempCanvas.width = outputSize;
        tempCanvas.height = outputSize;
        const tempCtx = tempCanvas.getContext('2d');

        const scaleFactor = outputSize / canvasSize;

        tempCtx.drawImage(
            img,
            offsetX * scaleFactor,
            offsetY * scaleFactor,
            img.naturalWidth * scale * scaleFactor,
            img.naturalHeight * scale * scaleFactor
        );

        tempCanvas.toBlob(async (blob) => {
            if (!blob) { resolve(false); return; }
            try {
                const resp = await fetch(uploadUrl, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/octet-stream' },
                    body: blob
                });
                resolve(resp.ok);
            } catch (e) {
                console.error('Crop upload failed', e);
                resolve(false);
            }
        }, 'image/png');
    });
}

/**
 * Clean up the crop tool and remove event listeners
 */
export function disposeCropTool() {
    if (!cropState) return;

    const { canvas } = cropState;
    canvas.removeEventListener('mousedown', onMouseDown);
    canvas.removeEventListener('mousemove', onMouseMove);
    canvas.removeEventListener('mouseup', onMouseUp);
    canvas.removeEventListener('mouseleave', onMouseUp);
    canvas.removeEventListener('touchstart', onTouchStart);
    canvas.removeEventListener('touchmove', onTouchMove);
    canvas.removeEventListener('touchend', onTouchEnd);

    cropState = null;
}
