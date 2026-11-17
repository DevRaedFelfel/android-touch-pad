import { createGestures } from "/gestures.js";
import { MODIFIERS, SENTINEL, VK, charToKey, createEditTracker, namedKeyToVk } from "/keyboard.js";
import { packButton, packKey, packMove, packScroll, packText, packZoom } from "/protocol.js";

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

// ---------- settings ----------
const settings = {
  sensitivity: 1.6,
  scrollSpeed: 1,
  natural: true,
  tapToClick: true,
  haptics: true,
  leftHanded: false,
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
    if (isCheck) el.checked = settings[key];
    else el.value = settings[key];
    el.addEventListener("input", () => {
      settings[key] = isCheck ? el.checked : parseFloat(el.value);
      saveSettings();
      applySettings();
    });
  }
  applySettings();
}

function applySettings() {
  bar.classList.toggle("left-handed", settings.leftHanded);
}

loadSettings();
bindSettings();

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
    moveAcc.x = moveAcc.y = scrollAcc.x = scrollAcc.y = zoomAcc.x = 0;
    gestures.reset();
    releaseBarButtons();
    dropMods();
    if (fatal) return;
    setState("reconnecting…", false);
    setTimeout(connect, Math.min(250 * 2 ** retry++, 2000));
  };
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
  move(dx, dy) {
    if (!authed) return; // offline: drop movement so the cursor can't jump on reconnect
    moveAcc.x += dx * settings.sensitivity;
    moveAcc.y += dy * settings.sensitivity;
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

const gestures = createGestures(out, () => settings);

// ---------- touch input (hot path: no logging, no DOM writes) ----------
function inEdge(e) {
  return e.clientX < EDGE || e.clientX > innerWidth - EDGE;
}

pad.addEventListener("pointerdown", (e) => {
  if (inEdge(e)) return;
  pad.setPointerCapture(e.pointerId);
  gestures.down(e.pointerId, e.clientX, e.clientY, e.timeStamp);
});

pad.addEventListener("pointermove", (e) => {
  const evs = e.getCoalescedEvents ? e.getCoalescedEvents() : null;
  if (evs && evs.length) {
    for (let i = 0; i < evs.length; i++) {
      gestures.move(e.pointerId, evs[i].clientX, evs[i].clientY, evs[i].timeStamp);
    }
  } else {
    gestures.move(e.pointerId, e.clientX, e.clientY, e.timeStamp);
  }
  pumpAll();
});

pad.addEventListener("pointerup", (e) => {
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
