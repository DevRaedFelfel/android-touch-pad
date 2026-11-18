# Phone Touchpad, Windows tray agent (C#)

The same agent as `python -m agent`, as a native tray application. See the [main README](../README.md) for what the project does and how the phone side works.

## Get the exe

Every push builds it: open the repository's **Actions** tab, the latest *Windows agent* run, and download the artifact `PhoneTouchpad-win-x64`. It is one self-contained file (about 40 MB; it includes the .NET runtime and the phone web client). Put it anywhere and run it.

Windows SmartScreen may warn about an unsigned program the first time: *More info*, then *Run anyway*.

## Use it

1. Run `PhoneTouchpad.exe`. A green touchpad icon appears in the notification area (it may be under the ^ arrow).
2. First run: your browser opens a page with a QR code. Scan it with the phone camera and open the link in Chrome. Later, *Pair a phone...* in the icon's menu does the same.
3. If the phone cannot connect, choose **Allow in Windows Firewall...** (Windows asks for administrator approval; it adds a rule for the port on private networks only).

Menu: status line (shows the address), Pair a phone, Forget all phones, Use HTTPS, Start with Windows, Allow in Windows Firewall, Quit.

The agent keeps its files in `%APPDATA%\phone-touchpad`: `config.json` (hashes of paired-phone tokens, shared with the Python agent), `tray-settings.json` (port, HTTPS) and, with HTTPS, `server-cs.pfx`.

## Build and test it yourself

Needs the .NET 8 SDK (any OS; the tests run without Windows).

```
dotnet run --project tests/TouchpadAgent.Tests        # 36 tests; exits non-zero on failure
dotnet publish src/TouchpadAgent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```

`out\PhoneTouchpad.exe --self-test` (Windows only) starts the agent, connects to it like a phone and checks that the real cursor moves.

## Layout

| Folder | Contents |
|---|---|
| `src/TouchpadAgent.Core` | Cross-platform library: protocol, tokens, rate limiter, Kestrel HTTP + WebSocket server, input queue, TLS, embedded web client |
| `src/TouchpadAgent` | The Windows exe: `SendInput` injector, tray icon and menu (Win32 through P/Invoke), `--self-test` |
| `tests/TouchpadAgent.Tests` | Plain console test runner (no NuGet test packages): unit tests, a fake `user32`, and end-to-end tests over a real WebSocket and TLS |

## Notes

- Same limits as the Python agent: plain HTTP on the local network is not encrypted (use *Use HTTPS*), and Windows blocks injected input into elevated windows unless the agent runs as administrator.
- The QR page is served on `port + 1`, on this laptop only (loopback), never to the network.
- The icon is drawn with Win32 calls and has not yet been checked on a real desktop beyond the `--self-test`; please report anything odd (a missing icon after an Explorer restart, a menu that does not close).
