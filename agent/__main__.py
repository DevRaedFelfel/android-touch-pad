"""Entry point: python -m agent [--port N] [--dry-run] [--pair] [--revoke-all]"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

from aiohttp import web

from . import __version__
from .auth import TokenStore
from .config import config_dir
from .inject_win import make_injector, tune_process, untune_process
from .netinfo import lan_ip, private_ips
from .server import create_app

WEB_DIR = Path(__file__).resolve().parent.parent / "web"


def _print_qr(url: str) -> None:
    import qrcode

    qr = qrcode.QRCode(border=1)
    qr.add_data(url)
    qr.make(fit=True)
    qr.print_ascii(invert=True)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="python -m agent", description=__doc__)
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--https", action="store_true", help="serve HTTPS with a self-signed certificate")
    p.add_argument("--dry-run", action="store_true", help="do not inject input")
    p.add_argument("--pair", action="store_true", help="create a new pairing token")
    p.add_argument("--revoke-all", action="store_true", help="forget all paired phones")
    p.add_argument("--config-dir", type=Path, default=None, help=argparse.SUPPRESS)
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")

    cfg_dir = args.config_dir or config_dir()
    tokens = TokenStore(cfg_dir / "config.json")

    if args.revoke_all:
        tokens.revoke_all()
        print("All paired phones have been forgotten.")
        return 0

    new_token = tokens.create() if (args.pair or not tokens.has_tokens()) else None

    ip = lan_ip()
    if ip is None:
        if not args.dry_run:
            print(
                "No private network address found. Connect the laptop to Wi-Fi "
                "(or start the Windows Mobile Hotspot) and try again.",
                file=sys.stderr,
            )
            return 1
        ip = "127.0.0.1"

    ssl_context = None
    if args.https:
        from .tls import ensure_cert, make_ssl_context

        cert, key = ensure_cert(cfg_dir, [ip, *private_ips()])
        ssl_context = make_ssl_context(cert, key)

    injector = make_injector(args.dry_run)
    app = create_app(injector, tokens, WEB_DIR)

    scheme = "https" if ssl_context else "http"
    base = f"{scheme}://{ip}:{args.port}/"
    print(f"Phone Touchpad agent {__version__}" + ("  [DRY RUN]" if args.dry_run else ""))
    if new_token:
        url = f"{base}?t={new_token}"
        print("\nScan this QR code with the phone camera (first-time pairing):\n")
        _print_qr(url)
        print(f"\n{url}\n")
    else:
        print(f"\nPhone already paired. Open {base} on the phone.")
        print("Use --pair to pair another phone.\n")
    if ssl_context:
        print("HTTPS: the phone warns about the certificate the first time. Tap Advanced, then Proceed.")
    if sys.platform == "win32":
        print("If the phone cannot connect, allow the port in Windows Firewall: see scripts\\allow-firewall.ps1")
    others = [a for a in private_ips() if a != ip]
    if others:
        print("Other addresses: " + ", ".join(others))
    print("Press Ctrl+C to stop.\n")

    tune_process()
    try:
        web.run_app(
            app, host="0.0.0.0", port=args.port, ssl_context=ssl_context, print=None, access_log=None
        )
    except OSError as e:
        print(f"Cannot listen on port {args.port}: {e}. Try --port <other>.", file=sys.stderr)
        return 1
    finally:
        untune_process()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
