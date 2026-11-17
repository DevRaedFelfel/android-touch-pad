// Run with: node --test tests/keyboard.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { SENTINEL, VK, charToKey, createEditTracker, diffEdit, namedKeyToVk } from "../web/keyboard.js";
import { packText } from "../web/protocol.js";

test("diffEdit: append, delete and replace", () => {
  assert.deepEqual(diffEdit("ab", "abc"), { backspaces: 0, text: "c" });
  assert.deepEqual(diffEdit("abc", "ab"), { backspaces: 1, text: "" });
  assert.deepEqual(diffEdit("teh", "the"), { backspaces: 2, text: "he" });
  assert.deepEqual(diffEdit("abc", "abc"), { backspaces: 0, text: "" });
  assert.deepEqual(diffEdit("", "héllo 😀"), { backspaces: 0, text: "héllo 😀" });
});

// Drives the tracker the way the page does: forward the edit, then reset the field.
function typeSession() {
  const t = createEditTracker();
  let value = SENTINEL;
  const sent = []; // what the laptop would receive
  const apply = (newValue, composing) => {
    value = newValue;
    const r = t.update(value, composing);
    for (let i = 0; i < r.backspaces; i++) sent.push("<BS>");
    if (r.text) sent.push(r.text);
    if (r.reset) value = SENTINEL;
  };
  return { apply, sent, value: () => value };
}

test("typing a word through an IME composition forwards each edit once", () => {
  const s = typeSession();
  s.apply(SENTINEL + "h", true);
  s.apply(SENTINEL + "he", true);
  s.apply(SENTINEL + "hel", true);
  s.apply(SENTINEL + "hello", true); // suggestion extends the word
  assert.deepEqual(s.sent, ["h", "e", "l", "lo"]); // each character sent exactly once
  // The laptop must end up with exactly "hello": simulate it.
  let screen = "";
  for (const item of s.sent) screen = item === "<BS>" ? screen.slice(0, -1) : screen + item;
  assert.equal(screen, "hello");
});

test("committing a word with a space resets the field to the sentinel", () => {
  const s = typeSession();
  s.apply(SENTINEL + "hi", true);
  s.apply(SENTINEL + "hi ", false);
  assert.equal(s.value(), SENTINEL);
  assert.equal(s.sent.join(""), "hi ");
});

test("Backspace on an empty field is seen as a deleted sentinel character", () => {
  const s = typeSession();
  s.apply(SENTINEL.slice(0, -1), false);
  assert.deepEqual(s.sent, ["<BS>"]);
  assert.equal(s.value(), SENTINEL); // refilled so the next Backspace works too
  s.apply(SENTINEL.slice(0, -1), false);
  assert.deepEqual(s.sent, ["<BS>", "<BS>"]);
});

test("autocorrect that rewrites the whole word becomes backspaces plus new text", () => {
  const s = typeSession();
  s.apply(SENTINEL + "teh", true);
  s.apply(SENTINEL + "the", true);
  let screen = "";
  for (const item of s.sent) screen = item === "<BS>" ? screen.slice(0, -1) : screen + item;
  assert.equal(screen, "the");
});

test("charToKey maps letters, digits and space for shortcuts", () => {
  assert.deepEqual(charToKey("c"), { vk: 0x43, shift: false });
  assert.deepEqual(charToKey("C"), { vk: 0x43, shift: true });
  assert.deepEqual(charToKey("7"), { vk: 0x37, shift: false });
  assert.deepEqual(charToKey(" "), { vk: VK.SPACE, shift: false });
  assert.equal(charToKey("é"), null);
  assert.equal(charToKey("!"), null);
  assert.equal(charToKey("ab"), null);
});

test("namedKeyToVk covers navigation keys", () => {
  assert.equal(namedKeyToVk("Enter"), VK.ENTER);
  assert.equal(namedKeyToVk("Backspace"), VK.BACK);
  assert.equal(namedKeyToVk("ArrowLeft"), VK.LEFT);
  assert.equal(namedKeyToVk("a"), null);
});

test("packText encodes UTF-8 behind the 0x05 type byte and splits long text", () => {
  const [one] = packText("héllo");
  const bytes = new Uint8Array(one);
  assert.equal(bytes[0], 0x05);
  assert.equal(new TextDecoder().decode(bytes.slice(1)), "héllo");

  const frames = packText("😀".repeat(2000)); // 8000 bytes
  assert.ok(frames.length >= 2);
  for (const f of frames) assert.ok(f.byteLength - 1 <= 4096);
  const joined = frames.map((f) => new TextDecoder().decode(new Uint8Array(f).slice(1))).join("");
  assert.equal(joined, "😀".repeat(2000)); // no character split across frames
});
