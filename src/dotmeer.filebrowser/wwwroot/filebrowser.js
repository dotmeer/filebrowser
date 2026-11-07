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
    },

    // Draggable splitter between layout panels. The adjacent panel is resized
    // in the browser only; .NET is not involved per mouse move.
    initSplitter: (element) => {
        if (element.fileBrowserSplitterHandler) {
            return;
        }

        const handler = (event) => {
            event.preventDefault();

            const target = element.dataset.target === "prev"
                ? element.previousElementSibling
                : element.nextElementSibling;
            if (!target) {
                return;
            }

            const startX = event.clientX;
            const startWidth = target.getBoundingClientRect().width;
            element.classList.add("splitter-dragging");

            const onMove = (moveEvent) => {
                const delta = element.dataset.target === "prev"
                    ? moveEvent.clientX - startX
                    : startX - moveEvent.clientX;
                const minWidth = parseInt(getComputedStyle(target).minWidth, 10) || 100;
                const width = Math.max(minWidth, startWidth + delta);
                target.style.flexBasis = width + "px";
                target.style.width = width + "px";
            };

            const onUp = () => {
                element.classList.remove("splitter-dragging");
                document.removeEventListener("mousemove", onMove);
                document.removeEventListener("mouseup", onUp);
            };

            document.addEventListener("mousemove", onMove);
            document.addEventListener("mouseup", onUp);
        };

        element.addEventListener("mousedown", handler);
        element.fileBrowserSplitterHandler = handler;
    },

    disposeSplitter: (element) => {
        if (!element || !element.fileBrowserSplitterHandler) {
            return;
        }

        element.removeEventListener("mousedown", element.fileBrowserSplitterHandler);
        element.fileBrowserSplitterHandler = null;
    }
};
