"""Latency test against an in-process agent running in --dry-run mode.

A simulated phone sends MOVE packets at a fixed rate (default 240 Hz, like a fast
touch screen) and measures:

  * server handling time: from the moment a MOVE packet reaches the handler until
    its movement is handed to the injector (p50/p95/p99)
  * coalescing ratio: MOVE packets received per injector call
  * loopback round-trip time of PING/PONG (the same measurement the phone shows)

Run:  python -m tools.latency_test [--packets 5000] [--rate 240] [--max-p99-ms 2]
It exits with status 1 if the server handling p99 exceeds --max-p99-ms.

This measures the agent only. Wi-Fi, the phone's touch pipeline and Windows'
SendInput are not included; use the latency overlay in the app for the real link.
"""

from __future__ import annotations

import argparse
import asyncio
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

import aiohttp
from aiohttp import web

from agent.auth import TokenStore
from agent.inject_win import DryRunInjector
from agent.protocol import T_PONG, encode_move, encode_ping
from agent.server import CONTROLLER, create_app
from agent.stats import percentile

WEB_DIR = Path(__file__).resolve().parent.parent / "web"


@dataclass
class Report:
    packets: int
    rate_hz: float
    handling_us: dict[str, float]
    rtt_us: dict[str, float]
    injector_calls: int
    coalescing_ratio: float
    total_dx_ok: bool

    def format(self) -> str:
        h, r = self.handling_us, self.rtt_us
        return "\n".join(
            [
                f"packets sent       : {self.packets} ({self.rate_hz:g} ticks/s)",
                f"server handling    : p50 {h['p50']:.0f} us   p95 {h['p95']:.0f} us   p99 {h['p99']:.0f} us   max {h['max']:.0f} us",
                f"loopback ping RTT  : p50 {r['p50']:.0f} us   p95 {r['p95']:.0f} us   p99 {r['p99']:.0f} us",
                f"injector calls     : {self.injector_calls}  (coalescing {self.coalescing_ratio:.2f} packets per call)",
                f"movement preserved : {'yes' if self.total_dx_ok else 'NO - movement was lost'}",
            ]
        )


def _summary(values_us: list[float]) -> dict[str, float]:
    return {
        "p50": percentile(values_us, 50),
        "p95": percentile(values_us, 95),
        "p99": percentile(values_us, 99),
        "max": max(values_us) if values_us else 0.0,
    }


async def run_test(
    packets: int = 5000, rate_hz: float = 240.0, ping_every: int = 24, burst: int = 1
) -> Report:
    samples: list[int] = []
    injector = DryRunInjector()
    with tempfile.TemporaryDirectory() as tmp:
        tokens = TokenStore(Path(tmp) / "config.json")
        token = tokens.create()
        app = create_app(injector, tokens, WEB_DIR)
        app[CONTROLLER].samples = samples
        runner = web.AppRunner(app, access_log=None)
        await runner.setup()
        site = web.TCPSite(runner, "127.0.0.1", 0)
        await site.start()
        port = site._server.sockets[0].getsockname()[1]  # noqa: SLF001 (ephemeral port)

        rtts_us: list[float] = []
        pending: dict[int, int] = {}

        async with aiohttp.ClientSession() as session:
            async with session.ws_connect(f"http://127.0.0.1:{port}/ws", compress=0) as ws:
                await ws.send_json({"t": "auth", "token": token, "v": 1})
                assert (await ws.receive_json())["t"] == "ok"

                async def reader() -> None:
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.BINARY and msg.data[0] == T_PONG:
                            t = int.from_bytes(msg.data[1:5], "little")
                            sent = pending.pop(t, None)
                            if sent is not None:
                                rtts_us.append((time.perf_counter_ns() - sent) / 1000)

                reader_task = asyncio.create_task(reader())
                interval = 1.0 / rate_hz
                start = time.perf_counter()
                sent = 0
                tick = 0
                while sent < packets:
                    for _ in range(min(burst, packets - sent)):  # `burst` packets back to back
                        await ws.send_bytes(encode_move(sent, 1, 1))
                        sent += 1
                    if tick % ping_every == 0:
                        pending[tick] = time.perf_counter_ns()
                        await ws.send_bytes(encode_ping(tick))
                    tick += 1
                    delay = start + tick * interval - time.perf_counter()
                    await asyncio.sleep(max(0.0, delay))
                await asyncio.sleep(0.2)  # let the last packets drain
                await ws.close()
                reader_task.cancel()
        calls = injector.move_calls
        ok = injector.total_dx == packets and injector.total_dy == packets
        await runner.cleanup()

    handling_us = [s / 1000 for s in samples]
    return Report(
        packets=packets,
        rate_hz=rate_hz,
        handling_us=_summary(handling_us),
        rtt_us=_summary(rtts_us),
        injector_calls=calls,
        coalescing_ratio=packets / calls if calls else 0.0,
        total_dx_ok=ok,
    )


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--packets", type=int, default=5000)
    p.add_argument("--rate", type=float, default=240.0, help="send ticks per second")
    p.add_argument("--burst", type=int, default=1, help="MOVE packets sent back to back per tick")
    p.add_argument("--max-p99-ms", type=float, default=2.0, help="fail if handling p99 exceeds this")
    args = p.parse_args(argv)

    report = asyncio.run(run_test(args.packets, args.rate, burst=args.burst))
    print(report.format())
    if not report.total_dx_ok:
        return 1
    if report.handling_us["p99"] / 1000 > args.max_p99_ms:
        print(f"FAIL: handling p99 above {args.max_p99_ms} ms")
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
