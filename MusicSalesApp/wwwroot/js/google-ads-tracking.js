(function () {
    const namespaceName = "streamtunesGoogleAds";
    const storagePrefix = "streamtunes.googleAdsConversion.";
    const sentValue = "sent";

    window[namespaceName] = window[namespaceName] || {};

    window[namespaceName].trackConversion = function (sendTo, transactionId) {
        if (!sendTo || !transactionId || typeof window.gtag !== "function") {
            return;
        }

        const storageKey = storagePrefix + transactionId;

        try {
            if (window.localStorage && window.localStorage.getItem(storageKey) === sentValue) {
                return;
            }
        } catch {
            // Browser storage may be blocked; conversion tracking can still proceed.
        }

        window.gtag("event", "conversion", {
            send_to: sendTo,
            transaction_id: transactionId
        });

        try {
            if (window.localStorage) {
                window.localStorage.setItem(storageKey, sentValue);
            }
        } catch {
            // Best-effort duplicate protection only.
        }
    };

    window[namespaceName].trackFunnelEvent = function (eventName, parameters) {
        if (!eventName) {
            return;
        }

        window.dataLayer = window.dataLayer || [];
        const eventPayload = Object.assign({ event: eventName }, parameters || {});
        window.dataLayer.push(eventPayload);
    };

    document.addEventListener("click", function (event) {
        const analyticsTarget = event.target && event.target.closest("[data-analytics-event]");
        if (!analyticsTarget) {
            return;
        }

        window[namespaceName].trackFunnelEvent(analyticsTarget.getAttribute("data-analytics-event"), {
            analytics_category: analyticsTarget.getAttribute("data-analytics-category") || "funnel",
            analytics_label: analyticsTarget.getAttribute("data-analytics-label") || analyticsTarget.textContent.trim()
        });
    });

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("[data-analytics-page-event]").forEach(function (analyticsTarget) {
            window[namespaceName].trackFunnelEvent(analyticsTarget.getAttribute("data-analytics-page-event"), {
                analytics_category: analyticsTarget.getAttribute("data-analytics-category") || "funnel",
                analytics_label: analyticsTarget.getAttribute("data-analytics-label") || document.title
            });
        });
    });
})();
