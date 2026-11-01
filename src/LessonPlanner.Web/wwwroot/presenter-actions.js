(() => {
    "use strict";
    if (!window.fetch || !window.FormData) return;

    const docxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    function prepare(container, control) {
        const feedback = container.querySelector("[data-action-feedback]");
        const label = control.textContent;
        let locked = false;

        function disable(value) {
            locked = value;
            control.setAttribute("aria-disabled", String(value));
            if (control.tagName === "BUTTON") control.disabled = value;
        }

        function show(message, state) {
            feedback.hidden = false;
            feedback.dataset.state = state;
            feedback.textContent = message;
        }

        function release() {
            disable(false);
            container.setAttribute("aria-busy", "false");
            control.textContent = label;
        }

        async function failure(response) {
            release();
            if (response.status === 429) {
                // A limit response rejects this request. Retry remains an explicit user action.
                const retry = response.headers.get("Retry-After");
                const seconds = retry && /^\d+$/.test(retry) ? Number(retry)
                    : retry ? Math.ceil((Date.parse(retry) - Date.now()) / 1000) : 0;
                if (Number.isFinite(seconds) && seconds > 0) {
                    const readyAt = Date.now() + seconds * 1000;
                    disable(true);
                    control.textContent = "Please wait";
                    const update = () => {
                        const remaining = Math.max(0, Math.ceil((readyAt - Date.now()) / 1000));
                        if (remaining === 0) {
                            clearInterval(timer);
                            release();
                            show("You can try again now. Your request will start when you select the action.", "error");
                        } else {
                            show(`The current request limit has been reached. Try again in ${remaining} seconds.`, "error");
                        }
                    };
                    const timer = setInterval(update, 1000);
                    update();
                } else {
                    show("The current request limit has been reached. Wait a moment, then try again.", "error");
                }
            } else if (response.status === 401 || response.status === 403 || response.redirected) {
                show("Your presenter session needs checking. Reload this page to sign in before trying again.", "error");
            } else if (response.status === 400) {
                show("The request could not be verified. Reload this page before trying again.", "error");
            } else if (response.status === 404) {
                show("This lesson is unavailable. Check the lesson link or return to the lesson workbench.", "error");
            } else if (response.headers.get("Content-Type")?.includes("application/json")) {
                const body = await response.json();
                show(body.message || "The request could not be completed. No request has been repeated.", "error");
            } else {
                show("Completion could not be confirmed. Keep this page and check the outcome before trying again. No request has been repeated.", "error");
            }
            feedback.focus();
        }

        async function run(request, complete) {
            if (locked) return;
            disable(true);
            container.setAttribute("aria-busy", "true");
            control.textContent = container.dataset.pendingLabel;
            show(container.dataset.pendingMessage, "pending");
            try {
                const response = await request();
                if (!response.ok || response.redirected) {
                    await failure(response);
                    return;
                }
                await complete(response);
            } catch {
                release();
                show("Completion could not be confirmed. Check the outcome before trying again. No request has been repeated.", "error");
                feedback.focus();
            }
        }
        return { run, release, show };
    }

    document.querySelectorAll("form[data-presenter-action]").forEach(form => {
        const action = prepare(form, form.querySelector('button[type="submit"]'));
        form.addEventListener("submit", event => {
            event.preventDefault();
            action.run(() => fetch(form.action, {
                method: "POST",
                body: new FormData(form),
                headers: { "X-Lumero-Action": "fetch" },
                credentials: "same-origin"
            }), async response => {
                const body = await response.json();
                const destination = new URL(body.location, window.location.origin);
                if (!body.location || destination.origin !== window.location.origin ||
                    !/^\/presenter\/(lessons|drafts)\/[a-f0-9]{48}$/.test(destination.pathname)) {
                    throw new Error("Unconfirmed action destination");
                }
                window.location.assign(destination.href);
            });
        });
    });

    document.querySelectorAll("[data-presenter-download]").forEach(container => {
        const control = container.querySelector("a");
        const action = prepare(container, control);
        control.addEventListener("click", event => {
            if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            action.run(() => fetch(control.href, { credentials: "same-origin" }), async response => {
                if (response.headers.get("Content-Type")?.split(";")[0] !== docxType) {
                    throw new Error("Unconfirmed document response");
                }
                const blob = await response.blob();
                if (!blob.size) throw new Error("Empty document response");
                const url = URL.createObjectURL(blob);
                const link = document.createElement("a");
                const disposition = response.headers.get("Content-Disposition") || "";
                const filename = /filename="?([^";]+)"?/i.exec(disposition)?.[1];
                link.href = url;
                link.download = filename?.replace(/[\\/:*?"<>|]/g, "_") || "lesson.docx";
                document.body.append(link);
                link.click();
                link.remove();
                setTimeout(() => URL.revokeObjectURL(url), 60_000);
                action.release();
                action.show("Your lesson document is ready. The download has been handed to your browser.", "success");
            });
        });
    });
})();
