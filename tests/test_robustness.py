"""M5: dead-link watchdog, blocked-input warning, optional HTTPS."""

import asyncio
import ssl
from pathlib import Path

import aiohttp
import pytest
from aiohttp import web

from agent import server
from agent.auth import TokenStore
from agent.inject_win import DryRunInjector
from agent.protocol import encode_button, encode_key, encode_ping, encode_pong
from tests.test_server import WEB_DIR, _auth, _start, run


# ---------- idle watchdog ----------


def test_silent_client_is_dropped_and_its_input_released(tmp_path):
    async def go():
        srv, inj, token = await _start(tmp_path, idle_timeout=0.6)
        async with aiohttp.ClientSession() as s:
            ws = await s.ws_connect(srv.make_url("/ws"))
            await _auth(ws, token)
            await ws.send_bytes(encode_button(0, 1))  # mouse button held ...
            await ws.send_bytes(encode_key(0x11, 1))  # ... and Ctrl held
            await asyncio.sleep(0.2)
            assert inj.buttons_down == {0} and inj.keys_down == {0x11}
            # ... then the phone goes silent (Wi-Fi lost). Nothing more is sent.
            await asyncio.sleep(1.6)
            released = (inj.buttons_down, inj.keys_down)
            msg = await ws.receive(timeout=3)  # the agent closes its side
            closed = msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSED)
        await srv.close()
        return released, closed

    (buttons, keys), closed = run(go())
    assert buttons == set() and keys == set()
    assert closed


def test_pinging_client_is_not_dropped(tmp_path):
    async def go():
        srv, inj, token = await _start(tmp_path, idle_timeout=0.6)
        async with aiohttp.ClientSession() as s, s.ws_connect(srv.make_url("/ws")) as ws:
            await _auth(ws, token)
            await ws.send_bytes(encode_button(0, 1))
            for i in range(10):  # 1.5 s of regular pings, longer than the idle timeout
                await ws.send_bytes(encode_ping(i))
                assert (await ws.receive(timeout=2)).data == encode_pong(i)
                await asyncio.sleep(0.15)
            still_held = inj.buttons_down == {0}
            await ws.close()
        await srv.close()
        return still_held

    assert run(go())


def test_client_can_reconnect_after_being_dropped(tmp_path):
    async def go():
        srv, inj, token = await _start(tmp_path, idle_timeout=0.5)
        async with aiohttp.ClientSession() as s:
            ws1 = await s.ws_connect(srv.make_url("/ws"))
            await _auth(ws1, token)
            await asyncio.sleep(1.4)  # dropped for silence
            ws2 = await s.ws_connect(srv.make_url("/ws"))
            reply = await _auth(ws2, token)
            await ws2.send_bytes(encode_ping(7))
            pong = await ws2.receive(timeout=2)
            await ws2.close()
        await srv.close()
        return reply, pong.data

    reply, pong = run(go())
    assert reply["t"] == "ok" and pong == encode_pong(7)


# ---------- blocked SendInput is reported once ----------


class _BlockedUser32:
    def __init__(self):
        self.SendInput = lambda n, arg, size: 0  # Windows refused everything
        self.MapVirtualKeyW = lambda vk, kind: 0
        self.GetSystemMetrics = lambda i: 100


def test_blocked_input_is_logged_once(monkeypatch, caplog):
    import ctypes

    import agent.inject_win as mod

    monkeypatch.setattr(mod.sys, "platform", "win32")
    monkeypatch.setattr(ctypes, "windll", type("W", (), {"user32": _BlockedUser32()}), raising=False)
    inj = mod.WindowsInjector()
    with caplog.at_level("WARNING", logger="touchpad"):
        inj.move(1, 1)
        inj.button(0, True)
        inj.key(0x41, True)
    warnings = [r for r in caplog.records if "blocked" in r.getMessage()]
    assert len(warnings) == 1
    assert "administrator" in warnings[0].getMessage()


# ---------- HTTPS ----------


def test_cert_is_created_reused_and_renewed_for_new_addresses(tmp_path):
    from cryptography import x509

    from agent.tls import ensure_cert

    cert, key = ensure_cert(tmp_path, ["192.168.1.20"])
    first = cert.read_bytes()
    parsed = x509.load_pem_x509_certificate(first)
    san = parsed.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
    assert {str(i) for i in san.get_values_for_type(x509.IPAddress)} == {"192.168.1.20", "127.0.0.1"}
    assert "localhost" in san.get_values_for_type(x509.DNSName)

    assert ensure_cert(tmp_path, ["192.168.1.20"]) == (cert, key)
    assert cert.read_bytes() == first  # reused, not regenerated

    ensure_cert(tmp_path, ["192.168.1.20", "10.0.0.7"])  # new address (e.g. hotspot)
    assert cert.read_bytes() != first
    san = x509.load_pem_x509_certificate(cert.read_bytes()).extensions.get_extension_for_class(
        x509.SubjectAlternativeName
    ).value
    assert "10.0.0.7" in {str(i) for i in san.get_values_for_type(x509.IPAddress)}


def test_corrupt_cert_is_replaced(tmp_path):
    from agent.tls import ensure_cert

    cert, key = ensure_cert(tmp_path, ["192.168.1.20"])
    cert.write_text("garbage")
    ensure_cert(tmp_path, ["192.168.1.20"])
    assert cert.read_text().startswith("-----BEGIN CERTIFICATE-----")


@pytest.mark.skipif(not hasattr(__import__("os"), "chmod") or __import__("os").name == "nt", reason="POSIX permissions")
def test_private_key_is_not_world_readable(tmp_path):
    from agent.tls import ensure_cert

    _, key = ensure_cert(tmp_path, ["192.168.1.20"])
    assert (key.stat().st_mode & 0o077) == 0


def test_https_websocket_end_to_end(tmp_path):
    from agent.tls import ensure_cert, make_ssl_context

    async def go():
        cert, key = ensure_cert(tmp_path / "tls", ["127.0.0.1"])
        tokens = TokenStore(tmp_path / "c.json")
        token = tokens.create()
        inj = DryRunInjector()
        app = server.create_app(inj, tokens, WEB_DIR)
        runner = web.AppRunner(app, access_log=None)
        await runner.setup()
        await web.TCPSite(runner, "127.0.0.1", 0, ssl_context=make_ssl_context(cert, key)).start()
        port = runner.addresses[0][1]

        # Trust only our own certificate: a real TLS handshake, verification ON.
        client_ctx = ssl.create_default_context(cafile=str(cert))
        async with aiohttp.ClientSession(connector=aiohttp.TCPConnector(ssl=client_ctx)) as s:
            async with s.get(f"https://127.0.0.1:{port}/") as r:
                assert r.status == 200
            async with s.ws_connect(f"https://127.0.0.1:{port}/ws") as ws:
                assert (await _auth(ws, token))["t"] == "ok"
                await ws.send_bytes(encode_ping(3))
                assert (await ws.receive(timeout=2)).data == encode_pong(3)
        await runner.cleanup()

    run(go())
