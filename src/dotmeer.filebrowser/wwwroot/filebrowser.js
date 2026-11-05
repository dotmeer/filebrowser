window.fileBrowser = {
    formatLocalTimes: () => {
        document.querySelectorAll("time.local-time:not([data-formatted])").forEach((element) => {
            const date = new Date(element.dateTime);
            if (!Number.isNaN(date.getTime())) {
                element.textContent = date.toLocaleString();
                element.dataset.formatted = "true";
            }
        });
    },

    registerReconnectRefresh: (dotNetReference) => {
        const reconnectModal = document.getElementById("components-reconnect-modal");
        if (!reconnectModal) {
            return;
        }

        if (reconnectModal.fileBrowserReconnectHandler) {
            reconnectModal.removeEventListener(
                "components-reconnect-state-changed",
                reconnectModal.fileBrowserReconnectHandler);
        }

        const handler = (event) => {
            if (event.detail && event.detail.state === "hide") {
                dotNetReference.invokeMethodAsync("RefreshAfterReconnect").catch(() => {});
            }
        };

        reconnectModal.addEventListener("components-reconnect-state-changed", handler);
        reconnectModal.fileBrowserReconnectHandler = handler;
    },

    unregisterReconnectRefresh: () => {
        const reconnectModal = document.getElementById("components-reconnect-modal");
        if (!reconnectModal || !reconnectModal.fileBrowserReconnectHandler) {
            return;
        }

        reconnectModal.removeEventListener(
            "components-reconnect-state-changed",
            reconnectModal.fileBrowserReconnectHandler);
        reconnectModal.fileBrowserReconnectHandler = null;
    }
};
