"""End-to-end tests against a real in-process server using --dry-run injection."""

import asyncio
from pathlib import Path

import aiohttp
from aiohttp.test_utils import TestServer

from agent import server
from agent.auth import RateLimiter, TokenStore
from agent.inject_win import DryRunInjector
from agent.protocol import encode_move

WEB_DIR = Path(__file__).resolve().parent.parent / "web"


def run(coro):
    return asyncio.run(coro)


async def _start(tmp_path, **kw):
    tokens = TokenStore(tmp_path / "c.json")
    token = tokens.create()
    inj = DryRunInjector()
    app = server.create_app(inj, tokens, WEB_DIR, **kw)
    srv = TestServer(app)
    await srv.start_server()
    return srv, inj, token


async def _auth(ws, token, v=1):
    await ws.send_json({"t": "auth", "token": token, "v": v})
    return await ws.receive_json()


def test_good_token_then_moves_are_injected(tmp_path):
    async def go():
        srv, inj, token = await _start(tmp_path)
        async with aiohttp.ClientSession() as s, s.ws_connect(srv.make_url("/ws")) as ws:
            reply = await _auth(ws, token)
            assert reply["t"] == "ok" and reply["screen"] == {"w": 1920, "h": 1080}
            for i in range(100):
                await ws.send_bytes(encode_move(i, 2, -1))
            await ws.send_bytes(b"\xff garbage")  # ignored, must not kill the session
            await ws.send_bytes(encode_move(100, 1, 1))
            await asyncio.sleep(0.1)
        await srv.close()
        return inj

    inj = run(go())
    assert (inj.total_dx, inj.total_dy) == (201, -99)


def test_wrong_token_is_rejected_and_nothing_is_injected(tmp_path):
    async def go():
        srv, inj, _ = await _start(tmp_path)
        async with aiohttp.ClientSession() as s, s.ws_connect(srv.make_url("/ws")) as ws:
            reply = await _auth(ws, "nope")
            assert reply == {"t": "err", "reason": "bad_token"}
            try:
                await ws.send_bytes(encode_move(0, 50, 50))
            except Exception:
                pass
            await asyncio.sleep(0.05)
        await srv.close()
        return inj

    inj = run(go())
    assert inj.total_dx == 0 and inj.move_calls == 0


def test_bad_version_and_malformed_auth(tmp_path):
    async def go():
        srv, _, token = await _start(tmp_path)
        async with aiohttp.ClientSession() as s:
            async with s.ws_connect(srv.make_url("/ws")) as ws:
                assert (await _auth(ws, token, v=99))["reason"] == "bad_version"
            async with s.ws_connect(srv.make_url("/ws")) as ws:
                await ws.send_str("not json")
                assert (await ws.receive_json())["reason"] == "bad_request"
            async with s.ws_connect(srv.make_url("/ws")) as ws:
                await ws.send_bytes(b"\x01")  # binary before auth
                assert (await ws.receive_json())["reason"] == "bad_request"
        await srv.close()

    run(go())


def test_auth_timeout(tmp_path):
    async def go():
        srv, _, _ = await _start(tmp_path, auth_timeout=0.2)
        async with aiohttp.ClientSession() as s, s.ws_connect(srv.make_url("/ws")) as ws:
            assert (await ws.receive_json())["reason"] == "auth_timeout"
        await srv.close()

    run(go())


def test_repeated_bad_tokens_get_rate_limited(tmp_path):
    async def go():
        srv, _, token = await _start(tmp_path, limiter=RateLimiter(max_failures=3, window=60))
        async with aiohttp.ClientSession() as s:
            for _ in range(3):
                async with s.ws_connect(srv.make_url("/ws")) as ws:
                    assert (await _auth(ws, "bad"))["reason"] == "bad_token"
            async with s.ws_connect(srv.make_url("/ws")) as ws:
                assert (await ws.receive_json())["reason"] == "rate_limited"
        await srv.close()

    run(go())


def test_second_client_replaces_the_first(tmp_path):
    async def go():
        srv, _, token = await _start(tmp_path)
        async with aiohttp.ClientSession() as s:
            ws1 = await s.ws_connect(srv.make_url("/ws"))
            assert (await _auth(ws1, token))["t"] == "ok"
            ws2 = await s.ws_connect(srv.make_url("/ws"))
            assert (await _auth(ws2, token))["t"] == "ok"
            msg = await ws1.receive(timeout=2)
            assert msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSED)
            await ws2.close()
        await srv.close()

    run(go())


def test_non_private_remote_ip_gets_403(tmp_path, monkeypatch):
    async def go():
        srv, _, _ = await _start(tmp_path)
        monkeypatch.setattr(server, "is_allowed_ip", lambda ip: False)
        async with aiohttp.ClientSession() as s:
            async with s.get(srv.make_url("/")) as r:
                assert r.status == 403
            try:
                await s.ws_connect(srv.make_url("/ws"))
                raise AssertionError("websocket should have been refused")
            except aiohttp.WSServerHandshakeError as e:
                assert e.status == 403
        await srv.close()

    run(go())


def test_static_files_are_served_without_cache(tmp_path):
    async def go():
        srv, _, _ = await _start(tmp_path)
        async with aiohttp.ClientSession() as s:
            for path in ("/", "/app.js", "/protocol.js", "/style.css", "/manifest.webmanifest"):
                async with s.get(srv.make_url(path)) as r:
                    assert r.status == 200, path
                    assert r.headers["Cache-Control"] == "no-cache"
        await srv.close()

    run(go())
