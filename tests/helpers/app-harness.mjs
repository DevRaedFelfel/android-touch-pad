// Loads web/app.js against a stub DOM, a fake WebSocket and a virtual clock so the
// connection logic (auth, ping, liveness, reconnect) can be tested without a browser.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

const WEB = path.resolve(import.meta.dirname, "..", "..", "web");
let loadCount = 0;

function stubElement() {
  const classes = new Set();
  return {
    listeners: {},
    addEventListener(type, fn) {
      this.listeners[type] = fn; // last one wins; enough for the handlers tests need to fire
    },
    classes,
    classList: {
      toggle: (c, on) => (on === undefined ? !classes.delete(c) && classes.add(c) : on ? classes.add(c) : classes.delete(c)),
      add: (c) => classes.add(c),
      remove: (c) => classes.delete(c),
    },
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

function makeClock() {
  let now = 0;
  let nextId = 1;
  const timers = new Map();
  const add = (fn, ms, every) => {
    const id = nextId++;
    timers.set(id, { fn, at: now + Math.max(0, ms || 0), every: every ? ms : 0 });
    return id;
  };
  return {
    now: () => now,
    setTimeout: (fn, ms) => add(fn, ms, false),
    setInterval: (fn, ms) => add(fn, ms, true),
    clear: (id) => timers.delete(id),
    /** Move time forward WITHOUT running timers, like a locked phone throttling a page. */
    jump(ms) {
      now += ms;
    },
    advance(ms) {
      const end = now + ms;
      for (;;) {
        let next = null;
        for (const [id, t] of timers) if (t.at <= end && (!next || t.at < next.t.at)) next = { id, t };
        if (!next) break;
        now = next.t.at;
        if (next.t.every) next.t.at += next.t.every;
        else timers.delete(next.id);
        next.t.fn();
      }
      now = end;
    },
  };
}

class FakeSocket {
  constructor(url, registry) {
    this.url = url;
    this.readyState = 0;
    this.bufferedAmount = 0;
    this.sent = [];
    this.closeCalls = 0;
    registry.push(this);
  }
  send(data) {
    this.sent.push(data);
  }
  close() {
    this.closeCalls++;
    this.readyState = 2; // CLOSING: a dead link never reaches CLOSED on its own
  }
  // helpers used by tests
  open() {
    this.readyState = 1;
    this.onopen();
  }
  receiveJson(obj) {
    this.onmessage({ data: JSON.stringify(obj) });
  }
  receiveBinary(bytes) {
    this.onmessage({ data: new Uint8Array(bytes).buffer });
  }
  serverClose() {
    this.readyState = 3;
    this.onclose();
  }
}

export async function loadApp({ href = "http://laptop:8765/?t=abc", stored = {} } = {}) {
  const sockets = [];
  const elements = new Map();
  const clock = makeClock();
  const storage = { ...stored };
  const documentListeners = {};

  Object.assign(globalThis, {
    document: {
      getElementById: (id) => {
        if (!elements.has(id)) {
          const el = stubElement();
          // the settings checkboxes: app.js tells them from sliders by their type
          if (/^set-(natural|tapToClick|haptics|leftHanded|showLatency|accelOn|keepAwake)$/.test(id)) el.type = "checkbox";
          elements.set(id, el);
        }
        return elements.get(id);
      },
      createElement: stubElement,
      addEventListener: (type, fn) => (documentListeners[type] = fn),
      hidden: false,
      documentElement: {},
      fullscreenElement: null,
    },
    location: { href, pathname: "/", protocol: "http:", host: "laptop:8765" },
    history: { replaceState() {} },
    localStorage: {
      getItem: (k) => storage[k] ?? null,
      setItem: (k, v) => {
        storage[k] = String(v);
      },
    },
    WebSocket: class extends FakeSocket {
      constructor(url) {
        super(url, sockets);
      }
    },
    setTimeout: clock.setTimeout,
    setInterval: clock.setInterval,
    clearTimeout: clock.clear,
    clearInterval: clock.clear,
    innerWidth: 400,
    innerHeight: 800,
    screen: {},
  });
  Object.defineProperty(globalThis, "navigator", { value: {}, configurable: true });
  Object.defineProperty(globalThis, "performance", { value: { now: clock.now }, configurable: true });

  // Rewrite absolute module imports ("/x.js") to files in web/ and import a temp copy.
  const src = fs
    .readFileSync(path.join(WEB, "app.js"), "utf8")
    .replace(/from "\/(\w+)\.js"/g, (_, name) => `from "${pathToFileURL(path.join(WEB, name + ".js"))}"`);
  const tmp = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "tp-")), `app${loadCount++}.mjs`);
  fs.writeFileSync(tmp, src);
  await import(pathToFileURL(tmp));

  return {
    sockets,
    clock,
    storage,
    documentListeners,
    status: () => elements.get("state").textContent,
    element: (id) => elements.get(id),
  };
}
