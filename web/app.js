import { packMove } from "/protocol.js";

const EDGE = 28; // ignore touches starting this close to the left/right/bottom edge
const MAX_BUFFERED = 4096; // back-pressure threshold, bytes
const INT16 = 32767;

const pad = document.getElementById("pad");
const dot = document.getElementById("dot");
const stateEl = document.getElementById("state");

const settings = { sensitivity: 1.6 };

// ---------- pairing token ----------
function loadToken() {
  const url = new URL(location.href);
  const fromUrl = url.searchParams.get("t");
  try {
    if (fromUrl) {
      localStorage.setItem("token", fromUrl);
      history.replaceState(null, "", location.pathname); // keep the token out of the URL
    }
    return localStorage.getItem("token");
  } catch {
    return fromUrl; // storage blocked: works for this page load only
  }
}
const token = loadToken();

// ---------- connection ----------
let ws = null;
let authed = false;
let retry = 0;
let fatal = false;

function setState(text, on) {
  stateEl.textContent = text;
  dot.classList.toggle("on", !!on);
}

function connect() {
  if (fatal) return;
  if (!token) {
    fatal = true;
    setState("Not paired: scan the QR code shown on the laptop", false);
    return;
  }
  const proto = location.protocol === "https:" ? "wss" : "ws";
  ws = new WebSocket(`${proto}://${location.host}/ws`);
  ws.binaryType = "arraybuffer";
  authed = false;
  setState("connecting…", false);

  ws.onopen = () => ws.send(JSON.stringify({ t: "auth", token, v: 1 }));
  ws.onmessage = (ev) => {
    if (typeof ev.data !== "string") return;
    const msg = JSON.parse(ev.data);
    if (msg.t === "ok") {
      authed = true;
      retry = 0;
      setState("connected", true);
    } else if (msg.t === "err") {
      if (msg.reason === "bad_token" || msg.reason === "bad_version") {
        fatal = true;
        setState("Pairing failed: scan the QR code again (python -m agent --pair)", false);
      }
    }
  };
  ws.onclose = () => {
    authed = false;
    if (fatal) return;
    setState("reconnecting…", false);
    setTimeout(connect, Math.min(250 * 2 ** retry++, 2000));
  };
}

// ---------- movement (hot path: no allocations, no logging, no DOM) ----------
let pendX = 0;
let pendY = 0; // sensitivity-scaled movement not yet sent; fractions carry over
let drainTimer = 0;

function flush() {
  if (!authed || ws.readyState !== 1) return;
  if (ws.bufferedAmount > MAX_BUFFERED) {
    // Network is behind: keep accumulating and send the sum once it drains.
    if (!drainTimer) {
      drainTimer = setTimeout(() => {
        drainTimer = 0;
        flush();
      }, 4);
    }
    return;
  }
  let ix = Math.trunc(pendX);
  let iy = Math.trunc(pendY);
  while (ix !== 0 || iy !== 0) {
    const sx = Math.max(-INT16, Math.min(INT16, ix));
    const sy = Math.max(-INT16, Math.min(INT16, iy));
    ws.send(packMove(sx, sy));
    ix -= sx;
    iy -= sy;
    pendX -= sx;
    pendY -= sy;
  }
}

// ---------- touch input ----------
let activeId = null;
let lastX = 0;
let lastY = 0;

function inEdge(e) {
  return e.clientX < EDGE || e.clientX > innerWidth - EDGE || e.clientY > innerHeight - EDGE;
}

pad.addEventListener("pointerdown", (e) => {
  if (activeId !== null || inEdge(e)) return;
  activeId = e.pointerId;
  lastX = e.clientX;
  lastY = e.clientY;
  pad.setPointerCapture(e.pointerId);
});

pad.addEventListener("pointermove", (e) => {
  if (e.pointerId !== activeId) return;
  const evs = e.getCoalescedEvents ? e.getCoalescedEvents() : null;
  let dx = 0;
  let dy = 0;
  if (evs && evs.length) {
    for (let i = 0; i < evs.length; i++) {
      dx += evs[i].clientX - lastX;
      dy += evs[i].clientY - lastY;
      lastX = evs[i].clientX;
      lastY = evs[i].clientY;
    }
  } else {
    dx = e.clientX - lastX;
    dy = e.clientY - lastY;
    lastX = e.clientX;
    lastY = e.clientY;
  }
  pendX += dx * settings.sensitivity;
  pendY += dy * settings.sensitivity;
  flush();
});

function release(e) {
  if (e.pointerId === activeId) activeId = null;
}
pad.addEventListener("pointerup", release);
pad.addEventListener("pointercancel", release);

connect();
