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

let inputEventQueue = []


function currentBrush() {
    return { width: Math.ceil(sizeInput.value), color: currentMainColor };
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
    // TODO: do proper tool selection and stuff... someday
}

function initCanvas() {
    // TODO: Get size when connecting
    previewCanvas.width  = canvas.width  = 1024;
    previewCanvas.height = canvas.height = 1024;
    ctx.fillStyle = "white";
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    canvas.addEventListener("mousedown",  (e) => { inputEventQueue.push(e); });
    canvas.addEventListener("touchstart", (e) => { e.preventDefault(); inputEventQueue.push(e); });

    window.addEventListener("mouseup",  (e) => { inputEventQueue.push(e); });
    window.addEventListener("touchend", (e) => { inputEventQueue.push(e); });

    window.addEventListener("mousemove", (e) => { inputEventQueue.push(e); });
    window.addEventListener("touchmove", (e) => { inputEventQueue.push(e); });

    window.addEventListener("keydown", (e) => { inputEventQueue.push(e); });

    window.addEventListener("dragstart", (e) => { e.preventDefault(); })
    requestAnimationFrame(animationFrame)
}

async function animationFrame(timestamp) {
    for (const e of inputEventQueue) {
 
        switch (e.type) {
            case "mousedown":
            case "touchstart": 
            {
                painting = true;
                strokePoints = []; 

                strokeLocalId = events.newLocalId();

                draw(e);
                break;
            }
            case "mouseup":
            case "touchend":
            {
                if (painting) {
                    painting = false;
                    events.commitBrushStroke(strokeLocalId, currentBrush(), strokePoints);
                }
                break;
            }
            case "mousemove":
            case "touchmove":
            {
                draw(e);
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
    if (e.kind == "brush") {
        drawBrushStroke(ctx, e.brush, e.points);
    } else {
        console.error("unknown event kind");
    }
}

function drawBrushStroke(ctx, brush, points) {
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

await initApp();