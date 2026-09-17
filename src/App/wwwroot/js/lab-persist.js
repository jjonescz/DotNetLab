window.netLabPrefs = {
    outputTabsKey: "netlab-output-tabs",
    settingsKey: "netlab-settings",
    readOutputTabs: function () {
        try {
            return localStorage.getItem(this.outputTabsKey) || "";
        } catch {
            return "";
        }
    },
    persistOutputTabs: function (json) {
        try {
            if (!json) {
                localStorage.removeItem(this.outputTabsKey);
                return;
            }

            localStorage.setItem(this.outputTabsKey, json);
        } catch {
        }
    },
    readSettings: function () {
        try {
            return localStorage.getItem(this.settingsKey) || "";
        } catch {
            return "";
        }
    },
    persistSettings: function (json) {
        try {
            if (!json) {
                localStorage.removeItem(this.settingsKey);
                return;
            }

            localStorage.setItem(this.settingsKey, json);
        } catch {
        }
    }
};

window.netLabCompileCache = {
    dbName: "netlab-compile-v1",
    storeName: "entries",
    maxEntries: 16,
    _db: null,
    open: function () {
        if (this._db) {
            return Promise.resolve(this._db);
        }

        if (!window.indexedDB) {
            return Promise.reject(new Error("indexedDB unavailable"));
        }

        const self = this;
        return new Promise(function (resolve, reject) {
            const request = indexedDB.open(self.dbName, 1);
            request.onupgradeneeded = function () {
                const db = request.result;
                if (!db.objectStoreNames.contains(self.storeName)) {
                    const store = db.createObjectStore(self.storeName, { keyPath: "key" });
                    store.createIndex("accessed", "accessed");
                }
            };
            request.onsuccess = function () {
                self._db = request.result;
                self._db.onclose = function () {
                    self._db = null;
                };
                resolve(self._db);
            };
            request.onerror = function () {
                reject(request.error);
            };
        });
    },
    request: function (req) {
        return new Promise(function (resolve, reject) {
            req.onsuccess = function () {
                resolve(req.result);
            };
            req.onerror = function () {
                reject(req.error);
            };
        });
    },
    get: async function (key) {
        try {
            const db = await this.open();
            const read = db.transaction(this.storeName, "readonly");
            const row = await this.request(read.objectStore(this.storeName).get(key));
            if (!row || !row.json) {
                return "";
            }

            const write = db.transaction(this.storeName, "readwrite");
            row.accessed = Date.now();
            write.objectStore(this.storeName).put(row);
            return JSON.stringify({ output: row.json, timestamp: row.timestamp });
        } catch {
            this._db = null;
            return "";
        }
    },
    put: async function (key, json, timestamp) {
        try {
            const db = await this.open();
            const write = db.transaction(this.storeName, "readwrite");
            write.objectStore(this.storeName).put({
                key: key,
                json: json,
                timestamp: timestamp,
                accessed: Date.now()
            });
            await this.complete(write);

            const countTx = db.transaction(this.storeName, "readonly");
            const count = await this.request(countTx.objectStore(this.storeName).count());
            if (count <= this.maxEntries) {
                return;
            }

            let extra = count - this.maxEntries;
            const evict = db.transaction(this.storeName, "readwrite");
            const index = evict.objectStore(this.storeName).index("accessed");
            await new Promise(function (resolve, reject) {
                const cursor = index.openCursor();
                cursor.onsuccess = function (e) {
                    const current = e.target.result;
                    if (!current || extra <= 0) {
                        resolve();
                        return;
                    }

                    current.delete();
                    extra--;
                    current.continue();
                };
                cursor.onerror = function () {
                    reject(cursor.error);
                };
            });
        } catch {
            this._db = null;
        }
    },
    complete: function (tx) {
        return new Promise(function (resolve, reject) {
            tx.oncomplete = function () {
                resolve();
            };
            tx.onerror = function () {
                reject(tx.error);
            };
            tx.onabort = function () {
                reject(tx.error);
            };
        });
    }
};

window.netLabMemory = {
    collectAndDownloadGcDump: async function () {
        const runtime = globalThis.getDotnetRuntime?.(0);
        if (!runtime?.collectGcDump) {
            return;
        }

        const result = await runtime.collectGcDump({ skipDownload: true });
        const blob = new Blob(result, { type: "application/octet-stream" });
        const blobUrl = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.download = `app.trace.${Date.now()}.nettrace`;
        link.href = blobUrl;
        document.body.appendChild(link);
        link.dispatchEvent(new MouseEvent("click", {
            bubbles: true, cancelable: true, view: window,
        }));
        link.remove();
        URL.revokeObjectURL(blobUrl);
    }
};

window.netLabUrl = {
    hash: function () {
        return (window.location.hash || "").replace(/^#/, "");
    }
};
