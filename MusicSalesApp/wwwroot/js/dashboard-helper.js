window.dashboardHelper = {
    getUserTimeZone: function () {
        return Intl.DateTimeFormat().resolvedOptions().timeZone;
    }
};
