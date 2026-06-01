"use strict";
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
        };
        request.onerror = () => {
            console.error("failed to store checkpoint", request, e.target.error);
        }
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
    id: Math.random(), // FIXME

    connect() {
        const roomId = new URLSearchParams(window.location.search).get('roomId');

        var url = new URL('/room_ws', window.location.href);
        url.protocol = url.protocol.replace('http', 'ws');
        url.searchParams.set('roomId', roomId);
        this.ws = new WebSocket(url.href);
        this.ws.onopen = (e) => {
            console.info('WebSocket opened');
            showNewMessage("**System**: connected");
            this.connected = true;
            this.requestEvents(events.recievedVersion + 1);
        }
        this.ws.onerror = (e) => {
            console.error("WebSocket error:", e);

        }
        this.ws.onmessage = async (e) => {
            console.log("WebSocket recieved:", e.data);

            if (e.data.startsWith("msg ")) {
                showNewMessage(e.data.substring("msg ".length));
            } else if (e.data.startsWith("assigned_id ")) {
                this.id = Number(e.data.substring("assigned_id ".length));
                console.log("assigned connection id", this.id);
            } else if (e.data.startsWith("canvas_size ")) {
                const [width, height] = e.data
                    .substring("canvas_size ".length)
                    .split(" ", 2)
                    .map(Number);
                setCanvasSize(width, height);
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


            } else {
                console.log("unknown message");
            }
        },
        this.ws.onclose = (e) => {
            console.info("WebSocket closed");
            if (this.connected) showNewMessage("**System**: disconnected");
            this.connected = false;
            setTimeout(() => { this.connect() }, 5_000); // TODO: Increase delay on with each failed attempt
        }
    },
    sendChatMessage(message) {
        try {
            this.ws.send("msg " + message);
        } catch (e) {
            console.error("error sending chat message: ", e);
        }
    },
    sendCanvasEvent(eventObj) {
        this.ws.send("event " + JSON.stringify(eventObj));
    },
    trySendBroadcast(obj) {
        try {
            this.ws.send("broadcast " + JSON.stringify(obj));
        } catch (e) {
            console.error("failed to send broadcast", e);
        }
    },
    requestEvents(start, end) {
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
        let redrawBeforeGlobalId = Number.MAX_SAFE_INTEGER;
        let redrawRequired = false;

        for (const {globalId, event} of this.globalPending) {
            event.globalId = globalId;
            this.global.push(event);
            const key = this.key(event.connection, event.local);

            this.localIdToGlobalId.set(key, globalId);
            if (this.brushStrokePreviews.delete(key)) {
                this.brushStrokePreviewsDirty = true;
            }

            console.log("new_global with key", globalId, this.key(event.connection, event.local));

            if (this.localDisplayed.length != 0 && connection.id == event.connection && event.local == this.localDisplayed[0].local) {
                this.localDisplayed.shift();
            } else {
                redrawRequired = true;
                if (event.kind == "visible") {
                    const targetGlobalId = this.localIdToGlobalId.get(this.key(event.connection, event.target));
                    redrawBeforeGlobalId = Math.min(redrawBeforeGlobalId, targetGlobalId);
                }

            }
        }
        this.globalPending.length = 0;

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
        if (globalId != this.recievedVersion + 1) return; // Out of order event, it could just be saved for later but whatever

        this.globalPending.push({ globalId: globalId, event: event });
        this.recievedVersion = globalId;
    },
    brushPreviewRecieved(payload) {
        if (payload.connection == connection.id) return;
        this.brushStrokePreviews.set(this.key(payload.connection, payload.local), payload);
        this.brushStrokePreviewsDirty = true;
    },
    commitBrushStroke(localId, brush, points) {
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
        const e = { connection: connection.id, local: localId, kind, x1, y1, x2, y2, brush };
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);
        drawEvent(ctx, e);
        this.undoStack.length = this.undoStackIndex + 1;
        this.undoStack.push(localId);
        this.undoStackIndex++;
    },
    commitFill(localId, x, y, color) {
        const e = { connection: connection.id, local: localId, kind: "fill", color, x, y };
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);
        drawEvent(ctx, e);
        this.undoStack.length = this.undoStackIndex + 1;
        this.undoStack.push(localId);
        this.undoStackIndex++;
    },
    commitSetVisible(localId, visible) {
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
