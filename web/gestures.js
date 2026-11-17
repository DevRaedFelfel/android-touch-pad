// Pure gesture state machine (SPEC.md section 6.3). No DOM access, no timers:
// feed it pointer events with timestamps and it calls the `out` callbacks.
//
//   out.move(dx, dy, t)   raw pixel movement ending at time t in ms (caller applies sensitivity)
//   out.scroll(dx, dy)    raw pixel movement of the two-finger centroid
//   out.zoom(d)           change in distance between two fingers, in pixels
//   out.button(b, down)   b: 0=left 1=right 2=middle (used for drag)
//   out.click(b)          press and release
//   out.chord(vks)        press keys in order, release in reverse order

export const VK = { TAB: 0x09, CONTROL: 0x11, D: 0x44, LWIN: 0x5b, LEFT: 0x25, RIGHT: 0x27 };

export const TUNING = {
  tapSlop: 6, // px a single finger may travel and still be a tap; also the move dead zone
  tapMs: 200, // max duration of a tap
  doubleTapMs: 300, // touch again within this after a tap to start a drag / double click
  multiSlop: 6, // px of two-finger travel before it becomes scroll or pinch
  multiTapMs: 250, // max duration of a two-finger tap
  middleSlop: 10, // px a three-finger tap may travel
  middleTapMs: 250,
  swipeMinPx: 60, // three-finger swipe distance
  swipeMaxMs: 600,
  pinchBias: 1.5, // pinch must beat pan by this factor to count as a pinch
};

export function createGestures(out, getSettings, tuning = TUNING) {
  const ptrs = new Map(); // id -> {x, y, sx, sy}
  let lockout = false; // after a multi-finger gesture, ignore leftover fingers until all lift
  let single = null;
  let multi = null;
  let triple = null;
  let lastTapT = -Infinity;

  function centroid() {
    let x = 0;
    let y = 0;
    for (const p of ptrs.values()) {
      x += p.x;
      y += p.y;
    }
    return [x / ptrs.size, y / ptrs.size];
  }

  function releaseDrag() {
    if (single && single.dragging) out.button(0, false);
  }

  function down(id, x, y, t) {
    if (ptrs.has(id) || ptrs.size >= 3) return;
    ptrs.set(id, { x, y, sx: x, sy: y });
    const n = ptrs.size;
    if (n === 1) {
      if (lockout) return;
      single = { id, t0: t, moved: false, hx: 0, hy: 0, dragging: false };
      if (getSettings().tapToClick && t - lastTapT <= tuning.doubleTapMs) {
        // Second touch right after a tap: hold the left button (drag, or double click).
        single.dragging = true;
        out.button(0, true);
      }
    } else if (n === 2) {
      releaseDrag();
      single = null;
      lockout = true;
      const [a, b] = ptrs.values();
      multi = {
        a: a,
        b: b,
        t0: t,
        mode: null,
        tap: true,
        cx: (a.x + b.x) / 2,
        cy: (a.y + b.y) / 2,
        dist: Math.hypot(a.x - b.x, a.y - b.y),
        panX: 0,
        panY: 0,
        pinch: 0,
      };
    } else if (n === 3) {
      multi = null;
      lockout = true;
      const [cx, cy] = centroid();
      triple = { t0: t, cx, cy };
    }
  }

  function move(id, x, y, t) {
    const p = ptrs.get(id);
    if (!p) return;
    let dx = x - p.x;
    let dy = y - p.y;
    p.x = x;
    p.y = y;

    if (single && single.id === id) {
      if (!single.moved && !single.dragging) {
        // Hold movement inside the dead zone so a tap doesn't nudge the cursor.
        single.hx += dx;
        single.hy += dy;
        if (Math.hypot(x - p.sx, y - p.sy) < tuning.tapSlop) return;
        single.moved = true;
        dx = single.hx;
        dy = single.hy;
      }
      out.move(dx, dy, t);
    } else if (multi && ptrs.size === 2) {
      const a = multi.a;
      const b = multi.b;
      const cx = (a.x + b.x) / 2;
      const cy = (a.y + b.y) / 2;
      const dist = Math.hypot(a.x - b.x, a.y - b.y);
      const dcx = cx - multi.cx;
      const dcy = cy - multi.cy;
      const dd = dist - multi.dist;
      multi.cx = cx;
      multi.cy = cy;
      multi.dist = dist;
      if (multi.mode === null) {
        multi.panX += dcx;
        multi.panY += dcy;
        multi.pinch += dd;
        const pan = Math.hypot(multi.panX, multi.panY);
        const pinch = Math.abs(multi.pinch);
        if (Math.max(pan, pinch) < tuning.multiSlop) return;
        multi.tap = false;
        if (pinch > pan * tuning.pinchBias) {
          multi.mode = "pinch";
          out.zoom(multi.pinch);
        } else {
          multi.mode = "scroll";
          out.scroll(multi.panX, multi.panY);
        }
      } else if (multi.mode === "scroll") {
        out.scroll(dcx, dcy);
      } else {
        out.zoom(dd);
      }
    }
  }

  function up(id, t) {
    const p = ptrs.get(id);
    if (!p) return;
    const n = ptrs.size;
    if (n === 1 && single && single.id === id) {
      if (single.dragging) {
        out.button(0, false);
        lastTapT = -Infinity;
      } else if (!single.moved && t - single.t0 <= tuning.tapMs && getSettings().tapToClick) {
        out.click(0);
        lastTapT = t;
      } else {
        lastTapT = -Infinity;
      }
      single = null;
    } else if (n === 2 && multi) {
      if (multi.tap && t - multi.t0 <= tuning.multiTapMs) out.click(1);
      multi = null;
    } else if (n === 3 && triple) {
      const [cx, cy] = centroid();
      const dx = cx - triple.cx;
      const dy = cy - triple.cy;
      const dist = Math.hypot(dx, dy);
      const dt = t - triple.t0;
      if (dist < tuning.middleSlop && dt <= tuning.middleTapMs) {
        out.click(2);
      } else if (dist >= tuning.swipeMinPx && dt <= tuning.swipeMaxMs) {
        if (Math.abs(dy) >= Math.abs(dx)) {
          out.chord(dy < 0 ? [VK.LWIN, VK.TAB] : [VK.LWIN, VK.D]);
        } else {
          out.chord([VK.CONTROL, VK.LWIN, dx < 0 ? VK.LEFT : VK.RIGHT]);
        }
      }
      triple = null;
    }
    ptrs.delete(id);
    if (ptrs.size === 0) lockout = false;
  }

  function cancel(id) {
    if (!ptrs.has(id)) return;
    releaseDrag();
    ptrs.delete(id);
    single = null;
    multi = null;
    triple = null;
    lastTapT = -Infinity;
    lockout = ptrs.size > 0;
  }

  /** Forget everything (e.g. on disconnect). Releases a held drag button. */
  function reset() {
    releaseDrag();
    ptrs.clear();
    single = multi = triple = null;
    lockout = false;
    lastTapT = -Infinity;
  }

  return { down, move, up, cancel, reset };
}
