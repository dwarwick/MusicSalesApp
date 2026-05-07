window.dashboardHelper = {
    getUserTimeZone: function () {
        return Intl.DateTimeFormat().resolvedOptions().timeZone;
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
