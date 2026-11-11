const previewTextChunkSize = 16 * 1024;
let lastPreviewText = "";

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

    scrollToHighlighted: () => {
        const row = document.querySelector(".file-table tr.row-highlighted");
        if (row) {
            row.scrollIntoView({ block: "center" });
        }
    },

    // Текстовый предпросмотр: браузер забирает тело напрямую через fetch,
    // байты не проходят через Blazor circuit. Передача текста в .NET
    // выполняется чанками: результат целиком (до 512 КиБ) превышает
    // потолок сообщения SignalR и обрывает circuit.
    fetchPreviewText: async (url) => {
        const response = await fetch(url, { credentials: "same-origin" });
        if (!response.ok) {
            lastPreviewText = "";
            return { ok: false, length: 0, chunkCount: 0, truncated: false };
        }

        lastPreviewText = await response.text();
        return {
            ok: true,
            length: lastPreviewText.length,
            chunkCount: Math.ceil(lastPreviewText.length / previewTextChunkSize),
            truncated: response.headers.get("X-Preview-Truncated") === "true"
        };
    },

    getPreviewTextChunk: (index) => {
        const start = index * previewTextChunkSize;
        return lastPreviewText.slice(start, start + previewTextChunkSize);
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

// Theme preference is a browser setting (localStorage), not part of
// config.yaml. The script is loaded synchronously from <head>, so applying
// the theme here happens before the body is painted and avoids a flash of
// the wrong theme.
(() => {
    const storageKey = "filebrowser.theme";
    const preferences = ["system", "light", "dark"];

    const readPreference = () => {
        try {
            const stored = window.localStorage.getItem(storageKey);
            return preferences.includes(stored) ? stored : "system";
        } catch {
            return "system";
        }
    };

    const systemTheme = () =>
        window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches
            ? "dark"
            : "light";

    const resolveTheme = (preference) =>
        preference === "system" ? systemTheme() : preference;

    const applyTheme = (preference) => {
        document.documentElement.dataset.bsTheme = resolveTheme(preference);
    };

    const writePreference = (preference) => {
        try {
            window.localStorage.setItem(storageKey, preference);
        } catch {
            // Private mode or blocked storage: keep the theme for this page only.
        }
    };

    const syncThemeControls = () => {
        const controls = document.querySelectorAll('input[name="theme-preference"]');
        if (controls.length === 0) {
            return;
        }

        const preference = readPreference();
        controls.forEach((control) => {
            control.checked = control.value === preference;
        });
    };

    // Enhanced navigation to an interactive page replaces the prerendered
    // DOM when the Blazor circuit starts, resetting plain HTML controls.
    // Re-sync whenever the DOM changes and theme controls are present.
    // Setting the checked property does not mutate attributes, so this
    // observer never loops on its own writes.
    let syncControlsScheduled = false;
    const scheduleSyncThemeControls = () => {
        if (syncControlsScheduled) {
            return;
        }

        syncControlsScheduled = true;
        window.requestAnimationFrame(() => {
            syncControlsScheduled = false;
            syncThemeControls();
        });
    };

    new MutationObserver(scheduleSyncThemeControls).observe(document.documentElement, {
        childList: true,
        subtree: true,
    });

    // The settings close button always returns to the home page instead of
    // walking the browser history.
    const goBack = () => {
        window.location.assign("/");
    };

    applyTheme(readPreference());

    document.addEventListener("change", (event) => {
        const target = event.target;
        if (target instanceof HTMLInputElement
            && target.name === "theme-preference"
            && preferences.includes(target.value)) {
            writePreference(target.value);
            applyTheme(target.value);
        }
    });

    document.addEventListener("click", (event) => {
        const backButton = event.target instanceof Element
            ? event.target.closest("[data-nav-back]")
            : null;
        if (!backButton) {
            return;
        }

        event.preventDefault();
        goBack();
    });

    if (window.matchMedia) {
        const media = window.matchMedia("(prefers-color-scheme: dark)");
        const onSystemThemeChange = () => {
            if (readPreference() === "system") {
                applyTheme("system");
            }
        };

        if (media.addEventListener) {
            media.addEventListener("change", onSystemThemeChange);
        } else if (media.addListener) {
            media.addListener(onSystemThemeChange);
        }
    }

    window.addEventListener("storage", (event) => {
        if (event.key === storageKey) {
            applyTheme(readPreference());
            syncThemeControls();
        }
    });

    const refreshThemeUi = () => {
        applyTheme(readPreference());
        syncThemeControls();
    };

    // Blazor enhanced navigation replaces the page content without a full
    // reload and also restores <html> attributes from the server response,
    // dropping data-bs-theme. Re-apply the theme after every enhanced load.
    const handleEnhancedLoad = () => {
        refreshThemeUi();
        // Guard against Blazor patching <html> after the event is dispatched.
        window.requestAnimationFrame(refreshThemeUi);
    };

    document.addEventListener("enhancedload", handleEnhancedLoad);

    const onDocumentReady = () => {
        refreshThemeUi();
        if (window.Blazor && window.Blazor.addEventListener) {
            window.Blazor.addEventListener("enhancedload", handleEnhancedLoad);
        }
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", onDocumentReady);
    } else {
        onDocumentReady();
    }

    window.fileBrowser.theme = {
        getPreference: readPreference,
        getResolved: () => resolveTheme(readPreference()),
        setPreference: (preference) => {
            if (!preferences.includes(preference)) {
                return;
            }

            writePreference(preference);
            applyTheme(preference);
            syncThemeControls();
        }
    };
})();
