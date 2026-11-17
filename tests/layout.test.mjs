// Layout switch: coordinate mapping and its wiring in web/app.js (no reconnect needed).
// Run with: node --test tests/layout.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { LAYOUTS, isLayout, mapPoint, nextLayout } from "../web/layout.js";
import { loadApp } from "./helpers/app-harness.mjs";

const W = 400;
const H = 800;

test("normal layout leaves points alone", () => {
  assert.deepEqual(mapPoint("normal", 120, 340, W, H), { x: 120, y: 340 });
});

test("wide layouts turn the long side of the phone into x", () => {
  // Phone turned left (UI turned clockwise): finger sliding down the screen moves right.
  const a = mapPoint("wide-left", 200, 100, W, H);
  const b = mapPoint("wide-left", 200, 300, W, H);
  assert.deepEqual({ dx: b.x - a.x, dy: b.y - a.y }, { dx: 200, dy: 0 });
  // Finger sliding toward the screen's right edge moves the pad's y up (screen right = UI up).
  const c = mapPoint("wide-left", 300, 100, W, H);
  assert.deepEqual({ dx: c.x - a.x, dy: c.y - a.y }, { dx: 0, dy: -100 });
  // Phone turned right is the mirror image.
  const d = mapPoint("wide-right", 200, 100, W, H);
  const e = mapPoint("wide-right", 200, 300, W, H);
  assert.deepEqual({ dx: e.x - d.x, dy: e.y - d.y }, { dx: -200, dy: 0 });
  const f = mapPoint("wide-right", 300, 100, W, H);
  assert.deepEqual({ dx: f.x - d.x, dy: f.y - d.y }, { dx: 0, dy: 100 });
});

test("mapped points stay inside the rotated pad", () => {
  for (const layout of ["wide-left", "wide-right"]) {
    for (const [x, y] of [[0, 0], [W, 0], [0, H], [W, H], [123, 456]]) {
      const p = mapPoint(layout, x, y, W, H);
      assert.ok(p.x >= 0 && p.x <= H && p.y >= 0 && p.y <= W, `${layout} ${x},${y} -> ${p.x},${p.y}`);
    }
  }
});

test("cycle visits every layout and recovers from junk", () => {
  assert.deepEqual(LAYOUTS, ["normal", "wide-left", "wide-right"]);
  assert.equal(nextLayout("normal"), "wide-left");
  assert.equal(nextLayout("wide-left"), "wide-right");
  assert.equal(nextLayout("wide-right"), "normal");
  assert.equal(nextLayout("garbage"), "normal"); // junk restarts the cycle at normal
  assert.ok(isLayout("wide-right") && !isLayout("diagonal") && !isLayout(undefined));
});

// ---- wiring in app.js ----
const OK = { t: "ok", v: 1, screen: { w: 1920, h: 1080 } };

async function connected(settings) {
  const app = await loadApp({ stored: { settings: JSON.stringify({ sensitivity: 1, accel: 0, ...settings }) } });
  const s = app.sockets[0];
  s.open();
  s.receiveJson(OK);
  const moves = [];
  s.send = (d) => {
    if (d instanceof ArrayBuffer && new Uint8Array(d)[0] === 0x01) {
      const v = new DataView(d.slice(0));
      moves.push({ dx: v.getInt16(3, true), dy: v.getInt16(5, true) });
    }
  };
  return { app, s, moves };
}

/** One finger from (x0,y0) to (x1,y1) in small steps, the way a real drag arrives. */
function drag(app, [x0, y0], [x1, y1], steps = 20) {
  const pad = app.element("pad").listeners;
  let t = 1000;
  pad.pointerdown({ pointerId: 1, clientX: x0, clientY: y0, timeStamp: t });
  for (let i = 1; i <= steps; i++) {
    t += 8;
    pad.pointermove({ pointerId: 1, clientX: x0 + ((x1 - x0) * i) / steps, clientY: y0 + ((y1 - y0) * i) / steps, timeStamp: t });
  }
  pad.pointerup({ pointerId: 1, timeStamp: t + 8 });
}

const total = (moves) => moves.reduce((a, m) => ({ x: a.x + m.dx, y: a.y + m.dy }), { x: 0, y: 0 });

test("normal layout: dragging right moves the cursor right", async () => {
  const { app, moves } = await connected({});
  drag(app, [150, 300], [250, 300]);
  const t = total(moves);
  assert.ok(t.x > 80 && Math.abs(t.y) < 3, JSON.stringify(t));
});

test("wide-left: dragging down the screen moves the cursor right", async () => {
  const { app, moves } = await connected({ layout: "wide-left" });
  drag(app, [200, 200], [200, 300]);
  const t = total(moves);
  assert.ok(t.x > 80 && Math.abs(t.y) < 3, JSON.stringify(t));
});

test("wide-right: dragging down the screen moves the cursor left", async () => {
  const { app, moves } = await connected({ layout: "wide-right" });
  drag(app, [200, 200], [200, 300]);
  const t = total(moves);
  assert.ok(t.x < -80 && Math.abs(t.y) < 3, JSON.stringify(t));
});

test("switching layout keeps the same connection and takes effect at once", async () => {
  const { app, s, moves } = await connected({});
  const btn = app.element("btn-layout").listeners;
  btn.click(); // normal -> wide-left
  assert.equal(app.sockets.length, 1);
  assert.equal(s.closeCalls, 0);
  assert.ok(app.element("app").classes.has("wide-left"));
  assert.equal(JSON.parse(app.storage.settings).layout, "wide-left");
  drag(app, [200, 200], [200, 300]);
  assert.ok(total(moves).x > 80);
  btn.click(); // -> wide-right
  btn.click(); // -> normal
  assert.equal(app.element("app").classes.has("wide-left"), false);
  assert.equal(app.element("app").classes.has("wide-right"), false);
  assert.equal(app.sockets.length, 1);
});

test("a corrupt saved layout falls back to normal", async () => {
  const { app, moves } = await connected({ layout: "sideways" });
  drag(app, [150, 300], [250, 300]);
  assert.ok(total(moves).x > 80);
});

test("switching mid-touch drops the half-finished gesture instead of jumping", async () => {
  const { app, moves } = await connected({});
  const pad = app.element("pad").listeners;
  pad.pointerdown({ pointerId: 1, clientX: 150, clientY: 300, timeStamp: 1000 });
  for (let i = 1; i <= 5; i++) pad.pointermove({ pointerId: 1, clientX: 150 + i * 6, clientY: 300, timeStamp: 1000 + i * 8 });
  const before = total(moves);
  app.element("btn-layout").listeners.click();
  pad.pointermove({ pointerId: 1, clientX: 190, clientY: 300, timeStamp: 1100 });
  pad.pointerup({ pointerId: 1, timeStamp: 1110 });
  const after = total(moves);
  assert.ok(Math.abs(after.x - before.x) < 3 && Math.abs(after.y - before.y) < 3, JSON.stringify({ before, after }));
});
