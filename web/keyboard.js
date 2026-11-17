// Keyboard helpers (SPEC.md section 6.4). Everything here is pure so it can be tested in Node.

// Windows virtual-key codes used by the special-keys panel.
export const VK = {
  BACK: 0x08,
  TAB: 0x09,
  ENTER: 0x0d,
  SHIFT: 0x10,
  CTRL: 0x11,
  ALT: 0x12,
  ESC: 0x1b,
  SPACE: 0x20,
  PGUP: 0x21,
  PGDN: 0x22,
  END: 0x23,
  HOME: 0x24,
  LEFT: 0x25,
  UP: 0x26,
  RIGHT: 0x27,
  DOWN: 0x28,
  DEL: 0x2e,
  WIN: 0x5b,
  F1: 0x70,
};

/** Modifiers that can be latched ("sticky") from the panel. */
export const MODIFIERS = [VK.SHIFT, VK.CTRL, VK.ALT, VK.WIN];

/**
 * Android soft keyboards cannot report Backspace on an empty field, so the hidden
 * input always holds a short sentinel; deleting a sentinel character is how we see
 * a Backspace.
 */
export const SENTINEL = "  ";

/** Smallest edit that turns `prev` into `cur`: how many characters to delete, then what to type. */
export function diffEdit(prev, cur) {
  const m = Math.min(prev.length, cur.length);
  let i = 0;
  while (i < m && prev[i] === cur[i]) i++;
  return { backspaces: prev.length - i, text: cur.slice(i) };
}

/**
 * Tracks the hidden input's value. Call update() on every `input` event with the new
 * value and whether an IME composition is active. It returns the edit to forward and
 * whether the caller should reset the field back to the sentinel.
 */
export function createEditTracker(sentinel = SENTINEL) {
  let prev = sentinel;
  return {
    update(cur, composing) {
      const edit = diffEdit(prev, cur);
      prev = cur;
      const reset = !composing && cur !== sentinel;
      if (reset) prev = sentinel;
      return { ...edit, reset };
    },
    reset() {
      prev = sentinel;
    },
  };
}

/** Virtual-key for characters that matter when a modifier is held (Ctrl+C, Alt+F4...). */
export function charToKey(ch) {
  if (ch.length !== 1) return null;
  const c = ch.charCodeAt(0);
  if (c >= 0x61 && c <= 0x7a) return { vk: c - 0x20, shift: false }; // a-z
  if (c >= 0x41 && c <= 0x5a) return { vk: c, shift: true }; // A-Z
  if (c >= 0x30 && c <= 0x39) return { vk: c, shift: false }; // 0-9
  if (c === 0x20) return { vk: VK.SPACE, shift: false };
  return null;
}

/** Map a hardware-keyboard `KeyboardEvent.key` to a virtual-key, or null. */
export function namedKeyToVk(key) {
  switch (key) {
    case "Enter": return VK.ENTER;
    case "Backspace": return VK.BACK;
    case "Tab": return VK.TAB;
    case "Escape": return VK.ESC;
    case "Delete": return VK.DEL;
    case "ArrowLeft": return VK.LEFT;
    case "ArrowRight": return VK.RIGHT;
    case "ArrowUp": return VK.UP;
    case "ArrowDown": return VK.DOWN;
    case "Home": return VK.HOME;
    case "End": return VK.END;
    case "PageUp": return VK.PGUP;
    case "PageDown": return VK.PGDN;
    default: return null;
  }
}
