import { createAccel } from "/accel.js";
import { createGestures } from "/gestures.js";
import { MODIFIERS, SENTINEL, VK, charToKey, createEditTracker, namedKeyToVk } from "/keyboard.js";
import { isLayout, mapPoint, nextLayout } from "/layout.js";
import { createLatencyStats, formatStatus } from "/latency.js";
import { packButton, packKey, packMove, packPing, packScroll, packText, packZoom, parsePong } from "/protocol.js";

const EDGE = 28; // ignore touches starting this close to the left/right edge
const MAX_BUFFERED = 4096; // back-pressure threshold, bytes
const INT16 = 32767;
const SCROLL_UNITS_PER_PX = 2.5; // wheel units (120 = one notch) per finger pixel at speed 1
const ZOOM_UNITS_PER_PX = 2;

const $ = (id) => document.getElementById(id);
const pad = $("pad");
const dot = $("dot");
const stateEl = $("state");
const bar = $("bar");
const appEl = $("app");

// ---------- settings ----------
const settings = {
  sensitivity: 1.6,
  accel: 0.5, // acceleration strength 0..1 (0 = linear)
  scrollSpeed: 1,
  natural: true,
  tapToClick: true,
  haptics: true,
  leftHanded: false,
  layout: "normal", // normal | wide-left | wide-right (see layout.js)
  showLatency: true,
};

function loadSettings() {
  try {
    Object.assign(settings, JSON.parse(localStorage.getItem("settings") || "{}"));
  } catch {
    /* storage unavailable or corrupt: use defaults */
  }
}

function saveSettings() {
  try {
    localStorage.setItem("settings", JSON.stringify(settings));
  } catch {
    /* ignore */
  }
}

function bindSettings() {
  for (const key of Object.keys(settings)) {
    const el = $("set-" + key);
    if (!el) continue;
    const isCheck = el.type === "checkbox";
    const isText = el.tagName === "SELECT";
    if (isCheck) el.checked = settings[key];
    else el.value = settings[key];
    const onChange = () => {
      settings[key] = isCheck ? el.checked : isText ? el.value : parseFloat(el.value);
      saveSettings();
      applySettings();
      renderStatus();
    };
    el.addEventListener("input", onChange);
    el.addEventListener("change", onChange); // <select> fires change in every browser
  }
  applySettings();
}

let appliedLayout = "normal";

function applySettings() {
  bar.classList.toggle("left-handed", settings.leftHanded);
  if (!isLayout(settings.layout)) settings.layout = "normal"; // corrupt saved value
  if (settings.layout !== appliedLayout) {
    // The axes just changed under any finger that is down: drop the half-finished gesture
    // so its next move can't be measured across two coordinate systems.
    appliedLayout = settings.layout;
    gestures.reset();
    accel.reset();
    for (const l of ["wide-left", "wide-right"]) appEl.classList.toggle(l, l === appliedLayout);
    const sel = $("set-layout");
    if (sel.value !== appliedLayout) sel.value = appliedLayout;
  }
}

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

const latency = createLatencyStats(100);
let baseState = "connecting…";

function renderStatus() {
  stateEl.textContent = formatStatus(baseState, latency, settings.showLatency && authed);
}

function setState(text, on) {
  baseState = text;
  dot.classList.toggle("on", !!on);
  renderStatus();
}

// Round-trip time: one ping per second; the agent echoes the token straight back.
const pingSent = new Map(); // token -> performance.now() when sent
let pingTimer = 0;
let pingToken = 0;

const LIVENESS_MS = 2500; // no reply from the agent for this long means the link is dead
let lastRx = 0;
let reconnectTimer = 0;
let dropSocket = () => {}; // closes the current socket and runs its cleanup right away

function startPinging() {
  stopPinging();
  const ping = () => {
    if (!authed || ws.readyState !== 1) return;
    // A silent Wi-Fi drop can leave the socket "open" for minutes. Don't wait for TCP:
    // if the agent hasn't answered recently, tear it down and reconnect.
    if (performance.now() - lastRx > LIVENESS_MS) {
      dropSocket();
      return;
    }
    const pingId = pingToken++;
    pingSent.set(pingId, performance.now());
    if (pingSent.size > 8) pingSent.delete(pingSent.keys().next().value); // lost pongs
    ws.send(packPing(pingId));
  };
  ping();
  pingTimer = setInterval(ping, 1000);
}

function stopPinging() {
  clearInterval(pingTimer);
  pingTimer = 0;
  pingSent.clear();
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, Math.min(250 * 2 ** retry++, 2000));
}

function connect() {
  clearTimeout(reconnectTimer);
  if (fatal) return;
  if (ws && (ws.readyState === 0 || ws.readyState === 1)) return; // already connecting/connected
  if (!token) {
    fatal = true;
    setState("Not paired: scan the QR code shown on the laptop", false);
    return;
  }
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const sock = new WebSocket(`${proto}://${location.host}/ws`);
  ws = sock;
  sock.binaryType = "arraybuffer";
  authed = false;
  setState("connecting…", false);

  let closed = false;
  const finish = () => {
    if (closed) return;
    closed = true;
    if (ws === sock) {
      authed = false;
      stopPinging();
      latency.clear();
      moveAcc.x = moveAcc.y = scrollAcc.x = scrollAcc.y = zoomAcc.x = 0;
      gestures.reset();
      releaseBarButtons();
      dropMods();
    }
    if (fatal || ws !== sock) return;
    setState("reconnecting…", false);
    scheduleReconnect();
  };
  dropSocket = () => {
    try {
      sock.close();
    } catch {
      /* already closed */
    }
    finish(); // don't wait for the browser to notice the dead connection
  };

  sock.onopen = () => sock.send(JSON.stringify({ t: "auth", token, v: 1 }));
  sock.onmessage = (ev) => {
    if (ws !== sock) return;
    lastRx = performance.now();
    if (typeof ev.data !== "string") {
      const pongId = parsePong(ev.data);
      const t0 = pongId === null ? undefined : pingSent.get(pongId);
      if (t0 !== undefined) {
        pingSent.delete(pongId);
        latency.add(performance.now() - t0);
        renderStatus();
      }
      return;
    }
    const msg = JSON.parse(ev.data);
    if (msg.t === "ok") {
      authed = true;
      retry = 0;
      latency.clear();
      setState("connected", true);
      startPinging();
      acquireWakeLock();
    } else if (msg.t === "err") {
      if (msg.reason === "bad_token" || msg.reason === "bad_version") {
        fatal = true;
        setState("Pairing failed: scan the QR code again (python -m agent --pair)", false);
      }
    }
  };
  sock.onclose = finish;
}

// Coming back to the app (unlock, app switch): timers were throttled, so check the link now.
document.addEventListener("visibilitychange", () => {
  if (document.hidden) return;
  if (authed && performance.now() - lastRx > LIVENESS_MS) dropSocket();
  if (!authed && !fatal) {
    retry = 0;
    connect();
  }
  acquireWakeLock();
});

// ---------- keep the screen on ----------
let wakeLock = null;

function showToast(text, ms = 7000) {
  const el = $("toast");
  el.textContent = text;
  el.hidden = false;
  setTimeout(() => {
    el.hidden = true;
  }, ms);
}

/** Screen Wake Lock needs HTTPS. Without it, tell the user once how to avoid the screen sleeping. */
async function acquireWakeLock() {
  if (!authed || wakeLock) return;
  try {
    if (typeof isSecureContext !== "undefined" && isSecureContext && navigator.wakeLock) {
      wakeLock = await navigator.wakeLock.request("screen");
      wakeLock.addEventListener("release", () => {
        wakeLock = null;
      });
      return;
    }
  } catch {
    /* denied, e.g. low battery: fall through to the hint */
  }
  try {
    if (localStorage.getItem("wakeHintShown")) return;
    localStorage.setItem("wakeHintShown", "1");
  } catch {
    /* storage unavailable: show the hint every time */
  }
  showToast("The screen may turn off while idle. Raise the screen timeout, or start the agent with --https.");
}

// ---------- outgoing streams ----------
// Move, scroll and zoom accumulate here (fractions carry over) and are pumped to the
// socket. If the socket's send buffer is backed up we keep accumulating and send the
// sum once it drains, so lag can't build up.
function makeAccum(pack) {
  return { x: 0, y: 0, pack, timer: 0 };
}
const moveAcc = makeAccum(packMove);
const scrollAcc = makeAccum(packScroll);
const zoomAcc = makeAccum((x) => packZoom(x));

function clamp16(v) {
  return v > INT16 ? INT16 : v < -INT16 ? -INT16 : v;
}

function pump(a) {
  if (!authed || ws.readyState !== 1) return;
  if (ws.bufferedAmount > MAX_BUFFERED) {
    if (!a.timer) {
      a.timer = setTimeout(() => {
        a.timer = 0;
        pump(a);
      }, 4);
    }
    return;
  }
  let ix = Math.trunc(a.x);
  let iy = Math.trunc(a.y);
  while (ix !== 0 || iy !== 0) {
    const sx = clamp16(ix);
    const sy = clamp16(iy);
    ws.send(a.pack(sx, sy));
    ix -= sx;
    iy -= sy;
    a.x -= sx;
    a.y -= sy;
  }
}

function pumpAll() {
  pump(moveAcc);
  pump(scrollAcc);
  pump(zoomAcc);
}

/** Reliable, ordered messages (buttons, keys). Pending movement goes out first. */
function sendReliable(buf) {
  if (!authed || ws.readyState !== 1) return;
  pumpAll();
  ws.send(buf);
}

// ---------- buttons held on the bar ----------
const barHeld = new Map(); // button index -> element

function barPress(b, el) {
  if (barHeld.has(b)) return;
  barHeld.set(b, el);
  el.classList.add("down");
  sendReliable(packButton(b, 1));
}

function barRelease(b) {
  const el = barHeld.get(b);
  if (!el) return;
  barHeld.delete(b);
  el.classList.remove("down");
  sendReliable(packButton(b, 0));
}

function releaseBarButtons() {
  for (const [, el] of barHeld) el.classList.remove("down");
  barHeld.clear(); // the agent releases held buttons itself when the socket closes
}

function wireBarButton(id, b) {
  const el = $(id);
  el.addEventListener("pointerdown", (e) => {
    e.preventDefault();
    el.setPointerCapture(e.pointerId);
    barPress(b, el);
  });
  const up = () => barRelease(b);
  el.addEventListener("pointerup", up);
  el.addEventListener("pointercancel", up);
  el.addEventListener("contextmenu", (e) => e.preventDefault());
}
wireBarButton("btn-left", 0);
wireBarButton("btn-right", 1);

// ---------- gestures ----------
function haptic() {
  if (settings.haptics && navigator.vibrate) navigator.vibrate(8);
}

const out = {
  move(dx, dy, t) {
    if (!authed) return; // offline: drop movement so the cursor can't jump on reconnect
    const k = settings.sensitivity * accel.gain(dx, dy, t);
    moveAcc.x += dx * k;
    moveAcc.y += dy * k;
  },
  scroll(dx, dy) {
    if (!authed) return;
    const k = SCROLL_UNITS_PER_PX * settings.scrollSpeed;
    // Natural: content follows the fingers. Windows wheel: + is up / right.
    scrollAcc.x += settings.natural ? -dx * k : dx * k;
    scrollAcc.y += settings.natural ? dy * k : -dy * k;
  },
  zoom(d) {
    if (!authed) return;
    zoomAcc.x += d * ZOOM_UNITS_PER_PX; // fingers apart = positive = zoom in
  },
  button(b, down) {
    sendReliable(packButton(b, down ? 1 : 0));
  },
  click(b) {
    if (!authed) return; // no haptic tick for a click that won't reach the laptop
    out.button(b, true);
    out.button(b, false);
    haptic();
  },
  chord(vks) {
    for (let i = 0; i < vks.length; i++) sendReliable(packKey(vks[i], 1));
    for (let i = vks.length - 1; i >= 0; i--) sendReliable(packKey(vks[i], 0));
  },
};

const accel = createAccel(() => settings.accel);
const gestures = createGestures(out, () => settings);

// Settings are loaded here, after accel and gestures exist, because applying a saved
// layout resets them.
loadSettings();
bindSettings();

// ---------- touch input (hot path: no logging, no DOM writes) ----------
function inEdge(e) {
  return e.clientX < EDGE || e.clientX > innerWidth - EDGE;
}

/** Screen point -> the (possibly rotated) pad's own coordinates. */
function pt(x, y) {
  return mapPoint(settings.layout, x, y, innerWidth, innerHeight);
}

pad.addEventListener("pointerdown", (e) => {
  if (inEdge(e)) return;
  pad.setPointerCapture(e.pointerId);
  accel.reset();
  const p = pt(e.clientX, e.clientY);
  gestures.down(e.pointerId, p.x, p.y, e.timeStamp);
});

pad.addEventListener("pointermove", (e) => {
  const evs = e.getCoalescedEvents ? e.getCoalescedEvents() : null;
  if (evs && evs.length) {
    for (let i = 0; i < evs.length; i++) {
      const p = pt(evs[i].clientX, evs[i].clientY);
      gestures.move(e.pointerId, p.x, p.y, evs[i].timeStamp);
    }
  } else {
    const p = pt(e.clientX, e.clientY);
    gestures.move(e.pointerId, p.x, p.y, e.timeStamp);
  }
  pumpAll();
});

pad.addEventListener("pointerup", (e) => {
  accel.reset();
  gestures.up(e.pointerId, e.timeStamp);
  pumpAll();
});
pad.addEventListener("pointercancel", (e) => gestures.cancel(e.pointerId));
pad.addEventListener("contextmenu", (e) => e.preventDefault());

// ---------- keyboard ----------
const kbin = $("kbin");
const keysPanel = $("keys");
const heldMods = new Map(); // sticky modifier vk -> its button element

function key(vk, down) {
  sendReliable(packKey(vk, down ? 1 : 0));
}

/** Release all latched modifiers (they apply to one key, like Android sticky keys). */
function releaseMods() {
  for (const [vk, el] of heldMods) {
    key(vk, false);
    el.classList.remove("down");
  }
  heldMods.clear();
}

/** Forget latched modifiers without sending anything (the agent releases them on disconnect). */
function dropMods() {
  for (const [, el] of heldMods) el.classList.remove("down");
  heldMods.clear();
}

function toggleMod(vk, el) {
  if (heldMods.has(vk)) {
    heldMods.delete(vk);
    el.classList.remove("down");
    key(vk, false);
  } else {
    heldMods.set(vk, el);
    el.classList.add("down");
    key(vk, true);
  }
}

function tapKey(vk, withShift = false) {
  const addShift = withShift && !heldMods.has(VK.SHIFT);
  if (addShift) key(VK.SHIFT, true);
  key(vk, true);
  key(vk, false);
  if (addShift) key(VK.SHIFT, false);
  releaseMods();
}

function typeText(text) {
  const chars = Array.from(text);
  let i = 0;
  // While a modifier is latched, letters and digits go out as real key presses so
  // Ctrl+C, Alt+F4 and similar shortcuts work; Unicode injection would not trigger them.
  while (i < chars.length && heldMods.size) {
    const ch = chars[i++];
    const k = charToKey(ch);
    if (k) {
      tapKey(k.vk, k.shift);
    } else {
      for (const buf of packText(ch)) sendReliable(buf);
      releaseMods();
    }
  }
  if (i < chars.length) {
    for (const buf of packText(chars.slice(i).join(""))) sendReliable(buf);
  }
}

const tracker = createEditTracker();
let composing = false;

function resetInput() {
  kbin.value = SENTINEL;
  kbin.setSelectionRange(SENTINEL.length, SENTINEL.length);
  tracker.reset();
}

function processInput(e) {
  const r = tracker.update(kbin.value, composing || !!(e && e.isComposing));
  for (let i = 0; i < r.backspaces; i++) tapKey(VK.BACK);
  if (r.text) typeText(r.text);
  if (r.reset) resetInput();
}

kbin.addEventListener("compositionstart", () => {
  composing = true;
});
kbin.addEventListener("compositionend", () => {
  composing = false;
  processInput();
});
kbin.addEventListener("input", processInput);
kbin.addEventListener("beforeinput", (e) => {
  if (e.inputType === "insertLineBreak") {
    e.preventDefault();
    tapKey(VK.ENTER);
  }
});
kbin.addEventListener("keydown", (e) => {
  if (e.isComposing || e.keyCode === 229) return; // soft keyboard: handled via input events
  const vk = namedKeyToVk(e.key);
  if (vk !== null) {
    e.preventDefault();
    tapKey(vk);
  }
});

function wireKey(el, vk, repeat) {
  let delay = 0;
  let timer = 0;
  const stop = () => {
    clearTimeout(delay);
    clearInterval(timer);
    delay = timer = 0;
  };
  el.addEventListener("pointerdown", (e) => {
    e.preventDefault(); // keep focus (and the soft keyboard) where it is
    el.setPointerCapture(e.pointerId);
    tapKey(vk);
    if (repeat) {
      delay = setTimeout(() => {
        timer = setInterval(() => tapKey(vk), 45);
      }, 400);
    }
  });
  el.addEventListener("pointerup", stop);
  el.addEventListener("pointercancel", stop);
}

for (const el of keysPanel.querySelectorAll("button[data-vk]")) {
  wireKey(el, parseInt(el.dataset.vk, 10), "repeat" in el.dataset);
}
for (const el of keysPanel.querySelectorAll("button[data-mod]")) {
  const vk = parseInt(el.dataset.mod, 10);
  if (!MODIFIERS.includes(vk)) continue;
  el.addEventListener("pointerdown", (e) => {
    e.preventDefault();
    toggleMod(vk, el);
  });
}
const fkeys = $("fkeys");
for (let i = 0; i < 12; i++) {
  const b = document.createElement("button");
  b.type = "button";
  b.textContent = "F" + (i + 1);
  wireKey(b, VK.F1 + i, false);
  fkeys.appendChild(b);
}
$("btn-fkeys").addEventListener("click", () => {
  fkeys.hidden = !fkeys.hidden;
});

$("btn-kb").addEventListener("click", () => {
  if (keysPanel.hidden) {
    keysPanel.hidden = false;
    resetInput();
    kbin.focus();
  } else {
    keysPanel.hidden = true;
    kbin.blur();
  }
});

// ---------- toolbar ----------
$("btn-layout").addEventListener("click", () => {
  settings.layout = nextLayout(settings.layout);
  saveSettings();
  applySettings();
  showToast(
    settings.layout === "normal"
      ? "Layout: normal"
      : "Layout: wide. Turn the phone " + (settings.layout === "wide-left" ? "left" : "right") + ".",
    2200,
  );
});

$("btn-gear").addEventListener("click", () => {
  const panel = $("settings");
  panel.hidden = !panel.hidden;
});

$("btn-fs").addEventListener("click", async () => {
  try {
    if (document.fullscreenElement) {
      await document.exitFullscreen();
    } else {
      await document.documentElement.requestFullscreen({ navigationUI: "hide" });
      try {
        await screen.orientation.lock("landscape");
      } catch {
        /* not supported or not allowed: stay in the current orientation */
      }
    }
  } catch {
    /* fullscreen not available */
  }
});

connect();
