"use strict";

function generateSessionId() {
    // preferred on https / localhost
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    // fallback for plain http
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

const sessionClientId = generateSessionId();

// Canvas DOM elements
const container = document.getElementById("canvasContainer");
const canvas = document.getElementById("paintCanvas");
const ctx = canvas.getContext("2d");
const previewCanvas = document.getElementById("previewCanvas");
const previewCtx = previewCanvas.getContext("2d");

// Color DOM elements
const mainColorDisplay = document.getElementById("mainColorDisplay");
const secondaryColorDisplay = document.getElementById("secondaryColorDisplay");
const palette = document.getElementById("palette");

// Chat DOM elments
const chatInput = document.getElementById("chatInput");
const chatMessages = document.getElementById("chatMessages");
const sizeInput = document.getElementById("size");

// States
let painting = false;
let currentMainColor = "#000";
let currentSecondaryColor = "#fff";

let strokeLocalId = -1;
let strokePoints = []
let lineStartPoint = null;

let inputEventQueue = []
let currentTool = 'brush';

let reconnectDelay = 1000;

// Compaction: after the initial sync, if the live event log for this room has grown
// past this many events, the client uploads a raster snapshot of the pre-session
// state so the server can flatten and drop the old events. Keeps storage + replay
// bounded without losing the drawing.
const SNAPSHOT_THRESHOLD = 400;
let snapshotUploadAttempted = false;
let snapshotCheckScheduled = false;
let userHasDrawn = false;

function scheduleSnapshotCheck(attempt = 0) {
    if (attempt > 15) return;
    setTimeout(() => {
        const finalized = events.maybeUploadSnapshot();
        if (!finalized) scheduleSnapshotCheck(attempt + 1);
    }, 2500);
}

let localFlipH = false;
let localFlipV = false;

let _panX = 0, _panY = 0, _zoom = 1.0;
let _isPanning = false, _spaceDown = false;
let _panStartX = 0, _panStartY = 0, _panStartPanX = 0, _panStartPanY = 0;
let _canvasWrapper = null;

function _clampPan() {
    const minVisible = 80;
    const displayW = canvas.width  * _zoom;
    const displayH = canvas.height * _zoom;
    const cw = container.clientWidth;
    const ch = container.clientHeight;
    _panX = Math.max(minVisible - displayW, Math.min(cw - minVisible, _panX));
    _panY = Math.max(minVisible - displayH, Math.min(ch - minVisible, _panY));
}

function _applyViewTransform() {
    if (_canvasWrapper) {
        _clampPan();
        _canvasWrapper.style.transform = `translate(${_panX}px,${_panY}px) scale(${_zoom})`;
    }
}

function _fitCanvasToView() {
    const cw = container.clientWidth;
    const ch = container.clientHeight;
    if (cw <= 0 || ch <= 0) return;
    _zoom = Math.min(1.0, (cw - 80) / canvas.width, (ch - 80) / canvas.height);
    _zoom = Math.max(0.1, _zoom);
    _panX = (cw - canvas.width  * _zoom) / 2;
    _panY = (ch - canvas.height * _zoom) / 2;
    _applyViewTransform();
}


function currentBrush() {
    const width = Math.ceil(sizeInput.value);
    if (currentTool === 'eraser')   return { width, color: '#ffffff' };
    if (currentTool === 'pen')      return { width: 1, color: currentMainColor };
    if (currentTool === 'airbrush') return { width, color: currentMainColor, airbrush: true };
    return { width, color: currentMainColor };
}

function setCurrentTool(tool) {
    currentTool = tool;
    document.querySelectorAll('.tool[id]').forEach(el => el.classList.remove('active'));
    const el = document.getElementById(tool);
    if (el) el.classList.add('active');
    if (_canvasWrapper) {
        const cursors = { magnifier: 'grab', eyedropper: 'crosshair' };
        canvas.style.cursor = cursors[tool] ?? 'crosshair';
    }
    lineStartPoint = null;
    previewCtx?.clearRect(0, 0, previewCanvas?.width ?? 0, previewCanvas?.height ?? 0);
}

function floodFill(ctx, startX, startY, fillColor) {
    const w = ctx.canvas.width, h = ctx.canvas.height;
    const imgData = ctx.getImageData(0, 0, w, h);
    const d = imgData.data;

    const fr = parseInt(fillColor.slice(1, 3), 16);
    const fg = parseInt(fillColor.slice(3, 5), 16);
    const fb = parseInt(fillColor.slice(5, 7), 16);

    const i0 = (startY * w + startX) * 4;
    const tr = d[i0], tg = d[i0 + 1], tb = d[i0 + 2];

    if (tr === fr && tg === fg && tb === fb) return;

    const visited = new Uint8Array(w * h);
    const queue   = new Int32Array(w * h);
    let head = 0, tail = 0;

    const enq = (x, y) => {
        const pos = y * w + x;
        if (visited[pos]) return;
        visited[pos] = 1;
        queue[tail++] = pos;
    };
    enq(startX, startY);

    while (head < tail) {
        const pos = queue[head++];
        const x = pos % w, y = (pos / w) | 0;
        const i = pos * 4;
        if (Math.abs(d[i] - tr) + Math.abs(d[i+1] - tg) + Math.abs(d[i+2] - tb) > 48) continue;
        d[i] = fr; d[i+1] = fg; d[i+2] = fb; d[i+3] = 255;
        if (x > 0)     enq(x - 1, y);
        if (x < w - 1) enq(x + 1, y);
        if (y > 0)     enq(x, y - 1);
        if (y < h - 1) enq(x, y + 1);
    }
    ctx.putImageData(imgData, 0, 0);
}

let cache = {
    db: null,

    async init() {
        this.roomId = new URLSearchParams(window.location.search).get('roomId');
        if (this.roomId == null) this.roomId = "";

        this.db = await new Promise((resolve, reject) => {
            const openRequest = window.indexedDB.open("canvas_cache", 2);
            openRequest.onsuccess = (e) => {
                resolve(e.target.result);
            };
            openRequest.onerror = (e) => {
                reject("failed to open db:" + e.target.error);
            };
            openRequest.onupgradeneeded = (e) => {
                const db = e.target.result;
                if (!db.objectStoreNames.contains("checkpoints")) db.createObjectStore("checkpoints", { keyPath: ["roomId", "globalId"]});
            };
        });

        // Clear all local checkpoints on every page load. The server is the source
        // of truth and a fresh client reconstructs the canvas correctly by replaying
        // events from globalId 0, so we must never seed redraws from a snapshot taken
        // in a previous page session — those can be stale (e.g. different hidden set)
        // and would leave the canvas blank/desynced after a reload. Checkpoints built
        // during the current session still speed up in-session redraws.
        await new Promise((resolve) => {
            const tx = this.db.transaction(["checkpoints"], "readwrite");
            tx.objectStore("checkpoints").delete(
                IDBKeyRange.bound(
                    [this.roomId, 0],
                    [this.roomId, Number.MAX_SAFE_INTEGER]
                )
            );
            tx.oncomplete = () => resolve();
            tx.onerror = () => resolve();
        });
    },
    clearCheckpoints() {
        if (!this.db) return;
        const tx = this.db.transaction(["checkpoints"], "readwrite");
        tx.objectStore("checkpoints").delete(
            IDBKeyRange.bound(
                [this.roomId, 0],
                [this.roomId, Number.MAX_SAFE_INTEGER]
            )
        );
    },
    storeCheckpoint(globalId, image) {
        const transaction = this.db.transaction(["checkpoints"], "readwrite");
        const checkpoints = transaction.objectStore("checkpoints");

        const request = checkpoints.put({
            roomId: this.roomId,
            globalId: globalId,
            image: image,
        });

        request.onsuccess = () => {
            console.log("checkpoint saved", globalId);
            this.pruneCheckpoints(globalId);
        };
        request.onerror = () => {
            console.error("failed to store checkpoint", request, e.target.error);
        }
    },
    pruneCheckpoints(latestId) {
        const store = this.db
            .transaction(["checkpoints"], "readwrite")
            .objectStore("checkpoints");
        store.getAllKeys(
            IDBKeyRange.bound([this.roomId, 0], [this.roomId, latestId])
        ).onsuccess = (e) => {
            e.target.result.forEach(key => {
                const age = latestId - key[1];
                const keep = age <= 50
                    || (age <= 200  && key[1] % 50  === 0)
                    || (age <= 1000 && key[1] % 200 === 0)
                    || key[1] % 1000 === 0;
                if (!keep) store.delete(key);
            });
        };
    },
    async accessCheckpointBefore(globalId) {
        return new Promise((resolve, reject) => {
            const transaction = this.db.transaction(["checkpoints"], "readwrite");
            const store = transaction.objectStore("checkpoints");

            if (globalId == 0) {
               resolve(null);
               return;
            }

            const searchRange = IDBKeyRange.bound(
                [this.roomId, 0],
                [this.roomId, globalId],
                false, true // [0, globalId)
            );


            const request = store.openCursor(searchRange, "prev"); // go backwards to find newest
            request.onsuccess = (e) => {
                const cursor = e.target.result;
                if (cursor && cursor.value.roomId == this.roomId) {
                     this.nextCheckpointId = cursor.value.globalId + 10;
                    resolve(cursor.value);
                } else {
                    resolve(null);
                }
            };
            request.onerror = (e) => {
                reject(e.target.error);
            };


            // Remove newer checkpoints as they may have had different hidden set
            const deleteRange = IDBKeyRange.bound(
                [this.roomId, globalId],
                [this.roomId, Number.MAX_SAFE_INTEGER],
            );
            store.delete(deleteRange);
        });
    }

}

const connection = {
    ws: null,
    connected: false,
    // Stable, unique-per-page-load identity used to tag every event/preview this
    // client produces. MUST stay unique across sessions/reloads — the server's
    // numeric connection id gets reused (e.g. 0) between sessions, which made
    // (connection, local) keys collide and replayed events get dropped as dupes.
    id: sessionClientId,
    // The server-assigned numeric connection id, used only for the disconnect
    // protocol (clearing a departed peer's in-flight preview).
    serverId: null,

    connect() {
        const roomId = new URLSearchParams(window.location.search).get('roomId');

        var url = new URL('/room_ws', window.location.href);
        url.protocol = url.protocol.replace('http', 'ws');
        url.searchParams.set('roomId', roomId);
        // Keep the password in sessionStorage (which is scoped to this tab and cleared
        // when the tab closes) so reconnects after a dropped socket and reloads of the
        // same tab can re-authenticate. Removing it here broke both cases.
        const storedPassword = sessionStorage.getItem(`roomPassword_${roomId}`);
        if (storedPassword) {
            url.searchParams.set('password', storedPassword);
        }
        this.ws = new WebSocket(url.href);
        this.ws.onopen = (e) => {
            console.info('WebSocket opened');
            showNewMessage("**System**: connected");
            this.connected = true;
            reconnectDelay = 1000; // reset backoff after a successful connection
            // Don't request events here: the server sends a `snapshot ...` message
            // right after connect that establishes the compaction baseline and then
            // drives the initial event request (see events.applySnapshot).
            if (!snapshotCheckScheduled) {
                snapshotCheckScheduled = true;
                scheduleSnapshotCheck();
            }
        }
        this.ws.onerror = (e) => {
            console.error("WebSocket error:", e);

        }
        this.ws.onmessage = async (e) => {
            console.log("WebSocket recieved:", e.data);

            if (e.data.startsWith("msg ")) {
                showNewMessage(e.data.substring("msg ".length));
            } else if (e.data.startsWith("assigned_id ")) {
                // Keep this.id as the stable session UUID; only record the server's
                // numeric id separately. Overwriting this.id here was the root cause
                // of events being dropped on replay across sessions.
                this.serverId = Number(e.data.substring("assigned_id ".length));
                console.log("assigned server id", this.serverId, "session id", this.id);
            } else if (e.data.startsWith("canvas_size ")) {
                const [width, height] = e.data
                    .substring("canvas_size ".length)
                    .split(" ", 2)
                    .map(Number);
                setCanvasSize(width, height);
            } else if (e.data.startsWith("snapshot ")) {
                const rest = e.data.substring("snapshot ".length);
                const sep = rest.indexOf(" ");
                const snapGlobalId = Number(rest.substring(0, sep));
                const url = rest.substring(sep + 1);
                await events.applySnapshot(snapGlobalId, url);
            } else if (e.data === "clear") {
                events.handleClear();
            } else if (e.data.startsWith("disconnect ")) {
                const endedConnectionId = Number(e.data.substring("disconnect ".length));
                let dirty = false;
                for (const [key, preview] of events.brushStrokePreviews.entries()) {
                    if (preview.serverId === endedConnectionId) {
                        events.brushStrokePreviews.delete(key);
                        dirty = true;
                    }
                }
                if (dirty) events.brushStrokePreviewsDirty = true;
            } else if (e.data.startsWith("new_version ")) {
                const version = Number(e.data.substring("new_version ".length));
                console.log("new version", version);

                events.newestVersion = version;
                this.requestEvents(events.recievedVersion + 1);

            } else if (e.data.startsWith("event ")) {
                const content = e.data.substring("event ".length);
                const objectStart = content.indexOf("{");

                const id = Number(content.substring(0, objectStart));
                const event = JSON.parse(content.substring(objectStart));

                console.log("event recieved global_id: ", id, "event: ", event);

                events.eventRecieved(id, event)
            } else if (e.data.startsWith("broadcast ")) {
                const obj = JSON.parse(e.data.substring("broadcast ".length));
                console.log("broadcast recieved: ", obj);


                if (obj.kind == "brushPreview") {
                    events.brushPreviewRecieved(obj);
                } else {
                    console.error("unknown broadcast kind");
                }


            } else if (e.data.startsWith("error ")) {
                const detail = e.data.substring("error ".length);
                console.error("Server error:", detail);
                showNewMessage("**System**: server error — " + detail);
            } else {
                console.log("unknown message");
            }
        },
        this.ws.onclose = (e) => {
            console.info("WebSocket closed");
            if (this.connected) showNewMessage("**System**: disconnected — working offline");
            this.connected = false;
            painting = false; // abort any in-progress stroke so it isn't lost half-sent
            setTimeout(() => {
                reconnectDelay = Math.min(reconnectDelay * 2, 30000); // exponential backoff, capped at 30s
                this.connect();
            }, reconnectDelay);
        }
    },
    sendChatMessage(message) {
        if (!this.connected) return;
        try {
            this.ws.send("msg " + message);
        } catch (e) {
            console.error("error sending chat message: ", e);
        }
    },
    sendCanvasEvent(eventObj) {
        if (!this.connected) {
            console.warn("Dropped canvas event (offline):", eventObj.kind);
            return;
        }
        try {
            this.ws.send("event " + JSON.stringify(eventObj));
        } catch (err) {
            console.error("Failed to send canvas event:", err);
        }
    },
    trySendBroadcast(obj) {
        if (!this.connected) return;
        try {
            this.ws.send("broadcast " + JSON.stringify(obj));
        } catch (e) {
            console.error("failed to send broadcast", e);
        }
    },
    requestEvents(start, end) {
        if (this.ws?.readyState !== WebSocket.OPEN) return;
        if (!end) end = '';
        this.ws.send(`get_events ${start} ${end}`);
    }
}

const events = {
    nextLocalEventId: 0,

    globalPending: [], // just recieved waiting for processing during animationFrame
    global: [],

    localDisplayed: [],

    recievedVersion: -1,
    newestVersion: 0,

    // The globalId baseline covered by the server-side snapshot image (-1 = none).
    // liveCount = recievedVersion - snapshotBaseGlobalId.
    snapshotBaseGlobalId: -1,
    // True while the snapshot image is being fetched/drawn; pauses event processing
    // so replayed events never land on a blank canvas before the base image is ready.
    snapshotLoading: false,

    undoStack: [],
    undoStackIndex: -1,

    nextCheckpointId: 10,

    localIdToGlobalId: new Map(),


    brushStrokePreviews: new Map(),
    brushStrokePreviewsDirty: false,

    newLocalId() {
        return this.nextLocalEventId++;
    },
    key(connection, localId) {
        return `${connection}:${localId}`;
    },

    async applySnapshot(snapGlobalId, url) {
        this.snapshotBaseGlobalId = snapGlobalId;

        // Only rebuild from the snapshot image when the server's baseline is ahead of
        // what we already have: the initial page load, or a reconnect after the server
        // compacted past our position. On a normal reconnect we keep our in-memory
        // state and simply resync the events that came after.
        if (snapGlobalId > this.recievedVersion) {
            this.snapshotLoading = true;
            this.global = [];
            this.globalPending = [];
            this.localDisplayed = [];
            this.localIdToGlobalId.clear();
            this.recievedVersion = snapGlobalId;
            this.nextCheckpointId = snapGlobalId + 10;

            ctx.fillStyle = "white";
            ctx.fillRect(0, 0, canvas.width, canvas.height);

            if (url && url !== "-") {
                try {
                    const response = await fetch(url, { cache: "no-store" });
                    if (response.ok) {
                        const blob = await response.blob();
                        // Seed a checkpoint at the baseline so in-session redraws rebuild
                        // on top of the snapshot instead of from a blank canvas.
                        cache.storeCheckpoint(snapGlobalId, blob);
                        const bitmap = await createImageBitmap(blob);
                        ctx.drawImage(bitmap, 0, 0);
                        bitmap.close();
                    } else {
                        console.warn("snapshot image fetch failed:", response.status);
                    }
                } catch (err) {
                    console.error("failed to load snapshot image", err);
                }
            }

            this.snapshotLoading = false;
        }

        connection.requestEvents(this.recievedVersion + 1);
    },

    handleClear() {
        this.global = [];
        this.globalPending = [];
        this.localDisplayed = [];
        this.localIdToGlobalId.clear();
        this.recievedVersion = -1;
        this.newestVersion = 0;
        this.snapshotBaseGlobalId = -1;
        this.snapshotLoading = false;
        this.nextCheckpointId = 10;
        this.undoStack = [];
        this.undoStackIndex = -1;
        this.brushStrokePreviews.clear();
        this.brushStrokePreviewsDirty = true;

        cache.clearCheckpoints();

        ctx.fillStyle = "white";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        previewCtx.clearRect(0, 0, previewCanvas.width, previewCanvas.height);

        showNewMessage("**System**: canvas cleared");
    },

    // Called after the initial sync settles. Returns true once a final decision has
    // been made (stop polling), false while still catching up.
    maybeUploadSnapshot() {
        if (snapshotUploadAttempted) return true;
        if (!connection.connected) return false;
        // Still syncing or mid-stroke — wait for a quiet moment.
        if (this.snapshotLoading || this.globalPending.length !== 0 || painting) return false;

        snapshotUploadAttempted = true;

        // Preserve undo: only fold PRE-session events into a snapshot. If the user has
        // already drawn this session, skip and let a later visit compact instead.
        if (userHasDrawn || this.localDisplayed.length !== 0) return true;

        const liveCount = this.recievedVersion - this.snapshotBaseGlobalId;
        if (liveCount <= SNAPSHOT_THRESHOLD) return true;

        const roomId = new URLSearchParams(window.location.search).get('roomId');
        if (!roomId) return true;

        const g0 = this.recievedVersion;
        const imageBase64 = canvas.toDataURL("image/png");
        fetch(`/api/rooms/${roomId}/snapshot`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ imageBase64: imageBase64, globalId: g0 })
        })
            .then(r => (r.ok ? r.json() : null))
            .then(res => {
                if (res && res.applied) {
                    this.snapshotBaseGlobalId = g0;
                    console.log("[compaction] snapshot applied at globalId", g0);
                }
            })
            .catch(err => console.warn("snapshot upload failed", err));

        return true;
    },

    async redrawFromCheckpoint(beforeGlobalId) {
        console.log("find checkpoint before ", beforeGlobalId);
        const checkpoint = await cache.accessCheckpointBefore(beforeGlobalId);
        let checkpointGlobalId = -1;
        if (checkpoint) {
            checkpointGlobalId = checkpoint.globalId;

            console.log("used checkpoint", checkpoint.globalId);

            const bitmap = await createImageBitmap(checkpoint.image);
            ctx.drawImage(bitmap, 0, 0);
            bitmap.close();
        } else {
            console.log("no checkpoint found before", beforeGlobalId);
            ctx.fillStyle = "white";
            ctx.fillRect(0, 0, canvas.width, canvas.height);
        }

        function* events(events, afterGlobalId) {
            for (const e of events.global) {
                if (e.globalId > afterGlobalId) {
                    yield e;
                }
            }
            yield* events.localDisplayed;
        }

        const hidden = new Set();
        for (const e of events(this, checkpointGlobalId)) {
            if (e.kind == "visible") {
                if (e.visible) {
                    hidden.delete(this.key(e.connection, e.target));
                } else {
                    hidden.add(this.key(e.connection, e.target));
                }
            }
        }

        for (const e of events(this, checkpointGlobalId)) {
            if (e.kind == "visible" || hidden.has(this.key(e.connection, e.local))) continue;
            drawEvent(ctx, e);

            if (e.globalId && e.globalId > this.nextCheckpointId) { // this only saves when the rollback was required
                this.nextCheckpointId = e.globalId + 10;
                canvas.toBlob((blob) => {
                    cache.storeCheckpoint(e.globalId, blob);
                });
            }
        }
    },

    async processPendingEvents() {
        // Wait until the snapshot base image is loaded so replayed events draw on top
        // of it rather than on a blank canvas.
        if (this.snapshotLoading) return;

        let redrawBeforeGlobalId = Number.MAX_SAFE_INTEGER;
        let redrawRequired = false;

        // Process buffered events strictly in ascending globalId order. On a gap
        // (missing id) request the missing range and stop until it arrives, instead
        // of dropping out-of-order events (which previously caused desync).
        this.globalPending.sort((a, b) => a.globalId - b.globalId);

        let i = 0;
        while (i < this.globalPending.length) {
            const pending = this.globalPending[i];

            // Already applied — drop duplicate.
            if (pending.globalId <= this.recievedVersion) {
                this.globalPending.splice(i, 1);
                continue;
            }

            // Gap — ask the server to resend from the next expected id, then wait.
            if (pending.globalId !== this.recievedVersion + 1) {
                connection.requestEvents(this.recievedVersion + 1, pending.globalId);
                break;
            }

            this.recievedVersion = pending.globalId;
            this.globalPending.splice(i, 1);

            const event = pending.event;
            event.globalId = pending.globalId;
            const key = this.key(event.connection, event.local);

            // NOTE: do NOT skip on `localIdToGlobalId.has(key)`. Duplicate globalIds
            // are already filtered in eventRecieved, and each distinct event has a
            // unique globalId. Skipping by (connection, local) wrongly discarded
            // events whose key collided with an earlier one (legacy data where the
            // numeric connection id was reused across sessions), which is exactly
            // why replayed strokes vanished on reload.
            this.global.push(event);
            this.localIdToGlobalId.set(key, event.globalId);
            if (this.brushStrokePreviews.delete(key)) {
                this.brushStrokePreviewsDirty = true;
            }

            if (this.localDisplayed.length != 0 && connection.id == event.connection && event.local == this.localDisplayed[0].local) {
                this.localDisplayed.shift();
            } else {
                redrawRequired = true;
                if (event.kind == "visible") {
                    const targetGlobalId = this.localIdToGlobalId.get(this.key(event.connection, event.target));
                    redrawBeforeGlobalId = Math.min(redrawBeforeGlobalId, targetGlobalId ?? Number.MAX_SAFE_INTEGER);
                }
            }
        }

        if (redrawRequired) await this.redrawFromCheckpoint(redrawBeforeGlobalId);

        if (this.brushStrokePreviewsDirty) {
            this.brushStrokePreviewsDirty = false;

            previewCtx.clearRect(0, 0, previewCanvas.width, previewCanvas.height);

            for (const preview of this.brushStrokePreviews.values()) {
               drawBrushStroke(previewCtx, preview.brush, preview.points);
            }

            if (painting) {
                drawBrushStroke(previewCtx, currentBrush(), strokePoints);
            }
        }
    },
    eventRecieved(globalId, event) {
        // Buffer everything (even out-of-order); ordering and gap handling happen
        // in processPendingEvents. Skip ids we already have or already buffered.
        if (this.global.some(e => e.globalId === globalId)) return;
        if (this.globalPending.some(p => p.globalId === globalId)) return;
        this.globalPending.push({ globalId: globalId, event: event });
    },
    brushPreviewRecieved(payload) {
        if (payload.connection == connection.id) return;
        this.brushStrokePreviews.set(this.key(payload.connection, payload.local), payload);
        this.brushStrokePreviewsDirty = true;
    },
    commitBrushStroke(localId, brush, points) {
        userHasDrawn = true;
        const e = {
            connection: connection.id,
            local: localId,
            kind: "brush",
            brush: brush,
            points: points
        }
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);

        this.brushStrokePreviewsDirty = true;

        drawEvent(ctx, e);

        this.undoStack.length = this.undoStackIndex + 1; // truncate history
        this.undoStack.push(localId);
        this.undoStackIndex++;
    },
    commitShape(localId, kind, x1, y1, x2, y2, brush) {
        userHasDrawn = true;
        const e = { connection: connection.id, local: localId, kind, x1, y1, x2, y2, brush };
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);
        drawEvent(ctx, e);
        this.undoStack.length = this.undoStackIndex + 1;
        this.undoStack.push(localId);
        this.undoStackIndex++;
    },
    commitFill(localId, x, y, color) {
        userHasDrawn = true;
        const e = { connection: connection.id, local: localId, kind: "fill", color, x, y };
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);
        drawEvent(ctx, e);
        this.undoStack.length = this.undoStackIndex + 1;
        this.undoStack.push(localId);
        this.undoStackIndex++;
    },
    commitSetVisible(localId, visible) {
        userHasDrawn = true;
        const id = this.newLocalId();
        const e = {
            connection: connection.id,
            local: id,
            target: localId,
            kind: "visible",
            visible: visible,
        };
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);

        let beforeGlobalId = this.localIdToGlobalId.get(this.key(connection.id, localId));
        if (beforeGlobalId === undefined) {
            console.assert(this.localDisplayed.some(e => e.local == localId));
            beforeGlobalId = Number.MAX_SAFE_INTEGER;
        }
        this.redrawFromCheckpoint(beforeGlobalId);
    },


    undo() {
        if (this.undoStackIndex >= 0) {
            const localId = this.undoStack[this.undoStackIndex];
            this.undoStackIndex--;
            this.commitSetVisible(localId, false);
        }
    },
    redo() {
        if (this.undoStackIndex < this.undoStack.length - 1) {
            this.undoStackIndex++;
            const localId = this.undoStack[this.undoStackIndex];
            this.commitSetVisible(localId, true);
        }
    }
}

async function initApp() {
    await cache.init();
    initTools();
    initCanvas();
    initPallette();
    initChat();
    connection.connect();
}

function initTools() {
    ['pen', 'brush', 'eraser', 'bucket', 'eyedropper', 'magnifier', 'airbrush', 'line', 'rect', 'ellipse'].forEach(name => {
        const el = document.getElementById(name);
        if (el) el.addEventListener('click', () => setCurrentTool(name));
    });
}

function initCanvas() {
    _canvasWrapper = document.createElement('div');
    _canvasWrapper.style.cssText = 'position:absolute;top:0;left:0;transform-origin:0 0;';
    _canvasWrapper.appendChild(canvas);
    _canvasWrapper.appendChild(previewCanvas);
    container.appendChild(_canvasWrapper);

    for (const c of [canvas, previewCanvas]) {
        c.style.position       = 'absolute';
        c.style.top            = '0';
        c.style.left           = '0';
        c.style.imageRendering = 'pixelated';
    }
    canvas.style.cursor = 'crosshair';

    setCanvasSize(1024, 1024);

    let isPinching = false;
    let pinchInit = null;

    canvas.addEventListener("mousedown", (e) => {
        if (e.button === 0 && !_spaceDown && !_isPanning) inputEventQueue.push(e);
    });

    canvas.addEventListener("touchstart", (e) => {
        if (e.touches.length === 1 && !isPinching) {
            e.preventDefault();
            inputEventQueue.push(e);
        }
    }, { passive: false });

    container.addEventListener("touchstart", (e) => {
        if (e.touches.length >= 2) {
            e.preventDefault();
            isPinching = true;
            painting = false;
            const cr = container.getBoundingClientRect();
            const cx = (e.touches[0].clientX + e.touches[1].clientX) / 2 - cr.left;
            const cy = (e.touches[0].clientY + e.touches[1].clientY) / 2 - cr.top;
            pinchInit = {
                cx, cy,
                dist: Math.hypot(e.touches[1].clientX - e.touches[0].clientX,
                                 e.touches[1].clientY - e.touches[0].clientY),
                panX: _panX, panY: _panY, zoom: _zoom
            };
        }
    }, { passive: false });

    window.addEventListener("mouseup", (e) => {
        if (_isPanning && (e.button === 1 || e.button === 0)) {
            _isPanning = false;
            container.style.cursor = _spaceDown ? 'grab' : '';
        }
        inputEventQueue.push(e);
    });
    window.addEventListener("touchend", (e) => {
        if (e.touches.length < 2) { isPinching = false; pinchInit = null; }
        if (e.touches.length === 0 && _isPanning && (currentTool === 'hand' || currentTool === 'magnifier')) {
            _isPanning = false;
            canvas.style.cursor = 'grab';
        }
        inputEventQueue.push(e);
    });

    window.addEventListener("mousemove", (e) => {
        if (_isPanning) {
            _panX = _panStartPanX + (e.clientX - _panStartX);
            _panY = _panStartPanY + (e.clientY - _panStartY);
            _applyViewTransform();
        }
        inputEventQueue.push(e);
    });
    window.addEventListener("touchmove", (e) => {
        if (isPinching && e.touches.length >= 2 && pinchInit) {
            e.preventDefault();
            const cr  = container.getBoundingClientRect();
            const cx  = (e.touches[0].clientX + e.touches[1].clientX) / 2 - cr.left;
            const cy  = (e.touches[0].clientY + e.touches[1].clientY) / 2 - cr.top;
            const dist = Math.hypot(e.touches[1].clientX - e.touches[0].clientX,
                                    e.touches[1].clientY - e.touches[0].clientY);

            const newZoom = Math.max(0.1, Math.min(8, pinchInit.zoom * (dist / pinchInit.dist)));
            const wx = (pinchInit.cx - pinchInit.panX) / pinchInit.zoom;
            const wy = (pinchInit.cy - pinchInit.panY) / pinchInit.zoom;
            _zoom = newZoom;
            _panX = cx - wx * newZoom;
            _panY = cy - wy * newZoom;
            _applyViewTransform();
        } else if (_isPanning && e.touches.length >= 1) {
            e.preventDefault();
            _panX = _panStartPanX + (e.touches[0].clientX - _panStartX);
            _panY = _panStartPanY + (e.touches[0].clientY - _panStartY);
            _applyViewTransform();
        } else if (!isPinching && !_isPanning) {
            inputEventQueue.push(e);
        }
    }, { passive: false });

    window.addEventListener("keydown", (e) => {
        if (e.code === 'Space' && !e.target.matches('input,textarea,[contenteditable]')) {
            if (!_spaceDown) { _spaceDown = true; container.style.cursor = 'grab'; }
            e.preventDefault();
        }
        inputEventQueue.push(e);
    });
    window.addEventListener("keyup", (e) => {
        if (e.code === 'Space') {
            _spaceDown = false;
            if (!_isPanning) container.style.cursor = '';
        }
    });

    window.addEventListener("dragstart", (e) => { e.preventDefault(); });

    container.addEventListener("mousedown", (e) => {
        if (e.button === 1 || (e.button === 0 && _spaceDown)) {
            e.preventDefault();
            _isPanning = true;
            _panStartX    = e.clientX;
            _panStartY    = e.clientY;
            _panStartPanX = _panX;
            _panStartPanY = _panY;
            container.style.cursor = 'grabbing';
        }
    }, true);

    container.addEventListener('wheel', (e) => {
        e.preventDefault();
        const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
        const cr = container.getBoundingClientRect();
        const mx = e.clientX - cr.left, my = e.clientY - cr.top;
        const wx = (mx - _panX) / _zoom, wy = (my - _panY) / _zoom;
        _zoom = Math.max(0.1, Math.min(8, _zoom * factor));
        _panX = mx - wx * _zoom;
        _panY = my - wy * _zoom;
        _applyViewTransform();
    }, { passive: false });

    window.canvasZoomReset  = _fitCanvasToView;
    window.getCanvasZoom    = () => _zoom;

    requestAnimationFrame(animationFrame);
}

function setCanvasSize(width, height) {
    if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) return;
    if (canvas.width == width && canvas.height == height) return;

    previewCanvas.width = canvas.width = width;
    previewCanvas.height = canvas.height = height;
    previewCanvas.style.width = canvas.style.width = width + "px";
    previewCanvas.style.height = canvas.style.height = height + "px";

    if (_canvasWrapper) _fitCanvasToView();

    ctx.fillStyle = "white";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    previewCtx.clearRect(0, 0, previewCanvas.width, previewCanvas.height);
}

async function animationFrame(timestamp) {
    for (const e of inputEventQueue) {

        const clientXY = () => ({
            x: e.clientX ?? e.touches?.[0]?.clientX,
            y: e.clientY ?? e.touches?.[0]?.clientY
        });
        const canvasXY = (cx, cy) => {
            const rect = canvas.getBoundingClientRect();
            let x = Math.floor((cx - rect.left) * canvas.width  / rect.width);
            let y = Math.floor((cy - rect.top)  * canvas.height / rect.height);
            if (localFlipH) x = canvas.width  - 1 - x;
            if (localFlipV) y = canvas.height - 1 - y;
            return { x: Math.max(0, Math.min(canvas.width-1,  x)),
                     y: Math.max(0, Math.min(canvas.height-1, y)) };
        };

        switch (e.type) {
            case "mousedown":
            case "touchstart":
            {
                const { x: cx, y: cy } = clientXY();
                // Pan/zoom/eyedropper are local-only and allowed offline; everything
                // that mutates the shared canvas is blocked while disconnected.
                const localOnlyTool = currentTool === 'hand' || currentTool === 'magnifier' || currentTool === 'eyedropper';
                if (!connection.connected && !localOnlyTool) {
                    showNewMessage("**System**: Cannot draw while offline.");
                    break;
                }
                if (currentTool === 'hand' || currentTool === 'magnifier') {
                    _isPanning = true;
                    _panStartX = cx; _panStartY = cy;
                    _panStartPanX = _panX; _panStartPanY = _panY;
                    canvas.style.cursor = 'grabbing';
                } else if (currentTool === 'eyedropper') {
                    const { x, y } = canvasXY(cx, cy);
                    const px = ctx.getImageData(x, y, 1, 1).data;
                    selectColor('#' + [px[0], px[1], px[2]].map(v => v.toString(16).padStart(2,'0')).join(''));
                } else if (currentTool === 'bucket') {
                    const { x, y } = canvasXY(cx, cy);
                    events.commitFill(events.newLocalId(), x, y, currentMainColor);
                } else if (['line', 'rect', 'ellipse'].includes(currentTool)) {
                    lineStartPoint = canvasXY(cx, cy);
                    painting = true;
                    strokeLocalId = events.newLocalId();
                } else {
                    painting = true;
                    strokePoints = [];
                    strokeLocalId = events.newLocalId();
                    draw(e);
                }
                break;
            }
            case "mouseup":
            case "touchend":
            {
                if (['line', 'rect', 'ellipse'].includes(currentTool) && painting && lineStartPoint) {
                    previewCtx.clearRect(0, 0, previewCanvas.width, previewCanvas.height);
                    const ex = e.clientX ?? e.changedTouches?.[0]?.clientX;
                    const ey = e.clientY ?? e.changedTouches?.[0]?.clientY;
                    const end = canvasXY(ex, ey);
                    if (currentTool === 'line') {
                        events.commitBrushStroke(strokeLocalId, currentBrush(), [lineStartPoint, end]);
                    } else {
                        events.commitShape(strokeLocalId, currentTool,
                            lineStartPoint.x, lineStartPoint.y, end.x, end.y, currentBrush());
                    }
                    lineStartPoint = null; painting = false;
                } else if (painting) {
                    painting = false;
                    events.commitBrushStroke(strokeLocalId, currentBrush(), strokePoints);
                }
                if (_isPanning && (currentTool === 'hand' || currentTool === 'magnifier')) {
                    _isPanning = false;
                    canvas.style.cursor = 'grab';
                }
                break;
            }
            case "mousemove":
            case "touchmove":
            {
                if (['line', 'rect', 'ellipse'].includes(currentTool) && painting && lineStartPoint) {
                    previewCtx.clearRect(0, 0, previewCanvas.width, previewCanvas.height);
                    const { x: cx, y: cy } = clientXY();
                    const end = canvasXY(cx, cy);
                    const b = currentBrush();
                    previewCtx.save();
                    previewCtx.strokeStyle = b.color;
                    previewCtx.lineWidth   = b.width;
                    if (currentTool === 'line') {
                        drawBrushStroke(previewCtx, b, [lineStartPoint, end]);
                    } else if (currentTool === 'rect') {
                        previewCtx.lineCap = 'square'; previewCtx.lineJoin = 'miter';
                        previewCtx.strokeRect(lineStartPoint.x + 0.5, lineStartPoint.y + 0.5,
                            end.x - lineStartPoint.x, end.y - lineStartPoint.y);
                    } else if (currentTool === 'ellipse') {
                        const rx = Math.abs(end.x - lineStartPoint.x) / 2;
                        const ry = Math.abs(end.y - lineStartPoint.y) / 2;
                        if (rx > 0 && ry > 0) {
                            previewCtx.beginPath();
                            previewCtx.ellipse(
                                (lineStartPoint.x + end.x) / 2, (lineStartPoint.y + end.y) / 2,
                                rx, ry, 0, 0, Math.PI * 2);
                            previewCtx.stroke();
                        }
                    }
                    previewCtx.restore();
                } else {
                    draw(e);
                }
                break;
            }
            case "keydown":
            {
                if (!painting) {
                    if (e.ctrlKey && e.code == "KeyZ") {
                        if (e.shiftKey) events.redo();
                        else events.undo();
                    }
                }
                break;
            }
        }
    }
    inputEventQueue.length = 0;

    await events.processPendingEvents();

    requestAnimationFrame(animationFrame);
}

function drawEvent(ctx, e) {
    if (e.kind === "brush") {
        drawBrushStroke(ctx, e.brush, e.points);
    } else if (e.kind === "fill") {
        floodFill(ctx, e.x, e.y, e.color);
    } else if (e.kind === "rect") {
        ctx.save();
        ctx.strokeStyle = e.brush.color;
        ctx.lineWidth   = e.brush.width;
        ctx.lineCap = 'square'; ctx.lineJoin = 'miter';
        ctx.strokeRect(e.x1 + 0.5, e.y1 + 0.5, e.x2 - e.x1, e.y2 - e.y1);
        ctx.restore();
    } else if (e.kind === "ellipse") {
        const rx = Math.abs(e.x2 - e.x1) / 2, ry = Math.abs(e.y2 - e.y1) / 2;
        if (rx > 0 && ry > 0) {
            ctx.save();
            ctx.strokeStyle = e.brush.color;
            ctx.lineWidth   = e.brush.width;
            ctx.lineCap = 'round';
            ctx.beginPath();
            ctx.ellipse((e.x1 + e.x2) / 2, (e.y1 + e.y2) / 2, rx, ry, 0, 0, Math.PI * 2);
            ctx.stroke();
            ctx.restore();
        }
    } else {
        console.error("unknown event kind", e.kind);
    }
}

function drawBrushStroke(ctx, brush, points) {
    if (brush.airbrush) { drawAirbrushStroke(ctx, brush, points); return; }
    ctx.lineWidth = brush.width;
    ctx.strokeStyle = brush.color;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.beginPath();
    ctx.moveTo(points[0].x + 0.5, points[0].y + 0.5);
    for (const { x, y } of points) {
        ctx.lineTo(x + 0.5, y + 0.5);
    }
    ctx.stroke();
}

function drawAirbrushStroke(ctx, brush, points) {
    const radius = brush.width * 4;
    const dots   = Math.max(6, brush.width * 4);
    ctx.fillStyle = brush.color;
    for (const { x, y } of points) {
        for (let i = 0; i < dots; i++) {
            // same hash for same coords = same pattern on all clients
            const t = (x * 3 + y * 7 + i * 137) % 628;
            const r = ((x * 5 + y * 11 + i * 53) % 100) / 100 * radius;
            ctx.fillRect(
                Math.round(x + Math.cos(t / 100) * r),
                Math.round(y + Math.sin(t / 100) * r),
                2, 2
            );
        }
    }
}

function initPallette() {
    const colors = [
        "#000000",
        "#808080",
        "#800000",
        "#808000",
        "#008000",
        "#008080",
        "#000080",
        "#800080",
        "#808040",
        "#004040",
        "#0080ff",
        "#004080",
        "#4000ff",
        "#804000",
        "#ffffff",
        "#c0c0c0",
        "#ff0000",
        "#ffff00",
        "#00ff00",
        "#00ffff",
        "#0000ff",
        "#ff00ff",
        "#ffff80",
        "#00ff80",
        "#80ffff",
        "#8080ff",
        "#ff0080",
        "#ff8040",
    ];

    colors.forEach((color) => {
        const colorBox = document.createElement("div");
        colorBox.className = "palette-color";
        colorBox.style.backgroundColor = color;
        colorBox.onclick = () => selectColor(color);
        palette.appendChild(colorBox);
    });
}

// dummy chat
function initChat() {
    document
        .getElementById("chatInput")
        .addEventListener("keypress", function (e) {
            if (e.key === "Enter") sendMessage();
        });
}

function selectColor(color) {
    currentMainColor = color;
    mainColorDisplay.style.backgroundColor = color;
}

function toggleMainColor() {
    // App logic
    let temp = currentMainColor;
    currentMainColor = currentSecondaryColor;
    currentSecondaryColor = temp;

    // DOM Elements
    mainColorDisplay.style.backgroundColor = currentMainColor;
    secondaryColorDisplay.style.backgroundColor = currentSecondaryColor;
}

function draw(e) {
    if (!painting) return;
    const rect = canvas.getBoundingClientRect();

    function getClientCoords(e) {
        if (e.touches && e.touches.length > 0) {
            return {
                x: e.touches[0].clientX,
                y: e.touches[0].clientY
            };
        } else {
            return {
                x: e.clientX,
                y: e.clientY
            };
        }
    }

    let { x, y } = getClientCoords(e);

    x = Math.floor((x - rect.left) * canvas.width / rect.width);
    y = Math.floor((y - rect.top) * canvas.height / rect.height);

    if (localFlipH) x = canvas.width  - 1 - x;
    if (localFlipV) y = canvas.height - 1 - y;

    // Same point
    if (strokePoints.length > 1 && x == strokePoints[strokePoints.length - 1].x && y == strokePoints[strokePoints.length - 1].y) return;

    // Replace old point if it was on the same line and behind the new one.
    // Makes one pixel wide straight lines look much better.
    var replaced = false
    if (strokePoints.length >= 2) {
        const start    = strokePoints[strokePoints.length - 2];
        const previous = strokePoints[strokePoints.length - 1];

        const ax = previous.x - start.x;
        const ay = previous.y - start.y;

        const bx = x - start.x;
        const by = y - start.y;

        // Point on the same line: ax / ay = bx / by
        // Note: exact equality is okay since the coordinates are rounded to integers
        if (ax * by == ay * bx) {
            const dotBA = bx * ax + by * ay;
            const dotAA = ax * ax + ay * ay;
            // New point is further along the line
            if (dotBA > dotAA) {
                replaced = true;
                strokePoints[strokePoints.length - 1] = { x, y };
            }
        }
    }
    if (!replaced) strokePoints.push({ x: x, y: y });


    const brush = currentBrush();


    events.brushStrokePreviewsDirty = true;
    connection.trySendBroadcast({
        kind: "brushPreview",
        connection: connection.id,
        serverId: connection.serverId,
        local: strokeLocalId,
        brush: brush,
        points: strokePoints
    });
}

function toggleChat() {
    const body = document.getElementById("chatBody");
    body.classList.toggle("open");
}

function showNewMessage(text) {
    const msg = document.createElement("div");
    msg.classList.add("message");
    msg.textContent = text;
    chatMessages.appendChild(msg);
    chatMessages.scrollTop = chatMessages.scrollHeight;
}

function sendMessage() {
    if (chatInput.value.trim() !== "") {
        connection.sendChatMessage(chatInput.value);
        chatInput.value = "";
    }
}
window.toggleChat       = toggleChat;
window.sendMessage      = sendMessage;
window.toggleMainColor  = toggleMainColor;
window.selectColor      = selectColor;
window.setTool          = setCurrentTool;
window.setLocalFlip = (h, v) => {
    localFlipH = h; localFlipV = v;
    // flip is CSS-only, wrapper handles the rest
    let t = '';
    if (h) t += 'scaleX(-1) ';
    if (v) t += 'scaleY(-1) ';
    canvas.style.transform = previewCanvas.style.transform = t.trim();
};

await initApp();
