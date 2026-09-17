window.netLabLayout = {
    getRect: function (el) {
        const rect = el.getBoundingClientRect();
        return { top: rect.top, left: rect.left, width: rect.width, height: rect.height };
    },
    capturePointer: function (el, pointerId) {
        el.setPointerCapture(pointerId);
    },
    releasePointer: function (el, pointerId) {
        if (el.hasPointerCapture(pointerId)) {
            el.releasePointerCapture(pointerId);
        }
    },
    setSplit: function (el, percent) {
        el.style.setProperty("--pane-split", `${percent}%`);
        el.classList.toggle("resizing", true);
    },
    beginSplit: function (workspace, splitter, pointerId, clientX, clientY) {
        if (!workspace || !splitter) {
            return;
        }

        const apply = (x, y) => {
            const rect = workspace.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) {
                return;
            }

            const stacked = workspace.classList.contains("stacked");
            let percent = stacked
                ? ((y - rect.top) / rect.height) * 100
                : ((x - rect.left) / rect.width) * 100;
            percent = Math.min(75, Math.max(25, percent));
            workspace.style.setProperty("--pane-split", `${percent}%`);
            workspace.classList.add("resizing");
            workspace._labSplit = percent;
        };

        const onMove = (e) => {
            if ((e.buttons & 1) === 0) {
                return;
            }

            apply(e.clientX, e.clientY);
        };

        splitter._labSplitMove = onMove;
        splitter._labSplitPointerId = pointerId;
        try {
            splitter.setPointerCapture(pointerId);
        } catch {
        }

        splitter.addEventListener("pointermove", onMove);
        apply(clientX, clientY);
    },
    endSplit: function (workspace, splitter) {
        if (splitter && splitter._labSplitMove) {
            splitter.removeEventListener("pointermove", splitter._labSplitMove);
            const pointerId = splitter._labSplitPointerId;
            splitter._labSplitMove = null;
            splitter._labSplitPointerId = null;
            try {
                if (pointerId != null && splitter.hasPointerCapture(pointerId)) {
                    splitter.releasePointerCapture(pointerId);
                }
            } catch {
            }
        }

        if (workspace) {
            workspace.classList.remove("resizing");
            return typeof workspace._labSplit === "number" ? workspace._labSplit : null;
        }

        return null;
    },
    zoneFromPoint: function (el, clientX, clientY) {
        if (!el) {
            return "center";
        }

        const rect = el.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) {
            return "center";
        }

        const x = (clientX - rect.left) / rect.width;
        const y = (clientY - rect.top) / rect.height;
        const edge = 0.25;
        if (x < edge) {
            return "left";
        }

        if (x > 1 - edge) {
            return "right";
        }

        if (y < edge) {
            return "top";
        }

        if (y > 1 - edge) {
            return "bottom";
        }

        return "center";
    },
    dropZone: function (clientX, clientY) {
        const bodies = document.querySelectorAll(".lab-group-body");
        for (const el of bodies) {
            const rect = el.getBoundingClientRect();
            if (clientX >= rect.left && clientX <= rect.right && clientY >= rect.top && clientY <= rect.bottom) {
                return window.netLabLayout.zoneFromPoint(el, clientX, clientY);
            }
        }

        const hit = document.elementFromPoint(clientX, clientY);
        return window.netLabLayout.zoneFromPoint(hit && hit.closest(".lab-group-body"), clientX, clientY);
    },
    currentDrag: function () {
        return window.netLabDrag || {};
    },
    beginDrop: function (pane) {
        document.querySelectorAll(".lab-group-body").forEach(function (el) {
            const host = el.closest("[data-lab-pane]");
            if (!pane || (host && host.getAttribute("data-lab-pane") === pane)) {
                el.classList.add("lab-drop-ready");
            }
        });
    },
    hoverDrop: function (clientX, clientY) {
        window.netLabLayout.clearTabDrop();
        const hit = document.elementFromPoint(clientX, clientY);
        const body = hit && hit.closest(".lab-group-body");
        const zone = window.netLabLayout.zoneFromPoint(body, clientX, clientY);
        document.querySelectorAll(".lab-drop-zone").forEach(function (el) {
            const active = body && body.contains(el);
            el.className = active ? "lab-drop-zone " + zone : "lab-drop-zone";
        });
        return zone;
    },
    clearTabDrop: function () {
        document.querySelectorAll(".lab-tab.drop-before, .lab-tab.drop-after").forEach(function (el) {
            el.classList.remove("drop-before", "drop-after");
        });
    },
    hoverTab: function (clientX, clientY) {
        window.netLabLayout.clearTabDrop();
        document.querySelectorAll(".lab-drop-zone").forEach(function (el) {
            el.className = "lab-drop-zone";
        });

        const drag = window.netLabDrag;
        if (!drag || drag.kind !== "tab") {
            return;
        }

        const hit = document.elementFromPoint(clientX, clientY);
        const list = hit && hit.closest(".lab-tab-list");
        if (!list) {
            return;
        }

        const tabs = [...list.querySelectorAll(".lab-tab")].filter(function (tab) {
            return tab.getAttribute("data-lab-pane") === drag.pane;
        });
        if (tabs.length === 0) {
            return;
        }

        let target = hit.closest(".lab-tab");
        if (target && target.getAttribute("data-lab-pane") !== drag.pane) {
            return;
        }

        if (!target) {
            const last = tabs[tabs.length - 1];
            if (last.getAttribute("data-lab-tab") !== drag.value) {
                last.classList.add("drop-after");
            }

            window.netLabTabDropAfter = true;
            return;
        }

        if (target.getAttribute("data-lab-tab") === drag.value) {
            return;
        }

        const rect = target.getBoundingClientRect();
        const after = clientX > rect.left + rect.width / 2;
        window.netLabTabDropAfter = after;
        target.classList.add(after ? "drop-after" : "drop-before");
        const neighbor = after ? target.nextElementSibling : target.previousElementSibling;
        if (neighbor && neighbor.classList.contains("lab-tab") && neighbor.getAttribute("data-lab-tab") !== drag.value) {
            neighbor.classList.add(after ? "drop-before" : "drop-after");
        }
    },
    tabDropAfter: function () {
        return !!window.netLabTabDropAfter;
    },
    endDrop: function () {
        window.netLabDrag = null;
        window.netLabTabDropAfter = false;
        window.netLabLayout.clearVisual();
    },
    clearVisual: function () {
        document.querySelectorAll(".lab-tab.lab-tab-dragging").forEach(function (el) {
            el.classList.remove("lab-tab-dragging");
        });
        window.netLabLayout.clearTabDrop();
        document.querySelectorAll(".lab-group-body").forEach(function (el) {
            el.classList.remove("lab-drop-ready");
        });
        document.querySelectorAll(".lab-drop-zone").forEach(function (el) {
            el.className = "lab-drop-zone";
        });
    }
};

if (!window.netLabDropBound) {
    window.netLabDropBound = true;
    document.addEventListener("dragstart", function (e) {
        const tab = e.target.closest("[data-lab-tab]");
        if (!tab) {
            return;
        }

        const drag = {
            kind: "tab",
            pane: tab.getAttribute("data-lab-pane"),
            group: tab.getAttribute("data-lab-group"),
            value: tab.getAttribute("data-lab-tab")
        };

        window.netLabDrag = drag;
        tab.classList.add("lab-tab-dragging");
        try {
            e.dataTransfer.effectAllowed = "move";
            e.dataTransfer.setData("text/plain", drag.value || "tab");
        } catch {
        }

        window.netLabLayout.beginDrop(drag.pane);
    }, true);

    document.addEventListener("dragover", function (e) {
        if (!window.netLabDrag) {
            return;
        }

        const overlay = e.target.closest(".lab-drop-overlay");
        if (overlay) {
            e.preventDefault();
            e.dataTransfer.dropEffect = "move";
            window.netLabLayout.hoverDrop(e.clientX, e.clientY);
            return;
        }

        if (e.target.closest(".lab-tab, .lab-tab-list")) {
            e.preventDefault();
            e.dataTransfer.dropEffect = "move";
            window.netLabLayout.hoverTab(e.clientX, e.clientY);
        }
    }, true);

    document.addEventListener("drop", function (e) {
        if (e.target.closest(".lab-drop-overlay, .lab-tab, .lab-tab-list")) {
            e.preventDefault();
        }
    }, true);

    document.addEventListener("dragend", function () {
        window.netLabLayout.clearVisual();
    }, true);
}
