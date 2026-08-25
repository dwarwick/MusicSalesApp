window.dashboardHelper = {
    getUserTimeZone: function () {
        return Intl.DateTimeFormat().resolvedOptions().timeZone;
    },

    // Scrolls to a section AFTER the page has rendered it. The browser resolves a URL
    // fragment when the document loads, and pages that fetch their content first are still
    // a spinner at that moment - the target does not exist yet, so nothing happens and the
    // reader is left at the top wondering what the link did.
    scrollToSection: function (id) {
        if (!id) {
            return false;
        }

        var target = document.getElementById(id);
        if (!target) {
            return false;
        }

        target.scrollIntoView({ behavior: "smooth", block: "start" });
        return true;
    },

    // A no-op the server calls purely to find out whether this browser is still attached to the
    // circuit. It exists because NavigationManager.NavigateTo cannot be wrapped in a try/catch on
    // the server side - a navigation whose interop is never answered surfaces a minute later as an
    // unhandled circuit exception - so an automatic navigation asks this first and declines if
    // nobody answers. Deliberately does nothing and touches nothing.
    ping: function () {
        return true;
    },

    formatAdminMessageDate: function (utcIso) {
        if (!utcIso) {
            return '';
        }

        return new Date(utcIso).toLocaleDateString(undefined, {
            year: 'numeric',
            month: '2-digit',
            day: '2-digit'
        });
    }
};

window.getMaintenanceLocalTime = function (startUtcIso, endUtcIso) {
    var options = { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', hour12: true };
    var startLocal = '';
    var endLocal = '';
    var tzAbbr = '';

    if (startUtcIso) {
        var startDate = new Date(startUtcIso);
        startLocal = startDate.toLocaleString(undefined, options);
    }
    if (endUtcIso) {
        var endDate = new Date(endUtcIso);
        endLocal = endDate.toLocaleString(undefined, options);
    }

    // Get time zone abbreviation
    try {
        var formatter = new Intl.DateTimeFormat(undefined, { timeZoneName: 'short' });
        var parts = formatter.formatToParts(new Date());
        var tzPart = parts.find(function (p) { return p.type === 'timeZoneName'; });
        tzAbbr = tzPart ? tzPart.value : '';
    } catch (e) {
        tzAbbr = '';
    }

    return { startLocal: startLocal, endLocal: endLocal, timeZoneAbbreviation: tzAbbr };
};

window.checkMaintenanceAcknowledged = function (key) {
    try {
        return localStorage.getItem(key) === 'true';
    } catch (e) {
        return false;
    }
};

window.acknowledgeMaintenanceNotice = function (key) {
    try {
        localStorage.setItem(key, 'true');
    } catch (e) {
        // localStorage may be unavailable in private browsing
    }
};
