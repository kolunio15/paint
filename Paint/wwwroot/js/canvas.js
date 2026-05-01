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

const connection = {
    ws: null,
    connected: false,

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
        }
        this.ws.onerror = (e) => {
            console.error("WebSocket error:", e);
       
        }
        this.ws.onmessage = (e) => {
            console.log("WebSocket recieved:", e.data);

            if (e.data.startsWith("msg ")) {
                showNewMessage(e.data.substring("msg ".length));
            }
        },
        this.ws.onclose = (e) => {
            console.info("WebSocket closed");
            if (this.connected) showNewMessage("**System**: disconnected");
            connected = false;
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

    canvas.addEventListener("mousedown", async (e) => {
        ctx.lineWidth = Math.ceil(sizeInput.value);
        ctx.lineCap = "round";
        ctx.lineJoin = "round";
        ctx.strokeStyle = currentMainColor;
        canvasBitmapBeforeStroke = await createImageBitmap(canvas);

        painting = true;
        strokePoints = []; 
        draw(e);
    });
    window.addEventListener("mouseup", () => {
        painting = false;
        ctx.drawImage(canvasBitmapBeforeStroke, 0, 0);
        ctx.beginPath();
        ctx.moveTo(strokePoints[0].x + 0.5, strokePoints[0].y + 0.5);
        for (const { x, y } of strokePoints) {
            ctx.lineTo(x + 0.5, y + 0.5);
        }
        ctx.stroke();
    });
    window.addEventListener("mousemove", draw);
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

    const x = Math.floor((e.clientX - rect.left) * canvas.width / rect.width);
    const y = Math.floor((e.clientY - rect.top) * canvas.height / rect.height);

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
