# Phone Touchpad — Specification & Claude Code Build Prompt

## 0. How to use this file

1. Create an empty folder on the laptop, e.g. `C:\dev\touchpad`.
2. Save this file there as `SPEC.md`.
3. Open a terminal in that folder and run `claude`.
4. Paste the prompt from section 1.
5. When finished, follow section 12 to open the app on the phone.

---

## 1. Prompt to paste into Claude Code

```
Read SPEC.md completely before writing any code. Build the project exactly as
specified, following the milestones in section 9 in order (M1 → M5; M6 is optional
and only if I ask).

Rules:
- Target: Windows laptop (agent) + Android phone browser (client). No Android Studio,
  no Android SDK, no APK, no Node/npm, no build step, no CDN. The only installs allowed
  are the Python packages listed in SPEC.md section 4. If Python 3.11+ is missing,
  stop and tell me instead of installing it.
- Latency is the top priority. Obey every rule in sections 5, 6 and 7 that is marked
  [LATENCY]. Do not add features that put work on the hot path.
- After each milestone: run its acceptance checks (tests and the --dry-run latency
  tool where applicable), fix failures, then give me a 3-line status.
- Ask me questions only if blocked. Otherwise make the most reasonable choice and
  note it in README.md under "Decisions".
- Finish by printing: the exact commands to start the agent, and a 10-item manual
  test checklist I can run from the phone.
```

---

## 2. Goal and constraints

**Goal:** turn an Android phone into a low-latency touchpad (plus basic keyboard) for a Windows laptop, over the local network.

| Item | Decision |
|---|---|
| Laptop | Windows (Lenovo), runs a small Python **agent** |
| Phone | Samsung Galaxy S23 Ultra, Android, Chrome (120 Hz display, high touch sampling rate) |
| Phone app form | **Web app (PWA-style page)** served by the agent; opened via QR code; "Add to Home screen" shortcut. No install, no SDK |
| Link | Same Wi-Fi network (5 GHz), or the laptop's own Windows Mobile Hotspot |
| Latency target | Phone touch → cursor move: **p50 < 8 ms, p95 < 20 ms** (measured as RTT/2 plus agent processing time) |
| Out of scope v1 | Bluetooth (a browser cannot act as a Bluetooth HID mouse), lock-screen use, iOS-specific work, macOS/Linux agent |

---

## 3. Architecture

```
 Phone (Chrome)                              Windows laptop
┌──────────────────────┐   WebSocket       ┌─────────────────────────────┐
│ web/ (vanilla JS)    │   binary frames   │ agent/ (Python asyncio)     │
│  Pointer Events      │ ────────────────► │  aiohttp: HTTP + WebSocket  │
│  gesture engine      │   (same port)     │  auth + private-IP filter   │
│  tiny binary encoder │ ◄──────────────── │  accumulator + injector     │
└──────────────────────┘   PONG / status   │  ctypes → user32.SendInput  │
                                            └─────────────────────────────┘
```

- One process, one port (default **8765**): serves the static client files and the WebSocket at `/ws`.
- The client never needs internet. No external assets.

---

## 4. Technology choices

**Agent:** Python 3.11+, packages: `aiohttp`, `qrcode`, `cryptography` (only for optional HTTPS mode), `pytest`. Windows input via `ctypes` (no pywin32, no pynput).
**Client:** plain HTML + CSS + ES modules. No framework, no bundler, no service worker.

`requirements.txt` must list exactly these and nothing heavier.

---

## 5. Protocol (WebSocket)

Binary frames, **little-endian**, `ws.binaryType = "arraybuffer"`. Control messages (auth, config, errors) are JSON **text** frames. Hot-path messages are binary only. [LATENCY] No JSON, no compression (`compress=False`, no permessage-deflate) on the hot path.

### Client → agent (binary)

| Type | Name | Layout after the type byte | Notes |
|---|---|---|---|
| `0x01` | MOVE | `u16 seq, i16 dx, i16 dy` | Relative pixels. Split if \|value\| > 32767 |
| `0x02` | SCROLL | `u16 seq, i16 dx, i16 dy` | Units: 120 = one wheel notch (Windows WHEEL_DELTA). Fractions allowed |
| `0x03` | BUTTON | `u8 button (0=L,1=R,2=M), u8 state (0=up,1=down)` | |
| `0x04` | KEY | `u16 vk, u8 state` | Windows virtual-key code; state 0 up, 1 down |
| `0x05` | TEXT | `utf8 bytes` | Injected as Unicode key events; handle surrogate pairs |
| `0x06` | PING | `u32 t` | `t` = client `performance.now()` in ms, truncated |
| `0x07` | ZOOM | `i16 delta` | Agent sends Ctrl down, wheel, Ctrl up |

### Agent → client

| Type | Name | Layout |
|---|---|---|
| `0x81` | PONG | `u32 t` (echoed) |

### Control (JSON text frames)

- Client first frame (within 3 s of connect): `{"t":"auth","token":"<token>","v":1}`
- Agent reply: `{"t":"ok","screen":{"w":..,"h":..},"v":1}` or `{"t":"err","reason":"..."}` then close.

### Agent-side coalescing [LATENCY]

The WebSocket handler must **not** call `SendInput` per message. It adds MOVE/SCROLL deltas to a shared accumulator and sets an `asyncio.Event`. A single injector task wakes, takes the summed deltas, resets the accumulator, and makes **one** `SendInput` call per wake-up. Buttons and keys go through an ordered queue in the same injector so order relative to movement is preserved (flush pending movement before a button/key event).

---

## 6. Client specification (web/)

### 6.1 UI
- Full-screen dark pad. Thin status strip at top: connection dot, latency (median and p95 of the last 100 pings).
- Bottom bar: **Left button**, **Right button**, keyboard toggle, fullscreen toggle, settings gear. Buttons must work with a second finger while the first finger moves the cursor.
- Ignore touches starting within **28 px of the left/right/bottom screen edges** (avoids Android back-gesture conflicts).
- CSS: `touch-action: none; overscroll-behavior: none; user-select: none; -webkit-tap-highlight-color: transparent;` viewport meta with `user-scalable=no`, `viewport-fit=cover`.

### 6.2 Input handling [LATENCY]
- Use **Pointer Events** on the pad. Per `pointermove`, read `event.getCoalescedEvents()` and sum their deltas into **one** MOVE packet. Never use timers or fixed-interval batching.
- Apply sensitivity on the phone; keep the fractional remainder between packets so slow movement is not lost.
- **Back-pressure:** if `ws.bufferedAmount > 4096`, do not send; keep accumulating deltas and send the sum once the buffer drains. This prevents lag building up on a bad network.
- No allocations in the move path: reuse a preallocated `ArrayBuffer`/`DataView` per packet type.
- No `console.log`, no DOM writes in the move path. Update the latency overlay at most once per second.

### 6.3 Gestures

| Gesture | Action |
|---|---|
| 1-finger drag | Move cursor |
| 1-finger tap (< 200 ms, < 8 px travel) | Left click |
| Tap, then touch again within 300 ms and drag | Drag (left button held until release) |
| 2-finger drag (same direction) | Scroll (natural scrolling on by default, configurable) |
| 2-finger tap | Right click |
| 3-finger tap | Middle click |
| 2-finger pinch (distance change dominates over translation) | ZOOM |
| 3-finger swipe up / down | Win+Tab / Win+D |
| 3-finger swipe left / right | Ctrl+Win+Left / Ctrl+Win+Right |

Tap detection uses a small dead zone only to decide "tap vs move"; once movement starts there is no filtering and no smoothing (smoothing adds delay).

### 6.4 Keyboard
- Keyboard button focuses a hidden `<input>` to summon the Android keyboard. Use `beforeinput`/`input` events: normal characters → TEXT; `deleteContentBackward` → Backspace; Enter → Enter.
- A special-keys row: Esc, Tab, Ctrl, Alt, Win, Shift (sticky toggles), arrows, Del, Home, End, F1–F12 (collapsible).

### 6.5 Settings (localStorage, applied live)
Sensitivity (0.5–4, default 1.6), scroll speed, natural scroll, tap-to-click on/off, haptic tick on click (`navigator.vibrate(8)`), left-handed button swap, show latency overlay.

### 6.6 Connection
- Auto-reconnect with backoff 250 ms → 2 s cap. Show state clearly. Re-authenticate with the stored token.
- Fullscreen button calls `requestFullscreen()` (needs a user tap), then tries `screen.orientation.lock("landscape")` inside try/catch.
- Request Screen Wake Lock if available (it only works in HTTPS mode); otherwise show a one-time hint to raise the screen timeout.

---

## 7. Agent specification (agent/)

### 7.1 Startup
- `python -m agent [--port 8765] [--https] [--dry-run] [--pair] [--revoke-all]`
- Detect the LAN IP used for the default route (UDP-connect trick, no packets sent) and print `http://<ip>:8765/?t=<token>` plus an **ASCII QR code** in the terminal. Also list other private IPs found.
- Config and paired-device tokens (store only **hashes**) in `%APPDATA%\phone-touchpad\config.json`.

### 7.2 Security (this app injects keyboard and mouse input, so it must be locked down)
- Bind `0.0.0.0` but **reject any connection whose remote IP is not private** (RFC1918, link-local).
- WebSocket must authenticate within 3 s or be closed. Constant-time token compare. Rate-limit failures (5 per minute per IP).
- Token is passed once via the QR URL, saved in the phone's `localStorage`, and stripped from the URL afterwards (`history.replaceState`).
- Only **one** active client; a new authenticated connection replaces the old one.
- `--revoke-all` deletes all tokens. `--pair` creates a new one and prints a QR.
- **Plain HTTP is unencrypted on the LAN.** Document this honestly in README: acceptable on a home network, not on public Wi-Fi. Optional `--https`: generate a local CA + server cert at first run (cached in the config dir); user accepts the browser warning once; this also enables Wake Lock and `crypto.subtle`.
- Never run elevated by default. Document that injected input cannot reach UAC prompts or elevated windows unless the agent is run as administrator.

### 7.3 Input injection (inject_win.py)
- `ctypes.windll.user32.SendInput` with correct `INPUT` / `MOUSEINPUT` / `KEYBDINPUT` structures (64-bit safe, `ULONG_PTR`).
- Mouse movement: **relative** `MOUSEEVENTF_MOVE` (no absolute coordinates).
- Wheel: `MOUSEEVENTF_WHEEL` / `MOUSEEVENTF_HWHEEL`, with a fractional accumulator so high-resolution scroll deltas are not dropped.
- Keys: VK-based; set `KEYEVENTF_EXTENDEDKEY` for arrows and navigation keys. Text: `KEYEVENTF_UNICODE`.
- **Stuck-input protection:** track pressed buttons/keys and release all of them when the WebSocket closes or the agent exits.
- Clamp absurd single-packet deltas (e.g. > 4000 px).
- `--dry-run` replaces `SendInput` with a no-op counter (used by the latency tool and tests).

### 7.4 Performance [LATENCY]
- Call `winmm.timeBeginPeriod(1)` at start (and `timeEndPeriod` on exit).
- Raise process priority (`SetPriorityClass` → `HIGH_PRIORITY_CLASS`).
- Confirm `TCP_NODELAY` is set on the WebSocket socket (aiohttp sets it by default; assert it).
- No logging in the hot path (log only connect/disconnect/errors). Default log level INFO; no per-packet logs.
- Use the accumulator + single injector design from section 5.

### 7.5 Static files and network
- Serve `web/` with `Cache-Control: no-cache`. Routes: `/`, `/app.js`, modules, `/style.css`, `/manifest.webmanifest`, `/favicon.svg`.
- `scripts/allow-firewall.ps1`: a **printed-only** helper containing `netsh advfirewall firewall add rule ... profile=private`. The agent must never run it automatically.

---

## 8. Latency budget (for reasoning and verification)

| Stage | Budget |
|---|---|
| Touch sampling + browser event dispatch | 2–6 ms |
| Encode + send (WebSocket, Wi-Fi 5 GHz) | 2–8 ms |
| Agent receive → SendInput | < 1 ms |
| Windows cursor update | ≤ 1 frame |

Wi-Fi jitter is the dominant risk. That is why section 6.2 back-pressure exists and why section 8b exists.

### 8b. Phase 2 (M6, only if measurements show p95 > 20 ms)
Add an `RTCDataChannel` with `ordered:false, maxRetransmits:0` for MOVE/SCROLL (via `aiortc`, signaling over the existing WebSocket), falling back to WebSocket automatically. Because loss would drop distance, switch to **cumulative counters**: each packet carries total dx/dy since connect (i32, wrapping) and the agent injects `counter − last`. Known risk: Chrome hides host IPs behind mDNS candidates that `aiortc` may not resolve; if so, stay on WebSocket.

---

## 9. Milestones and acceptance checks

**M1 — Skeleton.** Agent serves the page, WebSocket auth works, QR printed, `--dry-run` works. Client full-screen pad sends MOVE; cursor moves.
*Accept:* on the phone, a finger drag moves the Windows cursor smoothly; wrong token is rejected; non-private IP is rejected (unit test).

**M2 — Mouse.** Buttons, tap-click, drag, scroll, zoom, multi-finger gestures, settings.
*Accept:* every row of the 6.3 table works; left/right bar buttons work while moving.

**M3 — Keyboard.** Text entry, special keys, sticky modifiers.
*Accept:* typing, Backspace, Enter, Ctrl+C / Ctrl+V, arrows, Win key work.

**M4 — Latency tooling.** Ping/pong overlay; `tools/latency_test.py` simulates a client sending 5,000 MOVE packets at 240 Hz against `--dry-run` and reports p50/p95/p99 server handling time and accumulator coalescing ratio.
*Accept:* server handling p99 < 2 ms; overlay shows RTT on the phone; tests in `tests/` pass (`protocol`, `coalesce`, `private-ip filter`, `auth rate limit`).

**M5 — Robustness.** Reconnect, stuck-input release, firewall script, `--https` mode, README with security notes and troubleshooting.
*Accept:* killing Wi-Fi mid-drag releases the mouse button; reconnect succeeds within 3 s; README covers every section 12 tip.

**M6 — Optional WebRTC (section 8b).** Only on request.

---

## 10. Project structure

```
touchpad/
  SPEC.md
  README.md
  requirements.txt
  agent/
    __main__.py   server.py   inject_win.py   protocol.py
    auth.py       netinfo.py  config.py
  web/
    index.html  app.js  gestures.js  protocol.js  style.css
    manifest.webmanifest  favicon.svg
  scripts/
    allow-firewall.ps1
  tools/
    latency_test.py
  tests/
    test_protocol.py  test_coalesce.py  test_auth.py  test_netinfo.py
```

---

## 11. Non-functional requirements

- Works fully offline on the LAN. No telemetry, no external requests.
- Clear error messages in the terminal (port in use, no private IP found, firewall hint).
- Code comments only where non-obvious. Type hints in Python.
- Gesture logic kept in `gestures.js` as a pure state machine (input: pointer events; output: actions) so it can be reasoned about and reviewed separately.

---

## 12. Easiest way to deploy to the phone (no SDK, no APK, no store)

1. Put phone and laptop on the **same 5 GHz Wi-Fi** (or turn on Windows **Mobile Hotspot** and join it from the phone; the laptop's fixed address there is `192.168.137.1`).
2. On the laptop: `pip install -r requirements.txt`, then `python -m agent`.
3. First run: allow Python through Windows Firewall on **Private** networks only.
4. On the phone, open the camera, scan the QR in the terminal, open the link in **Chrome**.
5. Chrome menu (⋮) → **Add to Home screen** for a one-tap shortcut. Tap the fullscreen button inside the app.
6. Samsung tips: turn off *Settings → Wi-Fi → Intelligent Wi-Fi / Switch to mobile data* so the phone does not drop Wi-Fi; disable Edge panels; keep the screen timeout long while using the app.
7. If the laptop's IP changes on your home router, reserve it (DHCP reservation) so the shortcut keeps working. The hotspot option avoids this.

**Trade-offs of this approach:** no Bluetooth, no lock-screen use, and a few ms of browser overhead versus a native app. If you later want a native APK without installing the SDK locally, it can be built in the cloud (for example GitHub Actions) from the same protocol.
