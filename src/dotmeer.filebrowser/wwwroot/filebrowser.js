window.fileBrowser = {
    formatLocalTimes: () => {
        document.querySelectorAll("time.local-time:not([data-formatted])").forEach((element) => {
            const date = new Date(element.dateTime);
            if (!Number.isNaN(date.getTime())) {
                element.textContent = date.toLocaleString();
                element.dataset.formatted = "true";
            }
        });
    }
};
