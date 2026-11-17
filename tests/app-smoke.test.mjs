// Loads web/app.js against a stub DOM to catch load-time errors, such as using a
// variable before its declaration. Run with: node --test tests/app-smoke.test.mjs
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { test } from "node:test";

const WEB = path.resolve(import.meta.dirname, "..", "web");

function stubElement() {
  return {
    addEventListener() {},
    classList: { toggle() {}, add() {}, remove() {} },
    setPointerCapture() {},
    querySelectorAll: () => [],
    appendChild() {},
    dataset: {},
    style: {},
    value: "",
    checked: false,
    hidden: true,
    textContent: "",
    setSelectionRange() {},
    focus() {},
    blur() {},
  };
}

test("app.js loads and connects without throwing", async () => {
  const sockets = [];
  const elements = new Map();
  Object.assign(globalThis, {
    document: {
      getElementById: (id) => {
        if (!elements.has(id)) elements.set(id, stubElement());
        return elements.get(id);
      },
      createElement: stubElement,
      documentElement: {},
      fullscreenElement: null,
    },
    location: { href: "http://laptop:8765/?t=abc", pathname: "/", protocol: "http:", host: "laptop:8765" },
    history: { replaceState() {} },
    localStorage: { d: {}, getItem(k) { return this.d[k] ?? null; }, setItem(k, v) { this.d[k] = v; } },
    WebSocket: class {
      constructor(url) {
        this.url = url;
        this.readyState = 0;
        this.bufferedAmount = 0;
        sockets.push(this);
      }
      send() {}
    },
    innerWidth: 400,
    innerHeight: 800,
    screen: {},
  });
  Object.defineProperty(globalThis, "navigator", { value: {}, configurable: true });

  // Rewrite absolute module imports ("/x.js") to files in web/ and import a temp copy.
  const src = fs
    .readFileSync(path.join(WEB, "app.js"), "utf8")
    .replace(/from "\/(\w+)\.js"/g, (_, name) => `from "${pathToFileURL(path.join(WEB, name + ".js"))}"`);
  const tmp = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "tp-")), "app.mjs");
  fs.writeFileSync(tmp, src);
  await import(pathToFileURL(tmp));

  assert.equal(sockets.length, 1);
  assert.equal(sockets[0].url, "ws://laptop:8765/ws");
  assert.equal(globalThis.localStorage.getItem("token"), "abc"); // token saved from the QR link
});
