const androidPlatform = 'Android';
const iosPlatform = 'iOS';
const fallbackModeHide = 'Hide';
const fallbackModeShowPromotionalBanner = 'ShowPromotionalBanner';

export async function evaluateMobileAppInstallBanner(options) {
    const normalizedOptions = normalizeOptions(options);

    if (isDismissed(normalizedOptions)) {
        return hiddenResult();
    }

    if (isAndroid()) {
        return await evaluateAndroidAsync(normalizedOptions);
    }

    if (isIos()) {
        return evaluateIos(normalizedOptions);
    }

    return hiddenResult();
}

export function dismissMobileAppInstallBanner(storageKey, storageValue) {
    if (!storageKey) {
        return;
    }

    localStorage.setItem(storageKey, storageValue || 'true');
}

async function evaluateAndroidAsync(options) {
    if (!options.googlePlayUrl) {
        return hiddenResult();
    }

    if (typeof navigator.getInstalledRelatedApps === 'function') {
        try {
            const relatedApps = await navigator.getInstalledRelatedApps();
            const hasStreamTunesApp = relatedApps.some(app =>
                app.platform === 'play' && app.id === options.androidPackageName);

            return hasStreamTunesApp
                ? hiddenResult()
                : visibleResult(androidPlatform, false);
        } catch {
            return evaluateAndroidFallback(options);
        }
    }

    return evaluateAndroidFallback(options);
}

function evaluateAndroidFallback(options) {
    return options.androidFallbackMode === fallbackModeShowPromotionalBanner
        ? visibleResult(androidPlatform, true)
        : hiddenResult();
}

function evaluateIos(options) {
    return options.showIosFallbackBanner && options.appleAppStoreUrl
        ? visibleResult(iosPlatform, true)
        : hiddenResult();
}

function isDismissed(options) {
    return !!options.dismissStorageKey
        && localStorage.getItem(options.dismissStorageKey) === options.dismissStorageValue;
}

function isAndroid() {
    return /android/i.test(navigator.userAgent || '');
}

function isIos() {
    const userAgent = navigator.userAgent || '';
    const platform = navigator.platform || '';
    return /iPad|iPhone|iPod/.test(userAgent)
        || (platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function normalizeOptions(options) {
    return {
        androidPackageName: readOption(options, 'androidPackageName', 'AndroidPackageName'),
        googlePlayUrl: readOption(options, 'googlePlayUrl', 'GooglePlayUrl'),
        appleAppStoreUrl: readOption(options, 'appleAppStoreUrl', 'AppleAppStoreUrl'),
        androidFallbackMode: readOption(options, 'androidFallbackMode', 'AndroidFallbackMode') || fallbackModeHide,
        showIosFallbackBanner: !!readOption(options, 'showIosFallbackBanner', 'ShowIosFallbackBanner'),
        dismissStorageKey: readOption(options, 'dismissStorageKey', 'DismissStorageKey'),
        dismissStorageValue: readOption(options, 'dismissStorageValue', 'DismissStorageValue') || 'true'
    };
}

function readOption(options, camelName, pascalName) {
    if (!options) {
        return undefined;
    }

    return options[camelName] ?? options[pascalName];
}

function hiddenResult() {
    return {
        showBanner: false,
        platform: '',
        isPromotional: false
    };
}

function visibleResult(platform, isPromotional) {
    return {
        showBanner: true,
        platform,
        isPromotional
    };
}