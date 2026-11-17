// Run with: node --test tests/
import assert from "node:assert/strict";
import { test } from "node:test";

import { createGestures, VK } from "../web/gestures.js";

function harness(settings = {}) {
  const log = [];
  const sum = { mx: 0, my: 0, sx: 0, sy: 0, z: 0 };
  const out = {
    move: (dx, dy) => {
      sum.mx += dx;
      sum.my += dy;
    },
    scroll: (dx, dy) => {
      sum.sx += dx;
      sum.sy += dy;
    },
    zoom: (d) => {
      sum.z += d;
    },
    button: (b, down) => log.push(["button", b, down]),
    click: (b) => log.push(["click", b]),
    chord: (vks) => log.push(["chord", vks]),
  };
  const s = { tapToClick: true, ...settings };
  return { g: createGestures(out, () => s), log, sum };
}

test("one-finger drag moves the cursor by the full distance", () => {
  const { g, log, sum } = harness();
  g.down(1, 100, 100, 0);
  for (let i = 1; i <= 20; i++) g.move(1, 100 + i * 3, 100 + i, i * 8);
  g.up(1, 200);
  assert.equal(sum.mx, 60);
  assert.equal(sum.my, 20);
  assert.deepEqual(log, []); // moved, so not a tap
});

test("movement inside the dead zone is held, then flushed in full", () => {
  const { g, sum } = harness();
  g.down(1, 0, 0, 0);
  g.move(1, 2, 0, 10);
  g.move(1, 4, 0, 20);
  assert.equal(sum.mx, 0); // still held
  g.move(1, 8, 0, 30); // exceeds the 6px slop
  assert.equal(sum.mx, 8); // nothing lost
});

test("quick tap is a left click", () => {
  const { g, log } = harness();
  g.down(1, 50, 50, 0);
  g.up(1, 100);
  assert.deepEqual(log, [["click", 0]]);
});

test("slow press is not a click", () => {
  const { g, log } = harness();
  g.down(1, 50, 50, 0);
  g.up(1, 600);
  assert.deepEqual(log, []);
});

test("tap-to-click can be turned off", () => {
  const { g, log } = harness({ tapToClick: false });
  g.down(1, 50, 50, 0);
  g.up(1, 100);
  assert.deepEqual(log, []);
});

test("tap then touch-and-drag holds the left button until release", () => {
  const { g, log, sum } = harness();
  g.down(1, 50, 50, 0);
  g.up(1, 80); // tap -> click
  g.down(2, 50, 50, 200); // within 300 ms -> button down
  g.move(2, 60, 50, 220); // dragging: no dead zone
  g.move(2, 90, 50, 240);
  g.up(2, 400);
  assert.deepEqual(log, [["click", 0], ["button", 0, true], ["button", 0, false]]);
  assert.equal(sum.mx, 40);
});

test("a late second tap is a normal tap, not a drag", () => {
  const { g, log } = harness();
  g.down(1, 50, 50, 0);
  g.up(1, 80);
  g.down(2, 50, 50, 600);
  g.up(2, 650);
  assert.deepEqual(log, [["click", 0], ["click", 0]]);
});

test("second finger while dragging releases the held button", () => {
  const { g, log } = harness();
  g.down(1, 50, 50, 0);
  g.up(1, 80);
  g.down(2, 50, 50, 200);
  g.down(3, 150, 50, 220);
  assert.deepEqual(log, [["click", 0], ["button", 0, true], ["button", 0, false]]);
});

test("two-finger drag scrolls by the centroid movement", () => {
  const { g, log, sum } = harness();
  g.down(1, 100, 100, 0);
  g.down(2, 200, 100, 5);
  for (let i = 1; i <= 10; i++) {
    g.move(1, 100, 100 + i * 5, 10 + i * 8);
    g.move(2, 200, 100 + i * 5, 10 + i * 8);
  }
  g.up(1, 200);
  g.up(2, 205);
  assert.ok(Math.abs(sum.sy - 50) < 1e-9, `sy=${sum.sy}`);
  assert.equal(sum.z, 0);
  assert.deepEqual(log, []);
});

test("two-finger tap is a right click", () => {
  const { g, log } = harness();
  g.down(1, 100, 100, 0);
  g.down(2, 200, 100, 10);
  g.up(1, 90);
  g.up(2, 95);
  assert.deepEqual(log, [["click", 1]]);
});

test("slow two-finger hold is not a click", () => {
  const { g, log } = harness();
  g.down(1, 100, 100, 0);
  g.down(2, 200, 100, 10);
  g.up(1, 700);
  g.up(2, 710);
  assert.deepEqual(log, []);
});

test("spreading two fingers zooms in (positive), pinching zooms out", () => {
  const a = harness();
  a.g.down(1, 100, 100, 0);
  a.g.down(2, 200, 100, 5);
  for (let i = 1; i <= 10; i++) {
    a.g.move(1, 100 - i * 4, 100, 10 + i * 8);
    a.g.move(2, 200 + i * 4, 100, 10 + i * 8);
  }
  assert.ok(a.sum.z > 70, `zoom=${a.sum.z}`);
  assert.ok(Math.abs(a.sum.sx) < 1e-9 && Math.abs(a.sum.sy) < 1e-9);

  const b = harness();
  b.g.down(1, 100, 100, 0);
  b.g.down(2, 300, 100, 5);
  for (let i = 1; i <= 10; i++) {
    b.g.move(1, 100 + i * 5, 100, 10 + i * 8);
    b.g.move(2, 300 - i * 5, 100, 10 + i * 8);
  }
  assert.ok(b.sum.z < -70, `zoom=${b.sum.z}`);
});

test("the leftover finger after a two-finger gesture does not move the cursor", () => {
  const { g, sum } = harness();
  g.down(1, 100, 100, 0);
  g.down(2, 200, 100, 5);
  g.move(1, 100, 140, 20);
  g.move(2, 200, 140, 20);
  g.up(2, 100);
  const before = { ...sum };
  g.move(1, 100, 300, 120);
  assert.deepEqual(sum, before);
  g.up(1, 150);
  // all fingers are up: a new touch works again
  g.down(3, 10 + 50, 10 + 50, 1000);
  g.move(3, 200, 50, 1010);
  assert.ok(sum.mx > 0);
});

function three(dx, dy, dt, ids = [1, 2, 3]) {
  const h = harness();
  const pts = [[100, 100], [150, 100], [200, 100]];
  ids.forEach((id, i) => h.g.down(id, pts[i][0], pts[i][1], i));
  const steps = 6;
  for (let s = 1; s <= steps; s++) {
    ids.forEach((id, i) => h.g.move(id, pts[i][0] + (dx * s) / steps, pts[i][1] + (dy * s) / steps, (dt * s) / steps));
  }
  ids.forEach((id) => h.g.up(id, dt));
  return h.log;
}

test("three-finger tap is a middle click", () => {
  assert.deepEqual(three(0, 0, 150), [["click", 2]]);
});

test("three-finger swipes send the documented shortcuts", () => {
  assert.deepEqual(three(0, -120, 300), [["chord", [VK.LWIN, VK.TAB]]]);
  assert.deepEqual(three(0, 120, 300), [["chord", [VK.LWIN, VK.D]]]);
  assert.deepEqual(three(-120, 0, 300), [["chord", [VK.CONTROL, VK.LWIN, VK.LEFT]]]);
  assert.deepEqual(three(120, 0, 300), [["chord", [VK.CONTROL, VK.LWIN, VK.RIGHT]]]);
});

test("short or slow three-finger movement is ignored", () => {
  assert.deepEqual(three(0, -30, 300), []);
  assert.deepEqual(three(0, -120, 900), []);
});

test("a fourth finger is ignored", () => {
  const { g, log } = harness();
  g.down(1, 0, 0, 0);
  g.down(2, 50, 0, 1);
  g.down(3, 100, 0, 2);
  g.down(4, 150, 0, 3);
  g.up(4, 10);
  g.up(1, 100);
  g.up(2, 101);
  g.up(3, 102);
  assert.deepEqual(log, [["click", 2]]);
});

test("cancel and reset release a held drag button", () => {
  const a = harness();
  a.g.down(1, 0, 0, 0);
  a.g.up(1, 50);
  a.g.down(2, 0, 0, 100);
  a.g.cancel(2);
  assert.deepEqual(a.log.slice(-1), [["button", 0, false]]);

  const b = harness();
  b.g.down(1, 0, 0, 0);
  b.g.up(1, 50);
  b.g.down(2, 0, 0, 100);
  b.g.reset();
  assert.deepEqual(b.log.slice(-1), [["button", 0, false]]);
});
