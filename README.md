# android-touch-pad

Turn your Android phone into a low-latency touchpad and keyboard for a Windows laptop, over your local Wi-Fi. No Android Studio, no SDK, no APK, no app store: the phone just opens a web page served by a small Python agent running on the laptop.

```
 Phone (Chrome)                          Windows laptop
┌──────────────────┐   WebSocket      ┌──────────────────────────┐
│ web/ (vanilla JS)│ ───────────────► │ agent/ (Python asyncio)  │
│ touch → packets  │   binary frames  │ auth → coalesce → inject │
└──────────────────┘ ◄─────────────── └──────────────────────────┘
                         ping / pong
```

The full design is in [SPEC.md](SPEC.md).

## Requirements

- Windows laptop with Python 3.11 or newer, **or** the C# tray app (below), which needs nothing installed
- Android phone with Chrome, on the same Wi-Fi network as the laptop (5 GHz recommended)

## Quick start

```
pip install -r requirements.txt
python -m agent
```

1. The agent prints a QR code. Scan it with the phone camera and open the link in **Chrome**.
2. In Chrome's menu (⋮) choose **Add to Home screen** for a one-tap shortcut. Tap the fullscreen button (⛶) inside the app.
3. Move your finger on the screen. The first time, Windows Firewall may ask whether to allow Python: allow **Private networks** only (see [Troubleshooting](#troubleshooting)).

The QR code is only printed the first time (and with `--pair`). After pairing, just open the shortcut.

### Or: the C# tray app

`windows/` holds a second agent, a native Windows tray application written in C#. It speaks the same protocol, serves the same phone client, and uses the same pairing file, so the phone cannot tell the two apart. Use whichever you prefer, **one at a time** (both default to port 8765).

- No Python and no console window: a green icon in the notification area. Right-click it for *Pair a phone* (opens a QR code in your browser), *Forget all phones*, *Use HTTPS*, *Start with Windows*, *Allow in Windows Firewall*, and *Quit*. A balloon tells you when a phone connects.
- One self-contained `PhoneTouchpad.exe` (the phone client is built in). You do not need the .NET SDK: every push builds the exe on GitHub (Actions tab, workflow *Windows agent*, artifact `PhoneTouchpad-win-x64`). To build it yourself, see [windows/README.md](windows/README.md).
- Same options on the command line: `--port N`, `--https`, `--dry-run`, `--web-dir PATH`, `--revoke-all`.

### Agent options

| Option | Meaning |
|---|---|
| `--port N` | Port to listen on (default 8765) |
| `--https` | Serve HTTPS with a self-signed certificate (encrypted; enables screen wake lock) |
| `--pair` | Create a new pairing token and QR code, to pair another phone |
| `--revoke-all` | Forget every paired phone |
| `--dry-run` | Do everything except inject input (works on any OS; used by tests) |

## Gestures

| Gesture | Action |
|---|---|
| One-finger drag | Move the cursor |
| Tap | Left click |
| Tap, then touch again and drag | Drag (left button held); lifting without moving is a double click |
| Two-finger drag | Scroll (natural scrolling by default) |
| Two-finger tap | Right click |
| Three-finger tap | Middle click |
| Two-finger pinch / spread | Zoom (Ctrl + wheel) |
| Three-finger swipe up / down | Win+Tab / Win+D |
| Three-finger swipe left / right | Ctrl+Win+Left / Ctrl+Win+Right |

### Portrait and wide layouts

Laptop touchpads are wide; a phone held upright is tall and narrow. The **⟳** button (or *Layout* in the gear panel) switches between:

- **Normal**: the page follows the phone (turn on auto-rotate and it also works held sideways).
- **Wide, phone turned left** and **Wide, phone turned right**: hold the phone sideways and the whole screen is drawn turned 90 degrees, with the touch axes remapped, so dragging along the long side moves the cursor left and right. Use these if auto-rotate is locked, or to pick which way you turn the phone.

Tap **⟳** to cycle through the three. The switch happens on the phone only, so the connection stays up and nothing is re-paired. A touch in progress is cancelled when you switch. The choice is remembered.

### Pointer acceleration

The cursor speed depends on how fast your finger moves, like a laptop touchpad: slow movements are slightly damped for precise aiming, normal movements feel 1:1, and quick flicks travel much farther.

Two settings (gear icon):
- **Pointer speed** scales everything.
- **Acceleration** sets the strength of the curve, from 0 to 1 (default 0.5). At 0 there is no acceleration (plain linear movement). At 1 the slowest movements are scaled to 0.6x and flicks to 4x.

To tune it, set Pointer speed so normal movement feels right, then raise Acceleration until a flick crosses the screen but slow aiming still feels precise. On top of this, Windows applies its own pointer settings: for fully predictable movement turn off *Enhance pointer precision* in Windows mouse settings.

The curve is a smooth function of finger speed (`web/accel.js`); it does not affect scrolling or pinch zoom.

The **Left** and **Right** buttons at the bottom work while another finger moves the cursor. The gear opens settings: layout, pointer speed, scroll speed, natural scrolling, tap to click, haptic tick, left-handed buttons, and the latency display. Touches within 28 px of the left and right screen edges are ignored so Android's back gesture doesn't interfere.

## Keyboard

The **⌨** button opens the special-keys panel and the phone's on-screen keyboard. Typed text is sent as Unicode, so any language works (including Arabic and emoji). The panel has Esc, Tab, arrows, Backspace, Enter, Del, Home, End, PgUp/PgDn and F1-F12; arrows, Backspace and Del repeat while held.

**Ctrl, Alt, Win and Shift are sticky:** tap one to latch it (it turns green), then press one key and it is released automatically. For example, tap Ctrl, then type `c` on the on-screen keyboard to send Ctrl+C.

Known limitation: on-screen keyboards report edits differently (Gboard, Samsung Keyboard, ...). The page mirrors the text field's edits, including autocorrect, but this has only been tested in simulation, not on every keyboard. If typing misbehaves, switch the phone's keyboard or use the special-keys panel.

## Latency

The status strip at the top of the app shows the round-trip time to the laptop (median and 95th percentile of the last 100 pings, e.g. `connected · 8 ms (p95 14)`). One-way delay is roughly half. Turn the display off in settings.

To measure the agent alone (no Wi-Fi, no phone), run the simulator, which sends 5,000 MOVE packets at 240 Hz to an agent in dry-run mode:

```
python -m tools.latency_test                       # fails if server handling p99 > 2 ms
python -m tools.latency_test --rate 500 --burst 8  # stress: bursts show the move coalescing
```

How the design keeps latency low: browsers can't send raw UDP, so the app uses a WebSocket with tiny binary packets, never JSON; it sends on every touch event with no timers; if the socket backs up it accumulates movement and sends the sum instead of queueing stale packets; and the agent merges queued moves and injects them with one `SendInput` call.

## Security

This app lets a device on your network type and click on your laptop, so treat the pairing token like a password.

- **Pairing:** the QR code contains a random 128-bit token. The agent stores only its hash. Use `--revoke-all` to forget all phones.
- **Local network only:** the agent refuses connections from non-private addresses (10.x, 172.16-31.x, 192.168.x, link-local and loopback). Do not forward the port on your router.
- **One phone at a time:** a new authenticated connection replaces the old one. Failed logins are rate-limited (5 per minute per address).
- **Plain HTTP is not encrypted.** Anyone on the same Wi-Fi who can capture traffic could read the token and the typed text. That is acceptable on a home network you trust, but **not on public or shared Wi-Fi**. Use `--https` there: the traffic is encrypted, but the phone will show a certificate warning the first time (tap Advanced, then Proceed) because the certificate is self-signed.
- **Elevated windows:** Windows does not let normal programs send input to administrator windows or UAC prompts. Running the agent as administrator lifts that, and also lets a paired phone control those windows, so only do it when you need to.
- **Stuck keys:** if the phone disconnects or goes silent for 3 seconds, the agent releases any held mouse button or key.

## Troubleshooting

**The phone can't open the page.**
- Check both are on the same Wi-Fi. Some routers and guest networks block devices from talking to each other (client or AP isolation); use another network or the laptop's hotspot (below).
- Allow the port through Windows Firewall. In an elevated PowerShell run `.\scripts\allow-firewall.ps1` (Private networks), or `.\scripts\allow-firewall.ps1 -Profile Any` if you use the Windows Mobile Hotspot, whose network is often classed as Public. `-Remove` undoes it. The agent never changes firewall rules itself.
- Make sure you typed `http://` (or `https://` if you started with `--https`).

**The link stopped working after a while.** The laptop's address probably changed. Give the laptop a fixed address in your router's settings (a DHCP reservation), or use the laptop's own hotspot: Windows Settings → Network → Mobile hotspot, join it from the phone; the laptop is always `192.168.137.1` there, so the shortcut never breaks.

**"Pairing failed" or "Not paired".** Run `python -m agent --pair` and scan the new QR code.

**Cursor is laggy or jumpy.**
- Use the 5 GHz band. Keep the phone and laptop close to the router.
- Watch the latency in the status strip. Steady single digits are good; large swings mean Wi-Fi interference.
- Samsung phones: turn off *Settings → Connections → Wi-Fi → ⋮ → Advanced → Switch to mobile data* (the exact name varies by version, e.g. "Intelligent Wi-Fi"), which can drop the Wi-Fi link when it thinks the connection is weak.

**The Android back gesture or edge panel keeps triggering.** Start your drags a little away from the screen edges, and turn off *Settings → Display → Edge panels* if they get in the way.

**The screen turns off while I'm using it.** Raise the screen timeout while you use the app, or start the agent with `--https` so the app can keep the screen awake itself.

**Typing duplicates or drops characters.** See the keyboard note above; try Gboard, or use the special-keys panel.

**Clicks don't reach an admin window or a UAC prompt.** See [Security](#security). The agent logs a warning the first time Windows blocks input.

## Development

```
pip install -r requirements.txt
python -m pytest                 # agent: protocol, auth, coalescing, injection, watchdog, TLS, end-to-end WebSocket
node --test tests/*.test.mjs     # gestures, keyboard, latency stats, client connection logic (Node 20+)
```

```
agent/      Python agent: server, auth, input injection, TLS, network helpers
web/        The phone page (plain JavaScript, no build step)
tools/      latency_test.py
scripts/    allow-firewall.ps1
tests/      pytest and Node tests
```

The real `SendInput` calls only run on Windows; on other systems the tests exercise the same code through a fake `user32` and `--dry-run`.

### Decisions

- **Web app instead of a native app**, so there is nothing to install on the phone and no Android SDK on the laptop. The cost is a few milliseconds of browser overhead and no Bluetooth (a browser cannot act as a Bluetooth mouse).
- **WebSocket (TCP), not WebRTC/UDP.** Simpler and reliable; back-pressure handling avoids lag build-up. A WebRTC unreliable channel (SPEC milestone M6) is only worth adding if measurements on real hardware show Wi-Fi jitter is a problem.
- **The layout switch rotates the UI with CSS and remaps touch points in the browser** (`web/layout.js`) rather than locking the screen orientation, because orientation lock needs fullscreen and is not available in every browser. The agent is unaware of it. In wide layouts the phone's safe-area insets are not applied.
- **A second, C# agent sits next to the Python one** (`windows/`) rather than replacing it. The Python agent stays the quickest thing to read, change and run on any OS in `--dry-run`; the C# one is the one to leave running in the tray. They share the wire protocol, the token file (`%APPDATA%\\phone-touchpad\\config.json`) and the web client, and tests on both sides check the same byte-level vectors. The tray icon is plain Win32 through P/Invoke, not WinForms, so the project builds and its tests run on any OS with the .NET 8 SDK.
- **Acceleration runs on the phone**, per touch event, so it adds no network or agent delay; the curve depends on smoothed finger speed, not on Windows' own acceleration.
- **Tap dead zone is 6 px** rather than 8 px (SPEC), to shorten the delay before movement starts.
- **KEY messages arrived in M2** (not M3) because the three-finger swipes send shortcuts.
- **Sticky modifiers are one-shot:** they release after the next key, like Android's sticky keys. Mouse clicks do not release them, so Ctrl+click works.
- **A single self-signed certificate** for `--https` (not a local CA), regenerated automatically when the laptop's addresses change.
- **Liveness:** the phone pings every second; the agent drops a client silent for 3 s and the phone reconnects if the agent is silent for 2.5 s.

## License

[PolyForm Noncommercial License 1.0.0](LICENSE). Free for personal, educational, research and non-profit use; commercial use needs the author's permission. See [NOTICE](NOTICE).
