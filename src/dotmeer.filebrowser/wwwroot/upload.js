// Browser-side uploader (SPEC §11). File bytes never pass through the
// Blazor circuit: this script sends them directly with XMLHttpRequest,
// which is the only API that reports upload progress. Blazor receives
// only a tiny "uploads changed" event so the listing can be refreshed.
(() => {
    const maxConcurrent = 2;
    window.fileBrowser = window.fileBrowser || {};

    const state = {
        context: { root: "", path: "", enabled: false },
        maxFileBytes: 0,
        strings: {},
        dotNetRef: null,
        queue: [],
        active: 0,
        seq: 0,
        items: new Map(),
        panel: null,
        list: null,
        refreshTimer: null,
        attached: false,
        handlers: null,
        disposed: false,
        generation: 0
    };

    const text = (key, fallback) => state.strings[key] || fallback;

    function ensurePanel() {
        if (state.disposed) {
            return;
        }

        if (state.panel) {
            return;
        }

        const panel = document.createElement("aside");
        panel.className = "upload-panel";
        panel.setAttribute("role", "status");
        panel.setAttribute("aria-live", "polite");
        panel.hidden = true;

        const header = document.createElement("header");
        header.className = "upload-panel-header";
        const title = document.createElement("h2");
        title.textContent = text("panelTitle", "Uploads");
        const close = document.createElement("button");
        close.type = "button";
        close.className = "upload-panel-close";
        close.title = text("hide", "Hide");
        close.setAttribute("aria-label", text("hide", "Hide"));
        close.textContent = "✕";
        close.addEventListener("click", clearFinished);
        header.append(title, close);

        const list = document.createElement("div");
        list.className = "upload-list";

        panel.append(header, list);
        document.body.append(panel);
        state.panel = panel;
        state.list = list;
    }

    function onChange(event) {
        const target = event.target;
        if (!(target instanceof HTMLInputElement) || target.id !== "upload-input") {
            return;
        }

        enqueue(target.files);
        target.value = "";
    }

    function dropZoneFor(target) {
        return target instanceof Element ? target.closest("[data-upload-dropzone]") : null;
    }

    function onDragOver(event) {
        if (!state.context.enabled) {
            return;
        }

        const zone = dropZoneFor(event.target);
        if (!zone) {
            return;
        }

        event.preventDefault();
        if (event.dataTransfer) {
            event.dataTransfer.dropEffect = "copy";
        }
        zone.classList.add("upload-drop-active");
    }

    function onDragLeave(event) {
        const zone = dropZoneFor(event.target);
        if (zone && !zone.contains(event.relatedTarget)) {
            zone.classList.remove("upload-drop-active");
        }
    }

    function onDrop(event) {
        if (!state.context.enabled) {
            return;
        }

        const zone = dropZoneFor(event.target);
        if (!zone) {
            return;
        }

        event.preventDefault();
        zone.classList.remove("upload-drop-active");
        if (event.dataTransfer) {
            enqueue(event.dataTransfer.files);
        }
    }

    function onTriggerClick(event) {
        if (state.disposed) {
            return;
        }

        const trigger = event.target instanceof Element
            ? event.target.closest("[data-upload-trigger]")
            : null;
        if (!trigger) {
            return;
        }

        const input = document.getElementById("upload-input");
        if (input instanceof HTMLInputElement && !input.disabled) {
            // Same user gesture as the click: the native file picker opens
            // without a server round-trip through the Blazor circuit.
            input.click();
        }
    }

    function attach() {
        if (state.attached) {
            return;
        }

        state.handlers = {
            dragover: onDragOver,
            dragleave: onDragLeave,
            drop: onDrop,
            change: onChange,
            click: onTriggerClick
        };
        document.addEventListener("dragover", state.handlers.dragover);
        document.addEventListener("dragleave", state.handlers.dragleave);
        document.addEventListener("drop", state.handlers.drop);
        document.addEventListener("change", state.handlers.change);
        document.addEventListener("click", state.handlers.click);
        state.attached = true;
    }

    function detach() {
        if (!state.attached || !state.handlers) {
            return;
        }

        document.removeEventListener("dragover", state.handlers.dragover);
        document.removeEventListener("dragleave", state.handlers.dragleave);
        document.removeEventListener("drop", state.handlers.drop);
        document.removeEventListener("change", state.handlers.change);
        document.removeEventListener("click", state.handlers.click);
        state.attached = false;
        state.handlers = null;
    }

    function enqueue(files) {
        if (state.disposed || !state.context.enabled || !files || files.length === 0) {
            return;
        }

        ensurePanel();
        let added = false;
        for (const file of Array.from(files)) {
            if (state.maxFileBytes > 0 && file.size > state.maxFileBytes) {
                const rejected = {
                    id: ++state.seq,
                    file,
                    status: "failed",
                    progress: 0,
                    error: text("tooLarge", "File is too large"),
                    settled: true
                };
                state.items.set(rejected.id, rejected);
                renderItem(rejected);
                scheduleNotify();
                added = true;
                continue;
            }

            const item = {
                id: ++state.seq,
                file,
                status: "waiting",
                progress: 0,
                error: null,
                xhr: null,
                settled: false,
                cancelRequested: false
            };
            state.items.set(item.id, item);
            state.queue.push(item);
            renderItem(item);
            added = true;
        }

        if (added) {
            state.panel.hidden = false;
            pump();
        }
    }

    function pump() {
        while (state.active < maxConcurrent && state.queue.length > 0) {
            const item = state.queue.shift();
            if (item.status !== "waiting") {
                continue;
            }

            void start(item);
        }
    }

    async function start(item) {
        if (state.disposed) {
            return;
        }

        const generation = state.generation;
        if (item.cancelRequested) {
            settleWaiting(item, "cancelled", null);
            return;
        }

        state.active += 1;
        try {
            item.status = "checking";
            renderItem(item);

            const checkUrl = buildUrl("/api/fs/upload/check", {
                root: state.context.root,
                path: state.context.path,
                name: item.file.name,
                size: String(item.file.size)
            });

            let response;
            try {
                response = await fetch(checkUrl, { credentials: "same-origin" });
            } catch {
                settle(item, "failed", text("networkError", "The connection was lost."));
                return;
            }

            if (item.cancelRequested) {
                settle(item, "cancelled", null);
                return;
            }

            if (generation !== state.generation) {
                return;
            }

            if (!response.ok) {
                settle(item, "failed", await readProblem(response));
                return;
            }

            const token = readToken();
            if (!token) {
                settle(item, "failed", text("genericError", "The file could not be uploaded."));
                return;
            }

            item.status = "uploading";
            renderItem(item);

            const xhr = new XMLHttpRequest();
            item.xhr = xhr;
            xhr.open("POST", buildUrl("/api/fs/upload", {
                root: state.context.root,
                path: state.context.path,
                name: item.file.name
            }));
            xhr.setRequestHeader("X-CSRF-TOKEN", token);
            xhr.setRequestHeader("Content-Type", "application/octet-stream");
            xhr.upload.addEventListener("progress", (event) => {
                if (event.lengthComputable && event.total > 0) {
                    item.progress = Math.min(1, event.loaded / event.total);
                    renderItem(item);
                }
            });
            xhr.addEventListener("load", () => {
                if (xhr.status === 201) {
                    item.progress = 1;
                    settle(item, "completed", null);
                    return;
                }

                const problem = parseProblem(xhr.responseText);
                if (xhr.status === 409 && problem && problem.code === "upload_cancelled") {
                    settle(item, "cancelled", null);
                    return;
                }

                settle(item, "failed", problemText(problem, xhr.status));
            });
            xhr.addEventListener("error", () => {
                settle(item, "failed", text("networkError", "The connection was lost."));
            });
            xhr.addEventListener("abort", () => {
                settle(item, "cancelled", null);
            });
            xhr.send(item.file);
        } catch {
            settle(item, "failed", text("genericError", "The file could not be uploaded."));
        }
    }

    function settle(item, status, error) {
        if (item.settled) {
            return;
        }

        item.settled = true;
        item.status = status;
        item.error = error;
        item.xhr = null;
        state.active = Math.max(0, state.active - 1);
        renderItem(item);
        scheduleNotify();
        pump();
    }

    function settleWaiting(item, status, error) {
        item.settled = true;
        item.status = status;
        item.error = error;
        renderItem(item);
        scheduleNotify();
    }

    function cancel(item) {
        if (item.settled) {
            return;
        }

        item.cancelRequested = true;
        if (item.xhr) {
            item.xhr.abort();
            return;
        }

        if (item.status === "waiting") {
            const index = state.queue.indexOf(item);
            if (index >= 0) {
                state.queue.splice(index, 1);
            }
            settleWaiting(item, "cancelled", null);
        }
    }

    function renderItem(item) {
        if (state.disposed) {
            return;
        }

        ensurePanel();
        let row = state.list.querySelector(`[data-upload-id="${item.id}"]`);
        if (!row) {
            row = document.createElement("div");
            row.className = "upload-item";
            row.dataset.uploadId = String(item.id);

            const header = document.createElement("div");
            header.className = "upload-item-header";
            const name = document.createElement("span");
            name.className = "upload-item-name";
            const cancelButton = document.createElement("button");
            cancelButton.type = "button";
            cancelButton.className = "upload-item-cancel";
            cancelButton.textContent = "✕";
            header.append(name, cancelButton);

            const status = document.createElement("div");
            status.className = "upload-item-status";

            const progress = document.createElement("div");
            progress.className = "upload-progress";
            const bar = document.createElement("div");
            bar.className = "upload-progress-bar";
            progress.append(bar);

            row.append(header, status, progress);
            state.list.append(row);
            row.__cancelButton = cancelButton;
            row.__name = name;
            row.__status = status;
            row.__progress = progress;
            row.__bar = bar;
        }

        row.className = `upload-item upload-item-${item.status}`;
        row.__name.textContent = item.file.name;
        row.__name.title = item.file.name;

        const percent = Math.round((item.progress || 0) * 100);
        row.__bar.style.width = item.status === "completed" ? "100%" : `${percent}%`;
        row.__progress.hidden = item.status === "waiting" || item.status === "checking";

        let statusText;
        switch (item.status) {
            case "waiting":
                statusText = text("waiting", "Waiting to upload…");
                break;
            case "checking":
                statusText = text("waiting", "Waiting to upload…");
                break;
            case "uploading":
                statusText = `${text("uploading", "Uploading…")} ${percent}%`;
                break;
            case "completed":
                statusText = text("uploaded", "Uploaded");
                break;
            case "cancelled":
                statusText = text("cancelled", "Upload cancelled.");
                break;
            default:
                statusText = `${text("failed", "Upload failed")}: ${item.error || ""}`;
                break;
        }
        row.__status.textContent = statusText;

        const cancellable = item.status === "waiting" || item.status === "checking" || item.status === "uploading";
        row.__cancelButton.hidden = !cancellable;
        row.__cancelButton.title = text("cancel", "Cancel");
        row.__cancelButton.setAttribute("aria-label", text("cancel", "Cancel"));
        row.__cancelButton.onclick = () => cancel(item);
    }

    function clearFinished() {
        if (!state.list) {
            return;
        }

        for (const [id, item] of state.items) {
            if (item.settled) {
                state.items.delete(id);
                const row = state.list.querySelector(`[data-upload-id="${id}"]`);
                row?.remove();
            }
        }

        if (state.items.size === 0 && state.panel) {
            state.panel.hidden = true;
        }
    }

    function scheduleNotify() {
        if (state.disposed) {
            return;
        }

        if (state.refreshTimer) {
            clearTimeout(state.refreshTimer);
        }

        state.refreshTimer = setTimeout(() => {
            state.refreshTimer = null;
            state.dotNetRef?.invokeMethodAsync("OnUploadsChanged").catch(() => {});
        }, 350);
    }

    function buildUrl(path, parameters) {
        const url = new URL(path, window.location.origin);
        for (const [key, value] of Object.entries(parameters)) {
            url.searchParams.set(key, value);
        }
        return url.toString();
    }

    function readToken() {
        const input = document.querySelector(
            'form#antiforgery-token input[name="__RequestVerificationToken"]');
        return input && input.value ? input.value : "";
    }

    async function readProblem(response) {
        try {
            const problem = await response.json();
            return problem.detail || problem.title || String(response.status);
        } catch {
            return String(response.status);
        }
    }

    function parseProblem(responseText) {
        try {
            return JSON.parse(responseText);
        } catch {
            return null;
        }
    }

    function problemText(problem, status) {
        return problem?.detail || problem?.title || String(status);
    }

    window.fileBrowser.upload = {
        configure: (maxFileBytes, strings, dotNetRef) => {
            state.disposed = false;
            state.maxFileBytes = typeof maxFileBytes === "number" ? maxFileBytes : 0;
            state.strings = strings || {};
            state.dotNetRef = dotNetRef || null;
            attach();
        },

        setContext: (root, path, enabled) => {
            state.context = {
                root: root || "",
                path: path || "",
                enabled: enabled === true
            };
        },

        dispose: () => {
            if (state.disposed) {
                return;
            }

            state.disposed = true;
            state.generation += 1;
            detach();
            if (state.refreshTimer) {
                clearTimeout(state.refreshTimer);
                state.refreshTimer = null;
            }

            for (const item of state.items.values()) {
                if (item.xhr) {
                    item.xhr.abort();
                }
            }

            state.dotNetRef = null;
            state.queue = [];
            state.items.clear();
            state.active = 0;

            if (state.panel) {
                state.panel.remove();
                state.panel = null;
                state.list = null;
            }
        }
    };
})();
