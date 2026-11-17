// Run with: node --test tests/latency.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { createLatencyStats, formatStatus, percentile } from "../web/latency.js";
import { packPing, parsePong } from "../web/protocol.js";

test("percentile uses nearest rank", () => {
  const v = [5, 1, 4, 2, 3];
  assert.equal(percentile(v, 50), 3);
  assert.equal(percentile(v, 95), 5);
  assert.equal(percentile(v, 0), 1);
  assert.equal(percentile([], 50), 0);
  assert.deepEqual(v, [5, 1, 4, 2, 3]); // input not mutated
});

test("stats keep only the last N samples", () => {
  const s = createLatencyStats(3);
  [100, 1, 2, 3].forEach((x) => s.add(x));
  assert.equal(s.count(), 3);
  assert.equal(s.median(), 2); // the 100 has been dropped
  s.clear();
  assert.equal(s.count(), 0);
});

test("status text shows median and p95 only when enabled and measured", () => {
  const s = createLatencyStats(100);
  assert.equal(formatStatus("connected", s, true), "connected");
  for (let i = 1; i <= 100; i++) s.add(i);
  assert.equal(formatStatus("connected", s, true), "connected · 50 ms (p95 95)");
  assert.equal(formatStatus("connected", s, false), "connected");
});

test("ping frames carry a u32 token; only PONG frames parse back", () => {
  const ping = new Uint8Array(packPing(0xdeadbeef));
  assert.deepEqual(Array.from(ping), [0x06, 0xef, 0xbe, 0xad, 0xde]);
  const pong = new Uint8Array(ping);
  pong[0] = 0x81;
  assert.equal(parsePong(pong.buffer), 0xdeadbeef);
  assert.equal(parsePong(ping.buffer), null); // a PING is not a PONG
  assert.equal(parsePong(new ArrayBuffer(3)), null);
});
