import asyncio

import aiohttp

from agent.protocol import T_PONG, encode_move, encode_ping, encode_pong, parse_binary
from agent.stats import percentile
from tests.test_server import _auth, _start, run
from tools.latency_test import run_test


def test_percentile_nearest_rank():
    assert percentile([], 50) == 0.0
    assert percentile([5, 1, 4, 2, 3], 50) == 3
    assert percentile([5, 1, 4, 2, 3], 95) == 5
    assert percentile(list(range(1, 101)), 99) == 99
    assert percentile([7], 99) == 7


def test_ping_pong_protocol():
    assert parse_binary(encode_ping(0xDEADBEEF)) == ("ping", 0xDEADBEEF)
    assert parse_binary(b"\x06\x00") is None
    assert encode_pong(1)[0] == T_PONG
    assert encode_pong(2**32 + 5) == encode_pong(5)  # truncated to u32


def test_server_echoes_ping_token_as_pong(tmp_path):
    async def go():
        srv, inj, token = await _start(tmp_path)
        async with aiohttp.ClientSession() as s, s.ws_connect(srv.make_url("/ws")) as ws:
            await _auth(ws, token)
            await ws.send_bytes(encode_ping(12345))
            msg = await ws.receive(timeout=2)
            assert msg.type == aiohttp.WSMsgType.BINARY
            assert msg.data == encode_pong(12345)
            # ping does not disturb input handling
            await ws.send_bytes(encode_move(0, 3, 4))
            await ws.send_bytes(encode_ping(1))
            assert (await ws.receive(timeout=2)).data == encode_pong(1)
            await asyncio.sleep(0.05)
        await srv.close()
        return inj

    inj = run(go())
    assert (inj.total_dx, inj.total_dy) == (3, 4)


def test_probe_records_one_sample_per_move_packet(tmp_path):
    async def go():
        from agent import server

        srv, inj, token = await _start(tmp_path)
        samples: list[int] = []
        srv.app[server.CONTROLLER].samples = samples
        async with aiohttp.ClientSession() as s, s.ws_connect(srv.make_url("/ws")) as ws:
            await _auth(ws, token)
            for i in range(50):
                await ws.send_bytes(encode_move(i, 1, 0))
            await asyncio.sleep(0.1)
        await srv.close()
        return samples

    samples = run(go())
    assert len(samples) == 50 and all(x >= 0 for x in samples)


def test_latency_tool_reports_and_preserves_movement():
    report = asyncio.run(run_test(packets=400, rate_hz=2000, burst=4, ping_every=10))
    assert report.total_dx_ok
    assert report.coalescing_ratio >= 1.0
    assert 0 < report.handling_us["p50"] <= report.handling_us["p99"] <= report.handling_us["max"]
    assert report.rtt_us["p50"] > 0
    assert report.handling_us["p99"] < 50_000  # generous: this is a smoke test, not a benchmark
    assert "coalescing" in report.format()
