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

function initWebSocket() {
    const roomId = new URLSearchParams(window.location.search).get('roomId');

    var url = new URL('/room_ws', window.location.href);
    url.protocol = url.protocol.replace('http', 'ws');
    url.searchParams.set('roomId', roomId);
    const ws = new WebSocket(url.href);
    ws.onopen = (e) => {
        console.info('WebSocket connected');
        ws.send("hello from client");
    }
    ws.onerror = (e) => {
        console.error("WebSocket disconnected, error:", e);
    }
    ws.onmessage = (e) => {
        console.log("WebSocket recieved:", e.data);
    }
    ws.onclose = (e) => {
        console.info("WebSocket disconnected");
    }
}

function initApp() {
    initTools();
    initCanvas();
    initPallette();
    initChat();
    initWebSocket();
}

function initTools() {
    // TODO: do proper tool selection and stuff... someday
}

function initCanvas() {
    canvas.width = container.clientWidth - 40;
    canvas.height = container.clientHeight - 40;
    ctx.fillStyle = "white";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.lineCap = "square";

    canvas.addEventListener("mousedown", (e) => {
        painting = true;
        draw(e);
    });
    window.addEventListener("mouseup", () => {
        painting = false;
        ctx.beginPath();
    });
    canvas.addEventListener("mousemove", draw);
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
    const x = e.offsetX * canvas.width  / rect.width;
    const y = e.offsetY * canvas.height / rect.height;

    ctx.lineWidth = sizeInput.value;
    ctx.strokeStyle = currentMainColor;
    ctx.lineTo(x, y);
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(x, y);

    // const size = sizeInput.value;
    // ctx.fillStyle = currentMainColor;
    // ctx.fillRect(x - size / 2, y - size / 2, size, size);
}

function toggleChat() {
    const body = document.getElementById("chatBody");
    body.classList.toggle("open");
}

// dummy chat
function sendMessage() {
    if (chatInput.value.trim() !== "") {
        const msg = document.createElement("div");
        msg.classList.add("message");
        msg.textContent = "You: " + chatInput.value;
        chatMessages.appendChild(msg);
        chatMessages.scrollTop = chatMessages.scrollHeight;
        chatInput.value = "";
    }
}

initApp();
