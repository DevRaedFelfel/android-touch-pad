// Connection behaviour of web/app.js: auth, ping/pong, dead-link detection, reconnect.
// Run with: node --test tests/connection.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { loadApp } from "./helpers/app-harness.mjs";

const OK = { t: "ok", v: 1, screen: { w: 1920, h: 1080 } };

function pongFor(pingFrame) {
  const bytes = Array.from(new Uint8Array(pingFrame));
  bytes[0] = 0x81; // PING (0x06) -> PONG, same token
  return bytes;
}

test("connects, authenticates with the saved token and starts pinging", async () => {
  const app = await loadApp();
  assert.equal(app.sockets.length, 1);
  assert.equal(app.storage.token, "abc"); // saved from the QR link
  const s = app.sockets[0];
  assert.equal(s.url, "ws://laptop:8765/ws");
  s.open();
  assert.deepEqual(JSON.parse(s.sent[0]), { t: "auth", token: "abc", v: 1 });
  s.receiveJson(OK);
  assert.equal(app.status().startsWith("connected"), true);
  assert.equal(new Uint8Array(s.sent[1])[0], 0x06); // first PING goes out immediately
});

test("pong replies produce a latency reading in the status text", async () => {
  const app = await loadApp();
  const s = app.sockets[0];
  s.open();
  s.receiveJson(OK);
  app.clock.advance(8); // 8 ms pass before the pong arrives
  s.receiveBinary(pongFor(s.sent[1]));
  assert.match(app.status(), /connected · 8 ms \(p95 8\)/);
});

test("a silent link is detected within 4 s and a new socket is opened", async () => {
  const app = await loadApp();
  const s1 = app.sockets[0];
  s1.open();
  s1.receiveJson(OK);
  // The agent answers for the first second, then the Wi-Fi drops silently.
  app.clock.advance(5);
  s1.receiveBinary(pongFor(s1.sent[1]));
  const lastReply = app.clock.now();

  let reconnectedAt = null;
  for (let t = 0; t < 6000 && reconnectedAt === null; t += 10) {
    app.clock.advance(10);
    if (app.sockets.length > 1) reconnectedAt = app.clock.now();
  }
  assert.ok(reconnectedAt !== null, "never reconnected");
  assert.ok(reconnectedAt - lastReply <= 4000, `took ${reconnectedAt - lastReply} ms`);
  assert.equal(s1.closeCalls, 1);
  assert.match(app.status(), /reconnecting|connecting/);
});

test("the dead socket's late close event does not cause a second reconnect", async () => {
  const app = await loadApp();
  const s1 = app.sockets[0];
  s1.open();
  s1.receiveJson(OK);
  app.clock.advance(4000); // no replies at all: liveness check fires
  app.clock.advance(300);
  assert.equal(app.sockets.length, 2);
  s1.serverClose(); // the browser finally notices, much later
  app.clock.advance(3000);
  assert.equal(app.sockets.length, 2);
});

test("after a reconnect the app authenticates again and shows connected", async () => {
  const app = await loadApp();
  const s1 = app.sockets[0];
  s1.open();
  s1.receiveJson(OK);
  s1.serverClose(); // clean drop
  app.clock.advance(300);
  assert.equal(app.sockets.length, 2);
  const s2 = app.sockets[1];
  s2.open();
  assert.equal(JSON.parse(s2.sent[0]).token, "abc");
  s2.receiveJson(OK);
  assert.match(app.status(), /^connected/);
});

test("reconnect delay backs off up to 2 s while the agent is unreachable", async () => {
  const app = await loadApp();
  const delays = [];
  let last = 0;
  for (let i = 0; i < 6; i++) {
    const s = app.sockets[app.sockets.length - 1];
    s.serverClose(); // never opened: connection refused
    const before = app.clock.now();
    while (app.sockets.length === i + 1) app.clock.advance(10);
    delays.push(app.clock.now() - before);
    last = delays[delays.length - 1];
  }
  assert.ok(delays[0] <= 260 && delays[0] >= 240, `first delay ${delays[0]}`);
  assert.ok(last <= 2010 && last >= 1990, `capped delay ${last}`);
});

test("a rejected token stops reconnecting and tells the user to pair again", async () => {
  const app = await loadApp();
  const s = app.sockets[0];
  s.open();
  s.receiveJson({ t: "err", reason: "bad_token" });
  assert.match(app.status(), /Pairing failed/);
  s.serverClose();
  app.clock.advance(10_000);
  assert.equal(app.sockets.length, 1);
});

test("without a saved token the app asks for the QR code and does not connect", async () => {
  const app = await loadApp({ href: "http://laptop:8765/" });
  assert.equal(app.sockets.length, 0);
  assert.match(app.status(), /scan the QR code/);
});

test("returning to the app after the link died reconnects immediately", async () => {
  const app = await loadApp();
  const s1 = app.sockets[0];
  s1.open();
  s1.receiveJson(OK);
  app.clock.jump(10_000); // phone was locked: no timers ran, no pong arrived
  app.documentListeners.visibilitychange();
  assert.equal(s1.closeCalls, 1);
  assert.equal(app.sockets.length, 2); // no waiting for a backoff timer
});

test("returning to the app while the link is healthy changes nothing", async () => {
  const app = await loadApp();
  const s1 = app.sockets[0];
  s1.open();
  s1.receiveJson(OK);
  app.clock.advance(500);
  app.documentListeners.visibilitychange();
  assert.equal(s1.closeCalls, 0);
  assert.equal(app.sockets.length, 1);
});

test("the screen-on hint is shown once over plain HTTP", async () => {
  const app = await loadApp();
  const s = app.sockets[0];
  s.open();
  s.receiveJson(OK);
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(app.element("toast").hidden, false);
  assert.equal(app.storage.wakeHintShown, "1");
});
