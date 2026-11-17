// Pointer acceleration (pure, no DOM). Gain depends on how fast the finger moves:
//
//   slow  -> a little BELOW 1x, for precise positioning
//   walk  -> about 1x
//   flick -> up to ~4x, to cross the screen in one swipe
//
// The "Acceleration" setting is a strength from 0 to 1. At 0 the gain is exactly 1
// at every speed (plain linear movement); at 1 the curve is at its strongest.

export const CURVE = {
  slowGainDrop: 0.4, // at strength 1, slow movement is scaled by 1 - 0.4 = 0.6
  fastGainBoost: 3.0, // at strength 1, fast movement is scaled by 1 + 3 = 4
  v0: 0.15, // px/ms (150 px/s): at or below this the finger counts as "slow"
  v1: 3.0, // px/ms (3000 px/s): at or above this it counts as a "flick"
  window: 6, // speed is measured over the last 6 events (about 24 ms at 240 Hz)
  gapMs: 80, // a pause longer than this starts a new stroke
  firstStepMs: 16, // assumed duration of the first step of a stroke
};

function smoothstep(x) {
  const t = x <= 0 ? 0 : x >= 1 ? 1 : x;
  return t * t * (3 - 2 * t);
}

/** Gain for a finger speed in px/ms at a strength 0..1. Pure function, also used by tests. */
export function gainAt(speed, strength, curve = CURVE) {
  const s = strength <= 0 ? 0 : strength >= 1 ? 1 : strength;
  const lo = 1 - curve.slowGainDrop * s;
  const hi = 1 + curve.fastGainBoost * s;
  return lo + (hi - lo) * smoothstep((speed - curve.v0) / (curve.v1 - curve.v0));
}

export function createAccel(getStrength, curve = CURVE) {
  // Speed = distance / time over the last few events. Averaging over a window makes the
  // estimate robust to timestamp jitter (touch events are sometimes delivered in batches),
  // so one odd event can't cause a sudden jump in gain.
  const n = curve.window;
  const dist = new Float64Array(n);
  const time = new Float64Array(n);
  let count = 0;
  let head = 0;
  let sumD = 0;
  let sumT = 0;
  let lastT = -Infinity;

  function clearWindow() {
    count = 0;
    head = 0;
    sumD = 0;
    sumT = 0;
  }

  return {
    /** Gain to apply to a movement of (dx, dy) pixels that ended at time t (ms). */
    gain(dx, dy, t) {
      const strength = getStrength();
      if (!(strength > 0)) {
        lastT = t;
        clearWindow();
        return 1;
      }
      let dt = t - lastT;
      lastT = t;
      if (!(dt <= curve.gapMs)) {
        // First event, a pause, or a bad timestamp: start a new stroke.
        clearWindow();
        dt = curve.firstStepMs;
      } else if (dt < 1) {
        dt = 1; // same-millisecond events
      }
      const d = Math.hypot(dx, dy);
      if (count === n) {
        sumD -= dist[head];
        sumT -= time[head];
      } else {
        count++;
      }
      dist[head] = d;
      time[head] = dt;
      sumD += d;
      sumT += dt;
      head = (head + 1) % n;
      return gainAt(sumD / sumT, strength, curve);
    },
    /** Forget the speed history (finger lifted or put down). */
    reset() {
      clearWindow();
      lastT = -Infinity;
    },
  };
}
