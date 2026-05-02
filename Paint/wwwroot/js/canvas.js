"use strict";
// Canvas DOM elements
const container = document.getElementById("canvasContainer");
const canvas = document.getElementById("paintCanvas");
const ctx = canvas.getContext("2d");

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
let strokePoints = []
let canvasBitmapBeforeStroke = null;

let inputEventQueue = []

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

    async processPendingEvents() {
        for (const {globalId, event} of this.globalPending) {
            this.global.push(event);

            if (connection.id == event.connection && (this.localDisplayed.length == 0 || event.local == this.localDisplayed[0].local)) {
                this.localDisplayed.shift();
            } else {
                // needs to roll back to latest checkpoint before local changes and continue from there
            
                // TODO: don't redraw all events by using checkpoints
                ctx.fillStyle = "white";
                ctx.fillRect(0, 0, canvas.width, canvas.height);

                for (const e of this.global) {
                    this.drawEvent(e);
                }
                for (const e of this.localDisplayed) {
                    this.drawEvent(e);
                }

                // If this event has been recieved during a stroke,
                // after the stroke completes the canvasBitmapBeforeStroke will override changes made here.
                // To prevent this render new bitmap immediately.
                if (painting) {
                    canvasBitmapBeforeStroke = await createImageBitmap(canvas)

                    const width = Math.ceil(sizeInput.value);
                    const color = currentMainColor;
                    drawBrushStroke(width, color, strokePoints); // Stroke drawn so far is also lost, redraw it

                }
            }
        }
        this.globalPending.length = 0;
    },
    eventRecieved(globalId, event) {
        if (globalId != this.recievedVersion + 1) return; // Out of order event, it could just be saved for later but whatever

        this.globalPending.push({ id: globalId, event: event });
        this.recievedVersion = globalId;
    },
    drawEvent(e) {
        if (e.kind == "brush") {
            drawBrushStroke(e.width, e.color, e.points);
        } else {
            console.error("unknown event kind");
        }
    },
    sendBrushStroke(width, color, points) {
        const id = this.nextLocalEventId++;
        const e = {
            connection: connection.id,
            local: id,
            kind: "brush",
            width: width,
            color: color,
            points: points
        }
        this.localDisplayed.push(e);
        connection.sendCanvasEvent(e);
    },
}

function initApp() {
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
    canvas.width = container.clientWidth - 40;
    canvas.height = container.clientHeight - 40;
    ctx.fillStyle = "white";
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    canvas.addEventListener("mousedown",  (e) => { inputEventQueue.push(e); });
    canvas.addEventListener("touchstart", (e) => { e.preventDefault(); inputEventQueue.push(e); });

    window.addEventListener("mouseup",  (e) => { inputEventQueue.push(e); });
    window.addEventListener("touchend", (e) => { inputEventQueue.push(e); });

    window.addEventListener("mousemove", (e) => { inputEventQueue.push(e); });
    window.addEventListener("touchmove", (e) => { inputEventQueue.push(e); });

    requestAnimationFrame(animationFrame)
}

async function animationFrame(timestamp) {
    for (const e of inputEventQueue) {
 
        switch (e.type) {
            case "mousedown":
            case "touchstart": 
            {
                canvasBitmapBeforeStroke = await createImageBitmap(canvas);

                painting = true;
                strokePoints = []; 
                draw(e);
                break;
            }
            case "mouseup":
            case "touchend":
            {
                if (painting) {
                    painting = false;
                    ctx.drawImage(canvasBitmapBeforeStroke, 0, 0);
                    drawBrushStroke(ctx.lineWidth, currentMainColor, strokePoints)
                    events.sendBrushStroke(ctx.lineWidth, currentMainColor, strokePoints);
                }
                break;
            }
            case "mousemove":
            case "touchmove":
            {
                draw(e);
                break;
            }
        }
    }
    inputEventQueue.length = 0;

    await events.processPendingEvents();

    requestAnimationFrame(animationFrame);
}



function drawBrushStroke(width, color, points) {
    console.log(width, color, points)
    ctx.lineWidth = width;
    ctx.strokeStyle = color;
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
    
    ctx.lineWidth = Math.ceil(sizeInput.value);
    ctx.strokeStyle = currentMainColor;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";

    if (strokePoints.length == 1) {
        ctx.beginPath();

        ctx.moveTo(x + 0.5, y + 0.5);
        ctx.lineTo(x + 0.5, y + 0.5);
        ctx.stroke();
    } else {
        const previous = strokePoints[strokePoints.length - 2];
        ctx.beginPath();

        ctx.moveTo(previous.x + 0.5, previous.y + 0.5);
        ctx.lineTo(x + 0.5, y + 0.5);

        ctx.stroke();
    }
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

initApp();
