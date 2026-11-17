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

Run the tests with `python -m pytest`.

## License

[PolyForm Noncommercial License 1.0.0](LICENSE). Free for personal, educational, research and non-profit use. Commercial use requires the author's permission. See [NOTICE](NOTICE).
