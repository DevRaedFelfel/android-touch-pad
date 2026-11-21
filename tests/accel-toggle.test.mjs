// Acceleration on/off switch and the keep-awake pings (web/app.js). Run with: node --test tests/accel-toggle.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { loadApp } from "./helpers/app-harness.mjs";

const OK = { t: "ok", v: 1, screen: { w: 1920, h: 1080 } };

async function connected(settings) {
  const app = await loadApp({ stored: { settings: JSON.stringify({ sensitivity: 1, ...settings }) } });
  const s = app.sockets[0];
  s.open();
  s.receiveJson(OK);
  const frames = [];
  s.send = (d) => frames.push(d instanceof ArrayBuffer ? d.slice(0) : d);
  return { app, s, frames };
}

const moves = (frames) =>
  frames
    .filter((d) => d instanceof ArrayBuffer && new Uint8Array(d)[0] === 0x01)
    .map((d) => ({ dx: new DataView(d).getInt16(3, true), dy: new DataView(d).getInt16(5, true) }));
const total = (m) => m.reduce((a, x) => ({ x: a.x + x.dx, y: a.y + x.dy }), { x: 0, y: 0 });

/** A fast flick: 600 px in 20 steps of 4 ms (150 px/s * 1000 = very fast). */
function flick(app) {
  const pad = app.element("pad").listeners;
  let t = 1000;
  pad.pointerdown({ pointerId: 1, clientX: 100, clientY: 300, timeStamp: t });
  for (let i = 1; i <= 20; i++) {
    t += 4;
    pad.pointermove({ pointerId: 1, clientX: 100 + i * 30, clientY: 300, timeStamp: t });
  }
  pad.pointerup({ pointerId: 1, timeStamp: t + 4 });
}

test("acceleration on: a flick travels farther than the finger", async () => {
  const { app, frames } = await connected({ accelOn: true, accel: 1 });
  flick(app);
  assert.ok(total(moves(frames)).x > 600 * 1.5, JSON.stringify(total(moves(frames))));
});

test("acceleration off: movement is exactly linear whatever the strength slider says", async () => {
  const { app, frames } = await connected({ accelOn: false, accel: 1 });
  flick(app);
  const t = total(moves(frames));
  // 600 px of finger travel; the 6 px dead zone is flushed with the first move, so the total is the full distance
  assert.ok(Math.abs(t.x - 600) <= 2, JSON.stringify(t));
});

test("the strength slider is disabled while acceleration is off, and follows the checkbox live", async () => {
  const { app } = await connected({ accelOn: false });
  assert.equal(app.element("set-accel").disabled, true);
  const box = app.element("set-accelOn");
  box.checked = true;
  box.listeners.input();
  assert.equal(app.element("set-accel").disabled, false);
  assert.equal(JSON.parse(app.storage.settings).accelOn, true);
  box.checked = false;
  box.listeners.input();
  assert.equal(app.element("set-accel").disabled, true);
});

test("keep-awake sends a small ping every 150 ms, which does not disturb the latency readout", async () => {
  const { app, frames } = await connected({ keepAwake: true });
  const pings = () => frames.filter((d) => d instanceof ArrayBuffer && new Uint8Array(d)[0] === 0x06 && new DataView(d).getUint32(1, true) === 0xffffffff).length;
  app.clock.advance(1000);
  const n = pings();
  assert.ok(n >= 6 && n <= 7, "pings in one second: " + n);
});

test("keep-awake can be switched off", async () => {
  const { app, frames } = await connected({ keepAwake: false });
  app.clock.advance(1000);
  const warm = frames.filter((d) => d instanceof ArrayBuffer && new Uint8Array(d)[0] === 0x06 && new DataView(d).getUint32(1, true) === 0xffffffff);
  assert.equal(warm.length, 0);
});
