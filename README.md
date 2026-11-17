# android-touch-pad

Turn your Android phone into a low-latency touchpad and keyboard for a Windows laptop, over your local Wi-Fi. No Android Studio, no SDK, no APK: the phone just opens a web page served by a small Python agent running on the laptop.

> Status: under construction. See [SPEC.md](SPEC.md) for the full specification and milestones.

## How it works

```
 Phone (Chrome)                          Windows laptop
┌──────────────────┐   WebSocket      ┌──────────────────────────┐
│ web/ (vanilla JS)│ ───────────────► │ agent/ (Python asyncio)  │
│ touch → packets  │   binary frames  │ auth → coalesce → inject │
└──────────────────┘                  └──────────────────────────┘
```

## Requirements

- Windows laptop with Python 3.11+
- Android phone with Chrome, on the same Wi-Fi network (5 GHz recommended)

## Quick start

```
pip install -r requirements.txt
python -m agent
```

Scan the QR code printed in the terminal with the phone camera and open the link in Chrome. Use `--dry-run` to try it without injecting input (works on any OS), `--pair` to pair another phone, `--revoke-all` to forget all phones.

## Gestures

| Gesture | Action |
|---|---|
| One-finger drag | Move the cursor |
| Tap | Left click |
| Tap, then touch again and drag | Drag (left button held); lifting without moving is a double click |
| Two-finger drag | Scroll (natural scrolling by default; change it in settings) |
| Two-finger tap | Right click |
| Three-finger tap | Middle click |
| Two-finger pinch / spread | Zoom (Ctrl + wheel) |
| Three-finger swipe up / down | Win+Tab / Win+D |
| Three-finger swipe left / right | Ctrl+Win+Left / Ctrl+Win+Right |

The **Left** and **Right** buttons at the bottom work while another finger moves the cursor. The gear opens settings (pointer speed, scroll speed, natural scrolling, tap to click, haptics, left-handed buttons).

## Keyboard

The **⌨** button opens the special-keys panel and the phone's on-screen keyboard. Typed text is sent as Unicode, so any language works (including Arabic and emoji). The panel has Esc, Tab, arrows, Backspace, Enter, Del, Home, End, PgUp/PgDn and F1-F12.

**Ctrl, Alt, Win and Shift are sticky:** tap one to latch it (it turns green), then press one key and it is released automatically. For example, tap Ctrl, then type `c` on the on-screen keyboard to send Ctrl+C.

Known limitation: on-screen keyboards report edits differently (Gboard, Samsung Keyboard, ...). The page mirrors the text field's edits, including autocorrect, but this has only been tested in simulation, not on every keyboard. If typing misbehaves, switch the phone's keyboard or use the special-keys panel.

## Tests

```
python -m pytest              # agent: protocol, auth, coalescing, injection, end-to-end WebSocket
node --test tests/gestures.test.mjs tests/keyboard.test.mjs   # gestures and keyboard logic (Node 20+)
```

## License

[PolyForm Noncommercial License 1.0.0](LICENSE). Free for personal, educational, research and non-profit use. Commercial use requires the author's permission. See [NOTICE](NOTICE).
