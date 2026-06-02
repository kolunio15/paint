"use strict";

const container = document.getElementById("canvasContainer");
const canvas = document.getElementById("paintCanvas");
const ctx = canvas.getContext("2d");
const previewCanvas = document.getElementById("previewCanvas");
const previewCtx = previewCanvas.getContext("2d");

const mainColorDisplay = document.getElementById("mainColorDisplay");
const secondaryColorDisplay = document.getElementById("secondaryColorDisplay");
const palette = document.getElementById("palette");

const chatInput = document.getElementById("chatInput");
const chatMessages = document.getElementById("chatMessages");
const sizeInput = document.getElementById("size");

function generateSessionId() {
    // used on https or localhost connections
    if (window.crypto && crypto.randomUUID) {
        return crypto.randomUUID();
    }
    // fallback for http connections
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
        const r = Math.random() * 16 | 0;
        const v = c === 'x' ? r : (r & 0x3 | 0x8);
        return v.toString(16);
    });
}

const sessionClientId = generateSessionId();

let painting = false;
let currentMainColor = "#000000";
let currentSecondaryColor = "#ffffff";
let currentTool = "brush";
let strokeButton = 0;

let strokeLocalId = -1;
let strokePoints = [];

let moveState = "idle";
let moveStartPos = null;
let moveSelectionRect = null;
let moveOffscreenCanvas = null;

let panState = { active: false, startX: 0, startY: 0, scrollX: 0, scrollY: 0 };

let inputEventQueue = [];
let reconnectDelay = 1000;

function currentBrush() {
    const size = Math.ceil(sizeInput.value);
    if (currentTool === "eraser") return { width: size, color: "#ffffff" };
    const color = strokeButton === 2 ? currentSecondaryColor : currentMainColor;
    if (currentTool === "pen") return { width: 1, color: color };
    return { width: size, color: color };
}

const loadImg = (src) =>
    new Promise((resolve) => {
        const img = new Image();
        img.onload = () => resolve(img);
        img.src = src;
    });

let cache = {
    db: null,
    async init() {
        this.roomId =
            new URLSearchParams(window.location.search).get("roomId") || "";
        this.db = await new Promise((resolve, reject) => {
            const openRequest = window.indexedDB.open("canvas_cache", 2);
            openRequest.onsuccess = (e) => resolve(e.target.result);
            openRequest.onerror = (e) =>
                reject("failed to open db:" + e.target.error);
            openRequest.onupgradeneeded = (e) => {
                const db = e.target.result;
                if (!db.objectStoreNames.contains("checkpoints"))
                    db.createObjectStore("checkpoints", {
                        keyPath: ["roomId", "globalId"],
                    });
            };
        });

        if (!sessionStorage.getItem("db_cleared_" + this.roomId)) {
            sessionStorage.setItem("db_cleared_" + this.roomId, "true");
            const tx = this.db.transaction(["checkpoints"], "readwrite");
            tx.objectStore("checkpoints").delete(
                IDBKeyRange.bound(
                    [this.roomId, 0],
                    [this.roomId, Number.MAX_SAFE_INTEGER],
                ),
            );
        }
    },
    storeCheckpoint(globalId, image) {
        this.db
            .transaction(["checkpoints"], "readwrite")
            .objectStore("checkpoints")
            .put({
                roomId: this.roomId,
                globalId: globalId,
                image: image,
            }).onsuccess = () => {
            this.pruneCheckpoints(globalId);
        };
    },
    pruneCheckpoints(latestId) {
        const store = this.db
            .transaction(["checkpoints"], "readwrite")
            .objectStore("checkpoints");
        store.getAllKeys(
            IDBKeyRange.bound([this.roomId, 0], [this.roomId, latestId]),
        ).onsuccess = (e) => {
            e.target.result.forEach((key) => {
                const age = latestId - key[1];
                let keep =
                    age <= 50 ||
                    (age <= 200 && key[1] % 50 === 0) ||
                    (age <= 1000 && key[1] % 200 === 0) ||
                    key[1] % 1000 === 0;
                if (!keep) store.delete(key);
            });
        };
    },
    async accessCheckpointBefore(globalId) {
        return new Promise((resolve, reject) => {
            const store = this.db
                .transaction(["checkpoints"], "readwrite")
                .objectStore("checkpoints");
            if (globalId == 0) {
                resolve(null);
                return;
            }

            store.openCursor(
                IDBKeyRange.bound(
                    [this.roomId, 0],
                    [this.roomId, globalId],
                    false,
                    true,
                ),
                "prev",
            ).onsuccess = (e) => {
                const cursor = e.target.result;
                if (cursor && cursor.value.roomId == this.roomId) {
                    this.nextCheckpointId = cursor.value.globalId + 10;
                    resolve(cursor.value);
                } else {
                    resolve(null);
                }
            };
            store.delete(
                IDBKeyRange.bound(
                    [this.roomId, globalId],
                    [this.roomId, Number.MAX_SAFE_INTEGER],
                ),
            );
        });
    },
};

const connection = {
    ws: null,
    connected: false,
    id: null,
    connect() {
        const roomId =
            new URLSearchParams(window.location.search).get("roomId") || "1";
        var url = new URL("/room_ws", window.location.href);
        url.protocol = url.protocol.replace("http", "ws");
        url.searchParams.set("roomId", roomId);

        this.ws = new WebSocket(url.href);
        this.ws.onopen = () => {
            showNewMessage("**System**: Connected to server.");
            this.connected = true;
            reconnectDelay = 1000;
            this.requestEvents(Math.max(0, events.recievedVersion + 1));
        };
        this.ws.onerror = (e) => console.error("WebSocket error:", e);
        ((this.ws.onmessage = async (e) => {
            if (e.data.startsWith("msg ")) {
                showNewMessage(e.data.substring("msg ".length));
            } else if (e.data.startsWith("assigned_id ")) {
                this.id = Number(e.data.split(" ")[1]);
            } else if (e.data.startsWith("canvas_size ")) {
                const parts = e.data.split(" ");
                const width = Number(parts[1]);
                const height = Number(parts[2]);

                if (canvas.width !== width || canvas.height !== height) {
                    canvas.width = previewCanvas.width = width;
                    canvas.height = previewCanvas.height = height;
                    ctx.fillStyle = "white";
                    ctx.fillRect(0, 0, width, height);
                }

                for (const ev of events.localDisplayed) {
                    this.sendCanvasEvent(ev);
                }
                await events.redrawFromCheckpoint(Number.MAX_SAFE_INTEGER);
            } else if (e.data.startsWith("disconnect ")) {
                const endedConnectionId = Number(e.data.split(" ")[1]);
                let dirty = false;
                for (const [key, val] of events.brushStrokePreviews.entries()) {
                    if (val.connection === endedConnectionId) {
                        events.brushStrokePreviews.delete(key);
                        dirty = true;
                    }
                }
                if (dirty) events.brushStrokePreviewsDirty = true;
            } else if (e.data.startsWith("new_version ")) {
                events.newestVersion = Number(
                    e.data.substring("new_version ".length),
                );
                this.requestEvents(events.recievedVersion + 1);
            } else if (e.data.startsWith("event ")) {
                const content = e.data.substring("event ".length);
                const objectStart = content.indexOf("{");
                events.eventRecieved(
                    Number(content.substring(0, objectStart)),
                    JSON.parse(content.substring(objectStart)),
                );
            } else if (e.data.startsWith("broadcast ")) {
                const obj = JSON.parse(e.data.substring("broadcast ".length));
                if (obj.kind == "brushPreview" || obj.kind == "shapePreview")
                    events.brushPreviewRecieved(obj);
            } else if (e.data.startsWith("error ")) {
                console.error("Server Error:", e.data);
            }
        }),
            (this.ws.onclose = () => {
                if (this.connected)
                    showNewMessage(
                        "**System**: You have been disconnected! Working offline.",
                        true,
                    );
                this.connected = false;
                painting = false;
                setTimeout(() => {
                    reconnectDelay = Math.min(reconnectDelay * 2, 30000);
                    this.connect();
                }, reconnectDelay);
            }));
    },
    sendChatMessage(message) {
        if (this.connected) this.ws.send("msg " + message);
    },
    sendCanvasEvent(eventObj) {
        if (this.connected) this.ws.send("event " + JSON.stringify(eventObj));
    },
    trySendBroadcast(obj) {
        if (this.connected)
            try {
                this.ws.send("broadcast " + JSON.stringify(obj));
            } catch (e) {}
    },
    requestEvents(start, end = "") {
        if (this.ws?.readyState === WebSocket.OPEN)
            this.ws.send(`get_events ${start} ${end}`);
    },
};

const events = {
    nextLocalEventId: 0,
    globalPending: [],
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
    key(authorId, localId) {
        return `${authorId}:${localId}`;
    },

    async redrawFromCheckpoint(beforeGlobalId) {
        const checkpoint = await cache.accessCheckpointBefore(beforeGlobalId);
        let checkpointGlobalId = -1;
        if (checkpoint) {
            checkpointGlobalId = checkpoint.globalId;
            const bitmap = await createImageBitmap(checkpoint.image);
            ctx.drawImage(bitmap, 0, 0);
            bitmap.close();
        } else {
            ctx.fillStyle = "white";
            ctx.fillRect(0, 0, canvas.width, canvas.height);
        }

        const self = this;
        function* getCombinedEvents(afterGlobalId) {
            for (const e of self.global)
                if (e.globalId > afterGlobalId) yield e;
            yield* self.localDisplayed;
        }

        const hidden = new Set();
        for (const e of getCombinedEvents(checkpointGlobalId)) {
            if (e.kind == "visible") {
                const targetKey = this.key(e.targetAuthor, e.targetLocal);
                if (e.visible) hidden.delete(targetKey);
                else hidden.add(targetKey);
            }
        }

        for (const e of getCombinedEvents(checkpointGlobalId)) {
            if (e.kind == "visible" || hidden.has(this.key(e.author, e.local)))
                continue;
            await drawEvent(ctx, e);
            if (e.globalId && e.globalId > this.nextCheckpointId) {
                this.nextCheckpointId = e.globalId + 10;
                canvas.toBlob((blob) =>
                    cache.storeCheckpoint(e.globalId, blob),
                );
            }
        }
    },

    async processPendingEvents() {
        let redrawBeforeGlobalId = Number.MAX_SAFE_INTEGER;
        let redrawRequired = false;

        this.globalPending.sort((a, b) => a.globalId - b.globalId);
        let i = 0;

        while (i < this.globalPending.length) {
            const pending = this.globalPending[i];
            if (pending.globalId <= this.recievedVersion) {
                this.globalPending.splice(i, 1);
                continue;
            }

            if (pending.globalId === this.recievedVersion + 1) {
                this.recievedVersion = pending.globalId;
                const event = pending.event;
                event.globalId = pending.globalId;
                const key = this.key(event.author, event.local);

                if (!this.localIdToGlobalId.has(key)) {
                    this.global.push(event);
                    this.localIdToGlobalId.set(key, pending.globalId);

                    if (this.brushStrokePreviews.delete(key))
                        this.brushStrokePreviewsDirty = true;

                    if (
                        this.localDisplayed.length != 0 &&
                        event.author === this.localDisplayed[0].author &&
                        event.local === this.localDisplayed[0].local
                    ) {
                        this.localDisplayed.shift();
                    } else {
                        redrawRequired = true;
                        if (event.kind == "visible") {
                            const targetKey = this.key(
                                event.targetAuthor,
                                event.targetLocal,
                            );
                            redrawBeforeGlobalId = Math.min(
                                redrawBeforeGlobalId,
                                this.localIdToGlobalId.get(targetKey) ??
                                    Number.MAX_SAFE_INTEGER,
                            );
                        }
                    }
                }
                this.globalPending.splice(i, 1);
            } else {
                connection.requestEvents(
                    this.recievedVersion + 1,
                    pending.globalId,
                );
                break;
            }
        }

        if (redrawRequired)
            await this.redrawFromCheckpoint(redrawBeforeGlobalId);

        if (this.brushStrokePreviewsDirty) {
            this.brushStrokePreviewsDirty = false;
            previewCtx.clearRect(
                0,
                0,
                previewCanvas.width,
                previewCanvas.height,
            );

            for (const preview of this.brushStrokePreviews.values()) {
                if (preview.kind === "brushPreview")
                    drawBrushStroke(previewCtx, preview.brush, preview.points);
                else if (preview.kind === "shapePreview")
                    drawShape(
                        previewCtx,
                        preview.shapeType,
                        preview.brush,
                        preview.points,
                    );
            }

            if (painting) {
                if (["brush", "pen", "eraser"].includes(currentTool))
                    drawBrushStroke(previewCtx, currentBrush(), strokePoints);
                else if (["rect", "ellipse", "line"].includes(currentTool))
                    drawShape(
                        previewCtx,
                        currentTool,
                        currentBrush(),
                        strokePoints,
                    );
            }

            if (moveState === "selecting" && strokePoints.length > 0) {
                previewCtx.lineWidth = 1;
                previewCtx.setLineDash([4, 4]);
                previewCtx.strokeStyle = "black";
                previewCtx.beginPath();
                previewCtx.moveTo(strokePoints[0].x, strokePoints[0].y);
                for (let p of strokePoints) previewCtx.lineTo(p.x, p.y);
                previewCtx.stroke();
                previewCtx.setLineDash([]);
            } else if (moveState === "selected" || moveState === "moving") {
                previewCtx.save();
                previewCtx.beginPath();
                previewCtx.moveTo(strokePoints[0].x, strokePoints[0].y);
                for (let i = 1; i < strokePoints.length; i++)
                    previewCtx.lineTo(strokePoints[i].x, strokePoints[i].y);
                previewCtx.closePath();
                previewCtx.fillStyle = "white";
                previewCtx.fill();
                previewCtx.restore();

                if (moveOffscreenCanvas)
                    previewCtx.drawImage(
                        moveOffscreenCanvas,
                        moveSelectionRect.dx,
                        moveSelectionRect.dy,
                    );

                previewCtx.lineWidth = 1;
                previewCtx.setLineDash([4, 4]);
                previewCtx.strokeStyle = "black";
                previewCtx.strokeRect(
                    moveSelectionRect.x + moveSelectionRect.dx,
                    moveSelectionRect.y + moveSelectionRect.dy,
                    moveSelectionRect.w,
                    moveSelectionRect.h,
                );
                previewCtx.setLineDash([]);
            }
        }
    },
    eventRecieved(globalId, event) {
        if (
            !this.global.some((e) => e.globalId === globalId) &&
            !this.globalPending.some((e) => e.globalId === globalId)
        ) {
            this.globalPending.push({ globalId: globalId, event: event });
        }
    },
    brushPreviewRecieved(payload) {
        if (payload.connection == connection.id) return;
        this.brushStrokePreviews.set(
            this.key(payload.author, payload.local),
            payload,
        );
        this.brushStrokePreviewsDirty = true;
    },
    async commitEvent(e) {
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);
        this.brushStrokePreviewsDirty = true;
        await drawEvent(ctx, e);

        this.undoStack.length = this.undoStackIndex + 1;
        this.undoStack.push(e);
        this.undoStackIndex++;
    },
    commitSetVisible(targetEvent, visible) {
        const e = {
            author: sessionClientId,
            local: this.newLocalId(),
            targetAuthor: targetEvent.author,
            targetLocal: targetEvent.local,
            kind: "visible",
            visible: visible,
        };
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);

        const targetKey = this.key(targetEvent.author, targetEvent.local);
        this.redrawFromCheckpoint(
            this.localIdToGlobalId.get(targetKey) ?? Number.MAX_SAFE_INTEGER,
        );
    },
    undo() {
        if (this.undoStackIndex >= 0) {
            this.commitSetVisible(this.undoStack[this.undoStackIndex], false);
            this.undoStackIndex--;
        }
    },
    redo() {
        if (this.undoStackIndex < this.undoStack.length - 1) {
            this.undoStackIndex++;
            this.commitSetVisible(this.undoStack[this.undoStackIndex], true);
        }
    },
};

window.events = events;

async function initApp() {
    await cache.init();
    initTools();
    initCanvas();
    initPallette();
    initChat();
    connection.connect();
}

function clearLassoSelection() {
    moveState = "idle";
    moveSelectionRect = null;
    moveOffscreenCanvas = null;
    events.brushStrokePreviewsDirty = true;
}

function initTools() {
    const tools = document.querySelectorAll(".tool");
    tools.forEach((tool) => {
        tool.addEventListener("click", () => {
            if (!tool.id) return;
            clearLassoSelection();
            tools.forEach((t) => t.classList.remove("active"));
            tool.classList.add("active");
            currentTool = tool.id;
        });
    });
}

const zoomLevels = [0.125, 0.25, 0.5, 1, 2, 4, 8];
let zoomIndex = zoomLevels.indexOf(1);

window.zoomCanvas = function (direction) {
    zoomIndex =
        direction > 0
            ? Math.min(zoomLevels.length - 1, zoomIndex + 1)
            : Math.max(0, zoomIndex - 1);
    const scale = zoomLevels[zoomIndex];
    previewCanvas.style.width = canvas.style.width =
        canvas.width * scale + "px";
    previewCanvas.style.height = canvas.style.height =
        canvas.height * scale + "px";
};

window.saveImage = function () {
    const link = document.createElement("a");
    link.download = "canvas.png";
    link.href = canvas.toDataURL("image/png");
    link.click();
};

function handlePanEvent(e) {
    if (currentTool !== "hand") return false;
    if (e.type === "mousedown" || e.type === "touchstart") {
        panState.active = true;
        const client = getClientCoords(e);
        panState.startX = client.x;
        panState.startY = client.y;
        panState.scrollX = container.scrollLeft;
        panState.scrollY = container.scrollTop;
        return true;
    } else if (
        (e.type === "mousemove" || e.type === "touchmove") &&
        panState.active
    ) {
        const client = getClientCoords(e);
        container.scrollLeft = panState.scrollX - (client.x - panState.startX);
        container.scrollTop = panState.scrollY - (client.y - panState.startY);
        return true;
    } else if (e.type === "mouseup" || e.type === "touchend") {
        panState.active = false;
        return true;
    }
    return false;
}

const pushEvent = (e) => {
    if (!handlePanEvent(e)) inputEventQueue.push(e);
};

function initCanvas() {
    previewCanvas.width = canvas.width = 1024;
    previewCanvas.height = canvas.height = 1024;
    ctx.fillStyle = "white";
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    canvas.addEventListener("contextmenu", (e) => e.preventDefault());

    canvas.addEventListener("mousedown", pushEvent);
    canvas.addEventListener(
        "touchstart",
        (e) => {
            e.preventDefault();
            pushEvent(e);
        },
        { passive: false },
    );
    window.addEventListener("mouseup", pushEvent);
    window.addEventListener("touchend", pushEvent);
    window.addEventListener("mousemove", pushEvent);
    window.addEventListener("touchmove", pushEvent);
    window.addEventListener("keydown", (e) => {
        inputEventQueue.push(e);
    });
    window.addEventListener("dragstart", (e) => {
        e.preventDefault();
    });

    container.addEventListener(
        "wheel",
        (e) => {
            if (e.ctrlKey) {
                e.preventDefault();
                window.zoomCanvas(e.deltaY < 0 ? 1 : -1);
            }
        },
        { passive: false },
    );

    requestAnimationFrame(animationFrame);
}

function getFloodFillEventPayload(startX, startY, fillColor) {
    const width = canvas.width;
    const height = canvas.height;
    if (startX < 0 || startX >= width || startY < 0 || startY >= height)
        return null;

    const imageData = ctx.getImageData(0, 0, width, height);
    const data = imageData.data;
    let hex = fillColor.replace("#", "");
    if (hex.length === 3)
        hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
    const targetR = parseInt(hex.substring(0, 2), 16);
    const targetG = parseInt(hex.substring(2, 4), 16);
    const targetB = parseInt(hex.substring(4, 6), 16);
    const startPos = (startY * width + startX) * 4;
    const startR = data[startPos];
    const startG = data[startPos + 1];
    const startB = data[startPos + 2];

    const tolerance = 80;
    if (
        Math.abs(targetR - startR) +
            Math.abs(targetG - startG) +
            Math.abs(targetB - startB) <=
        tolerance
    )
        return null;

    const tempCanvas = document.createElement("canvas");
    tempCanvas.width = width;
    tempCanvas.height = height;
    const tempCtx = tempCanvas.getContext("2d");
    const tempImageData = tempCtx.createImageData(width, height);
    const tempData = tempImageData.data;

    const queue = new Int32Array(width * height * 2);
    let head = 0;
    let tail = 0;
    queue[tail++] = startX;
    queue[tail++] = startY;
    const visited = new Uint8Array(width * height);
    visited[startY * width + startX] = 1;

    let minX = startX,
        maxX = startX,
        minY = startY,
        maxY = startY;
    let filledPixels = 0;

    while (head < tail) {
        const cx = queue[head++];
        const cy = queue[head++];
        const idx = (cy * width + cx) * 4;
        const vIdx = cy * width + cx;

        if (
            Math.abs(data[idx] - startR) +
                Math.abs(data[idx + 1] - startG) +
                Math.abs(data[idx + 2] - startB) <=
            tolerance
        ) {
            tempData[idx] = targetR;
            tempData[idx + 1] = targetG;
            tempData[idx + 2] = targetB;
            tempData[idx + 3] = 255;
            if (cx < minX) minX = cx;
            if (cx > maxX) maxX = cx;
            if (cy < minY) minY = cy;
            if (cy > maxY) maxY = cy;
            filledPixels++;

            if (cx > 0 && visited[vIdx - 1] === 0) {
                queue[tail++] = cx - 1;
                queue[tail++] = cy;
                visited[vIdx - 1] = 1;
            }
            if (cx < width - 1 && visited[vIdx + 1] === 0) {
                queue[tail++] = cx + 1;
                queue[tail++] = cy;
                visited[vIdx + 1] = 1;
            }
            if (cy > 0 && visited[vIdx - width] === 0) {
                queue[tail++] = cx;
                queue[tail++] = cy - 1;
                visited[vIdx - width] = 1;
            }
            if (cy < height - 1 && visited[vIdx + width] === 0) {
                queue[tail++] = cx;
                queue[tail++] = cy + 1;
                visited[vIdx + width] = 1;
            }
        }
    }

    if (filledPixels === 0) return null;
    tempCtx.putImageData(tempImageData, 0, 0);

    const cropWidth = maxX - minX + 1;
    const cropHeight = maxY - minY + 1;
    const cropCanvas = document.createElement("canvas");
    cropCanvas.width = cropWidth;
    cropCanvas.height = cropHeight;
    cropCanvas
        .getContext("2d")
        .drawImage(
            tempCanvas,
            minX,
            minY,
            cropWidth,
            cropHeight,
            0,
            0,
            cropWidth,
            cropHeight,
        );

    return { dataUrl: cropCanvas.toDataURL("image/png"), x: minX, y: minY };
}

async function animationFrame() {
    for (const e of inputEventQueue) {
        switch (e.type) {
            case "mousedown":
            case "touchstart": {
                if (!connection.connected) {
                    showNewMessage(
                        "**System**: Cannot draw while offline.",
                        true,
                    );
                    break;
                }

                strokeButton = e.button || 0;
                const coords = getCanvasCoords(e);
                if (!coords) break;

                if (currentTool === "bucket") {
                    const color =
                        strokeButton === 2
                            ? currentSecondaryColor
                            : currentMainColor;
                    const fillPayload = getFloodFillEventPayload(
                        coords.x,
                        coords.y,
                        color,
                    );
                    if (fillPayload) {
                        events.commitEvent({
                            author: sessionClientId,
                            local: events.newLocalId(),
                            kind: "stamp",
                            image: fillPayload.dataUrl,
                            x: fillPayload.x,
                            y: fillPayload.y,
                        });
                    }
                } else if (["brush", "pen", "eraser"].includes(currentTool)) {
                    painting = true;
                    strokePoints = [];
                    strokeLocalId = events.newLocalId();
                    handleDrawingMove(e);
                } else if (["rect", "ellipse", "line"].includes(currentTool)) {
                    painting = true;
                    strokePoints = [coords, coords];
                    strokeLocalId = events.newLocalId();
                } else if (currentTool === "lasso") {
                    if (moveState === "idle") {
                        moveState = "selecting";
                        strokePoints = [coords];
                    } else if (moveState === "selected") {
                        if (isInsideSelection(coords, moveSelectionRect)) {
                            moveState = "moving";
                            moveStartPos = coords;
                        } else {
                            clearLassoSelection();
                            moveState = "selecting";
                            strokePoints = [coords];
                        }
                    }
                }
                break;
            }
            case "mouseup":
            case "touchend": {
                if (painting) {
                    painting = false;
                    if (["rect", "ellipse", "line"].includes(currentTool)) {
                        events.commitEvent({
                            author: sessionClientId,
                            local: strokeLocalId,
                            kind: "shape",
                            shapeType: currentTool,
                            brush: currentBrush(),
                            points: strokePoints,
                        });
                    } else {
                        events.commitEvent({
                            author: sessionClientId,
                            local: strokeLocalId,
                            kind: "brush",
                            brush: currentBrush(),
                            points: strokePoints,
                        });
                    }
                } else if (currentTool === "lasso") {
                    if (moveState === "selecting") {
                        moveState = "selected";
                        let minX = Infinity,
                            minY = Infinity,
                            maxX = -Infinity,
                            maxY = -Infinity;
                        for (let p of strokePoints) {
                            minX = Math.min(minX, p.x);
                            minY = Math.min(minY, p.y);
                            maxX = Math.max(maxX, p.x);
                            maxY = Math.max(maxY, p.y);
                        }
                        moveSelectionRect = {
                            x: minX,
                            y: minY,
                            w: maxX - minX,
                            h: maxY - minY,
                            dx: 0,
                            dy: 0,
                        };

                        moveOffscreenCanvas = document.createElement("canvas");
                        moveOffscreenCanvas.width = canvas.width;
                        moveOffscreenCanvas.height = canvas.height;
                        const mCtx = moveOffscreenCanvas.getContext("2d");
                        mCtx.beginPath();
                        mCtx.moveTo(strokePoints[0].x, strokePoints[0].y);
                        for (let i = 1; i < strokePoints.length; i++)
                            mCtx.lineTo(strokePoints[i].x, strokePoints[i].y);
                        mCtx.closePath();
                        mCtx.clip();
                        mCtx.drawImage(canvas, 0, 0);
                        events.brushStrokePreviewsDirty = true;
                    } else if (moveState === "moving") {
                        const maskCanvas = document.createElement("canvas");
                        maskCanvas.width = canvas.width;
                        maskCanvas.height = canvas.height;
                        const mCtx = maskCanvas.getContext("2d");

                        mCtx.beginPath();
                        mCtx.moveTo(strokePoints[0].x, strokePoints[0].y);
                        for (let i = 1; i < strokePoints.length; i++)
                            mCtx.lineTo(strokePoints[i].x, strokePoints[i].y);
                        mCtx.closePath();
                        mCtx.clip();
                        mCtx.drawImage(canvas, 0, 0);

                        events.commitEvent({
                            author: sessionClientId,
                            local: events.newLocalId(),
                            kind: "lasso_stamp",
                            points: strokePoints,
                            image: maskCanvas.toDataURL("image/png"),
                            dx: moveSelectionRect.dx,
                            dy: moveSelectionRect.dy,
                        });
                        clearLassoSelection();
                    }
                }
                break;
            }
            case "mousemove":
            case "touchmove": {
                const coords = getCanvasCoords(e);
                if (!coords) break;

                if (painting) {
                    if (["rect", "ellipse", "line"].includes(currentTool)) {
                        strokePoints[1] = coords;
                        events.brushStrokePreviewsDirty = true;
                        connection.trySendBroadcast({
                            kind: "shapePreview",
                            connection: connection.id,
                            author: sessionClientId,
                            local: strokeLocalId,
                            shapeType: currentTool,
                            brush: currentBrush(),
                            points: strokePoints,
                        });
                    } else handleDrawingMove(e);
                } else if (currentTool === "lasso") {
                    if (moveState === "selecting") {
                        strokePoints.push(coords);
                        events.brushStrokePreviewsDirty = true;
                    } else if (moveState === "moving") {
                        moveSelectionRect.dx = coords.x - moveStartPos.x;
                        moveSelectionRect.dy = coords.y - moveStartPos.y;
                        events.brushStrokePreviewsDirty = true;
                    }
                }
                break;
            }
            case "keydown": {
                if (!painting && e.ctrlKey && e.code === "KeyZ") {
                    if (e.shiftKey) events.redo();
                    else events.undo();
                }
                break;
            }
        }
    }
    inputEventQueue.length = 0;
    await events.processPendingEvents();
    requestAnimationFrame(animationFrame);
}

function isInsideSelection(coords, rect) {
    return (
        rect &&
        coords.x >= rect.x + rect.dx &&
        coords.x <= rect.x + rect.w + rect.dx &&
        coords.y >= rect.y + rect.dy &&
        coords.y <= rect.y + rect.h + rect.dy
    );
}

function getClientCoords(e) {
    if (e.touches && e.touches.length > 0)
        return { x: e.touches[0].clientX, y: e.touches[0].clientY };
    else if (e.changedTouches && e.changedTouches.length > 0)
        return {
            x: e.changedTouches[0].clientX,
            y: e.changedTouches[0].clientY,
        };
    else return { x: e.clientX, y: e.clientY };
}

function getCanvasCoords(e) {
    const rect = canvas.getBoundingClientRect();
    const client = getClientCoords(e);
    if (!client) return null;
    return {
        x: Math.floor(((client.x - rect.left) * canvas.width) / rect.width),
        y: Math.floor(((client.y - rect.top) * canvas.height) / rect.height),
    };
}

function handleDrawingMove(e) {
    const coords = getCanvasCoords(e);
    if (!coords) return;
    const { x, y } = coords;
    if (
        strokePoints.length > 0 &&
        x == strokePoints[strokePoints.length - 1].x &&
        y == strokePoints[strokePoints.length - 1].y
    )
        return;

    let replaced = false;
    if (strokePoints.length >= 2) {
        const start = strokePoints[strokePoints.length - 2];
        const prev = strokePoints[strokePoints.length - 1];
        const ax = prev.x - start.x;
        const ay = prev.y - start.y;
        const bx = x - start.x;
        const by = y - start.y;
        if (ax * by == ay * bx) {
            if (bx * ax + by * ay > ax * ax + ay * ay) {
                replaced = true;
                strokePoints[strokePoints.length - 1] = { x, y };
            }
        }
    }
    if (!replaced) strokePoints.push({ x: x, y: y });

    events.brushStrokePreviewsDirty = true;
    connection.trySendBroadcast({
        kind: "brushPreview",
        connection: connection.id,
        author: sessionClientId,
        local: strokeLocalId,
        brush: currentBrush(),
        points: strokePoints,
    });
}

async function drawEvent(ctx, e) {
    if (e.kind === "brush") drawBrushStroke(ctx, e.brush, e.points);
    else if (e.kind === "shape") drawShape(ctx, e.shapeType, e.brush, e.points);
    else if (e.kind === "stamp") {
        const img = await loadImg(e.image);
        ctx.drawImage(img, e.x, e.y);
    } else if (e.kind === "lasso_stamp") {
        if (!e.points || e.points.length === 0) return;
        ctx.save();
        ctx.beginPath();
        ctx.moveTo(e.points[0].x, e.points[0].y);
        for (let i = 1; i < e.points.length; i++)
            ctx.lineTo(e.points[i].x, e.points[i].y);
        ctx.closePath();
        ctx.clip();
        ctx.fillStyle = "white";
        ctx.fill();
        ctx.restore();
        const img = await loadImg(e.image);
        ctx.drawImage(img, e.dx, e.dy);
    }
}

function drawBrushStroke(ctx, brush, points) {
    if (!points || points.length === 0) return;
    ctx.lineWidth = brush.width;
    ctx.strokeStyle = brush.color;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.imageSmoothingEnabled = brush.color !== "#ffffff";
    ctx.beginPath();
    ctx.moveTo(points[0].x + 0.5, points[0].y + 0.5);
    for (const { x, y } of points) ctx.lineTo(x + 0.5, y + 0.5);
    ctx.stroke();
}

function drawShape(ctx, shapeType, brush, points) {
    if (!points || points.length < 2) return;
    const start = points[0];
    const end = points[1];
    ctx.lineWidth = brush.width;
    ctx.strokeStyle = brush.color;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.beginPath();
    if (shapeType === "rect") {
        ctx.rect(start.x, start.y, end.x - start.x, end.y - start.y);
    } else if (shapeType === "ellipse") {
        const cx = (start.x + end.x) / 2;
        const cy = (start.y + end.y) / 2;
        ctx.ellipse(
            cx,
            cy,
            Math.abs(end.x - start.x) / 2,
            Math.abs(end.y - start.y) / 2,
            0,
            0,
            2 * Math.PI,
        );
    } else if (shapeType === "line") {
        ctx.moveTo(start.x, start.y);
        ctx.lineTo(end.x, end.y);
    }
    ctx.stroke();
}

function initPallette() {
    [
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
    ].forEach((color) => {
        const cb = document.createElement("div");
        cb.className = "palette-color";
        cb.style.backgroundColor = color;
        cb.onclick = () => {
            currentMainColor = color;
            mainColorDisplay.style.backgroundColor = color;
        };
        palette.appendChild(cb);
    });
}

function initChat() {
    chatInput.addEventListener("keypress", (e) => {
        if (e.key === "Enter") sendMessage();
    });
}

function toggleMainColor() {
    let temp = currentMainColor;
    currentMainColor = currentSecondaryColor;
    currentSecondaryColor = temp;
    mainColorDisplay.style.backgroundColor = currentMainColor;
    secondaryColorDisplay.style.backgroundColor = currentSecondaryColor;
}
window.toggleMainColor = toggleMainColor;

function toggleChat() {
    const chatBody = document.getElementById("chatBody");
    const chatHeader = document.getElementById("chatHeader");
    chatBody.classList.toggle("open");

    chatHeader.classList.remove("unread");
    chatHeader.querySelector(".indicator").textContent =
        chatBody.classList.contains("open") ? "▼" : "▲";
}
window.toggleChat = toggleChat;

function showNewMessage(text, important = false) {
    const msg = document.createElement("div");
    msg.classList.add("message");
    if (important) msg.classList.add("important");
    msg.textContent = text;
    chatMessages.appendChild(msg);
    chatMessages.scrollTop = chatMessages.scrollHeight;

    const chatBody = document.getElementById("chatBody");
    const chatHeader = document.getElementById("chatHeader");

    if (important && !chatBody.classList.contains("open")) toggleChat();
    else if (!chatBody.classList.contains("open"))
        chatHeader.classList.add("unread");
}

function sendMessage() {
    if (chatInput.value.trim() !== "") {
        connection.sendChatMessage(chatInput.value);
        chatInput.value = "";
    }
}
window.sendMessage = sendMessage;

await initApp();
