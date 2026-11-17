// Run with: node --test tests/accel.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { CURVE, createAccel, gainAt } from "../web/accel.js";

const speeds = [0, 0.05, 0.15, 0.3, 0.6, 1, 1.5, 2, 3, 4, 10];

test("strength 0 is exactly linear at every speed", () => {
  for (const v of speeds) assert.equal(gainAt(v, 0), 1);
  const a = createAccel(() => 0);
  for (let i = 0; i < 20; i++) assert.equal(a.gain(30, 0, i * 4), 1);
});

test("slow movement is damped, fast movement is boosted, and the range is bounded", () => {
  for (const s of [0.25, 0.5, 1]) {
    const lo = 1 - CURVE.slowGainDrop * s;
    const hi = 1 + CURVE.fastGainBoost * s;
    assert.ok(gainAt(0.01, s) < 1);
    assert.ok(gainAt(10, s) > 1);
    for (const v of speeds) {
      const g = gainAt(v, s);
      assert.ok(g >= lo - 1e-12 && g <= hi + 1e-12, `gain ${g} at v=${v}, s=${s}`);
    }
    assert.ok(Math.abs(gainAt(0, s) - lo) < 1e-12);
    assert.ok(Math.abs(gainAt(100, s) - hi) < 1e-12);
  }
});

test("gain never decreases as the finger speeds up", () => {
  for (const s of [0.1, 0.5, 1]) {
    let prev = -Infinity;
    for (let v = 0; v <= 5; v += 0.01) {
      const g = gainAt(v, s);
      assert.ok(g >= prev - 1e-12, `drop at v=${v}`);
      prev = g;
    }
  }
});

test("stronger settings give stronger boost at high speed and more damping at low speed", () => {
  assert.ok(gainAt(3, 1) > gainAt(3, 0.5) && gainAt(3, 0.5) > gainAt(3, 0.25));
  assert.ok(gainAt(0.05, 1) < gainAt(0.05, 0.5) && gainAt(0.05, 0.5) < gainAt(0.05, 0.25));
});

test("a normal walking-speed stroke is left close to 1x at the default strength", () => {
  const g = gainAt(0.8, 0.5);
  assert.ok(g > 0.95 && g < 1.1, `gain ${g}`);
});

function stroke(accel, pxPerMs, steps = 30, dtMs = 4, t0 = 0) {
  let t = t0;
  let g = 1;
  for (let i = 0; i < steps; i++) {
    t += dtMs;
    g = accel.gain(pxPerMs * dtMs, 0, t);
  }
  return g;
}

test("a fast stroke ends with a higher gain than a slow one", () => {
  const slow = stroke(createAccel(() => 0.5), 0.1);
  const fast = stroke(createAccel(() => 0.5), 4);
  assert.ok(slow < 1 && fast > 2, `slow ${slow}, fast ${fast}`);
});

test("speed is averaged over a window: one odd event does not jump to full boost", () => {
  const a = createAccel(() => 1);
  stroke(a, 0.2, 20, 4); // slow stroke
  const g = a.gain(40, 0, 20 * 4 + 4); // single 10 px/ms spike
  const full = gainAt(10, 1);
  assert.ok(g < full - 0.3, `gain ${g} too close to ${full}`);
});

test("a pause starts a new stroke and reset() forgets the speed", () => {
  const a = createAccel(() => 0.5);
  const fast = stroke(a, 4); // high speed estimate
  const afterPause = a.gain(1, 0, 4 * 30 + 500); // 1 px, 500 ms later
  assert.ok(afterPause < fast, "speed history must not leak across a pause");
  const b = createAccel(() => 0.5);
  stroke(b, 4);
  b.reset();
  assert.ok(b.gain(1, 0, 130) < 1);
});

test("same-millisecond events and bad timestamps are handled", () => {
  const a = createAccel(() => 0.5);
  a.gain(3, 0, 100);
  for (const t of [100, 100, 99, NaN]) {
    const g = a.gain(3, 0, t);
    assert.ok(Number.isFinite(g) && g > 0.5 && g < 3, `gain ${g} for t=${t}`);
  }
});

test("setting changes apply immediately", () => {
  let strength = 0;
  const a = createAccel(() => strength);
  assert.equal(stroke(a, 4), 1);
  strength = 1;
  assert.ok(stroke(a, 4, 30, 4, 1000) > 3);
});

test("batched events with uneven timestamps give the same gain as evenly spaced ones", () => {
  const even = createAccel(() => 0.5);
  const batched = createAccel(() => 0.5);
  let ge = 1;
  let gb = 1;
  // Same motion (3 px/ms): evenly every 4 ms vs. alternating 1 ms / 7 ms delivery.
  let t = 0;
  for (let i = 0; i < 24; i++) {
    t += 4;
    ge = even.gain(12, 0, t);
  }
  t = 0;
  for (let i = 0; i < 24; i++) {
    t += i % 2 === 0 ? 1 : 7;
    gb = batched.gain(12, 0, t);
  }
  assert.ok(Math.abs(ge - gb) < 0.35, `even ${ge} vs batched ${gb}`);
});

test("a sustained flick reaches the full boost within about 25 ms", () => {
  const a = createAccel(() => 1);
  let g = 1;
  for (let i = 1; i <= 7; i++) g = a.gain(16, 0, i * 4); // 4 px/ms
  assert.ok(g > 3.9, `gain ${g}`);
});
