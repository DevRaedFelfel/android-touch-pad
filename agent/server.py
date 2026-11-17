"""HTTP + WebSocket server and the input controller.

Latency design (SPEC.md section 5): the WebSocket handler never injects input
itself. It appends to an ordered op list, merging consecutive moves into one, and
wakes a single injector task that applies the whole list in one go.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import socket
from pathlib import Path

from aiohttp import WSMsgType, web

from .auth import RateLimiter, TokenStore
from .netinfo import is_allowed_ip
from .protocol import PROTOCOL_VERSION, parse_binary

log = logging.getLogger("touchpad")

MAX_STEP = 4000  # clamp for a single injected movement, in pixels


def _clamp(v: int) -> int:
    return max(-MAX_STEP, min(MAX_STEP, v))


class InputController:
    """Ordered, coalescing queue between the socket handler and the injector."""

    def __init__(self, injector) -> None:
        self.injector = injector
        self._ops: list[list] = []
        self._wake = asyncio.Event()
        self.packets = 0  # movement packets received
        self.flushes = 0  # times the injector drained the queue

    def move(self, dx: int, dy: int) -> None:
        self.packets += 1
        ops = self._ops
        if ops and ops[-1][0] == "move":
            last = ops[-1]
            last[1] += dx
            last[2] += dy
        else:
            ops.append(["move", dx, dy])
        self._wake.set()

    def take_ops(self) -> list[list]:
        ops, self._ops = self._ops, []
        return ops

    def apply(self, op: list) -> None:
        if op[0] == "move":
            dx, dy = _clamp(op[1]), _clamp(op[2])
            if dx or dy:
                self.injector.move(dx, dy)

    async def run(self) -> None:
        while True:
            await self._wake.wait()
            self._wake.clear()
            self.flushes += 1
            for op in self.take_ops():
                try:
                    self.apply(op)
                except Exception:  # keep the injector alive whatever happens
                    log.exception("input injection failed")

    def release_all(self) -> None:
        self.injector.release_all()


CONTROLLER = web.AppKey("controller", InputController)
TOKENS = web.AppKey("tokens", TokenStore)
LIMITER = web.AppKey("limiter", RateLimiter)
STATE = web.AppKey("state", dict)


@web.middleware
async def private_only(request: web.Request, handler):
    if not is_allowed_ip(request.remote):
        raise web.HTTPForbidden(text="forbidden")
    return await handler(request)


async def _no_cache(request: web.Request, response: web.StreamResponse) -> None:
    if request.path != "/ws":
        response.headers["Cache-Control"] = "no-cache"


def _set_nodelay(request: web.Request) -> None:
    transport = request.transport
    sock = transport.get_extra_info("socket") if transport else None
    if sock is not None:
        with contextlib.suppress(OSError):
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)


async def ws_handler(request: web.Request) -> web.StreamResponse:
    app = request.app
    ip = request.remote or ""
    limiter = app[LIMITER]
    state = app[STATE]
    ctrl = app[CONTROLLER]

    ws = web.WebSocketResponse(compress=False, max_msg_size=64 * 1024)
    await ws.prepare(request)
    _set_nodelay(request)

    async def reject(reason: str) -> web.StreamResponse:
        await ws.send_json({"t": "err", "reason": reason})
        await ws.close()
        return ws

    if not limiter.allow(ip):
        return await reject("rate_limited")

    try:
        first = await ws.receive(timeout=state["auth_timeout"])
    except asyncio.TimeoutError:
        return await reject("auth_timeout")
    if first.type != WSMsgType.TEXT:
        return await reject("bad_request")
    try:
        auth = json.loads(first.data)
        if not isinstance(auth, dict) or auth.get("t") != "auth":
            raise ValueError
    except ValueError:
        return await reject("bad_request")
    if auth.get("v") != PROTOCOL_VERSION:
        return await reject("bad_version")
    if not app[TOKENS].verify(auth.get("token")):
        limiter.record_failure(ip)
        return await reject("bad_token")

    # Only one active client: a new authenticated connection replaces the old one.
    old = state.get("active")
    if old is not None and not old.closed:
        await old.close()
        ctrl.release_all()
    state["active"] = ws

    w, h = ctrl.injector.screen_size()
    await ws.send_json({"t": "ok", "v": PROTOCOL_VERSION, "screen": {"w": w, "h": h}})
    log.info("client connected: %s", ip)

    try:
        async for msg in ws:
            if msg.type == WSMsgType.BINARY:
                parsed = parse_binary(msg.data)
                if parsed is not None and parsed[0] == "move":
                    ctrl.move(parsed[2], parsed[3])
    finally:
        if state.get("active") is ws:
            state["active"] = None
            ctrl.release_all()
        log.info("client disconnected: %s", ip)
    return ws


async def _index(request: web.Request) -> web.StreamResponse:
    return web.FileResponse(request.app[STATE]["web_dir"] / "index.html")


async def _controller_task(app: web.Application):
    task = asyncio.create_task(app[CONTROLLER].run())
    yield
    task.cancel()
    with contextlib.suppress(asyncio.CancelledError):
        await task
    app[CONTROLLER].release_all()


def create_app(
    injector,
    tokens: TokenStore,
    web_dir: Path,
    limiter: RateLimiter | None = None,
    auth_timeout: float = 3.0,
) -> web.Application:
    app = web.Application(middlewares=[private_only])
    app[CONTROLLER] = InputController(injector)
    app[TOKENS] = tokens
    app[LIMITER] = limiter or RateLimiter()
    app[STATE] = {"active": None, "auth_timeout": auth_timeout, "web_dir": web_dir}
    app.cleanup_ctx.append(_controller_task)
    app.on_response_prepare.append(_no_cache)
    app.router.add_get("/ws", ws_handler)
    app.router.add_get("/", _index)
    app.router.add_static("/", web_dir, show_index=False)
    return app
