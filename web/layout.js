// Layout switch: lets a phone held upright act as a wide touchpad.
//
// "normal"      the page follows the phone (auto-rotate works as usual).
// "wide-left"   hold the phone turned to the left (its top points left, like Android
//               landscape). The UI is drawn turned 90 degrees clockwise.
// "wide-right"  hold the phone turned to the right (its top points right). The UI is
//               drawn turned 90 degrees counter-clockwise.
//
// Everything here is client-side only; the agent never knows the layout changed, so
// switching needs no reconnect.

export const LAYOUTS = ["normal", "wide-left", "wide-right"];

export function isLayout(v) {
  return LAYOUTS.includes(v);
}

/** The next layout in the cycle (used by the bar button). Unknown values restart at normal. */
export function nextLayout(current) {
  const i = LAYOUTS.indexOf(current);
  return LAYOUTS[(i + 1) % LAYOUTS.length];
}

/**
 * Map a screen point (clientX/clientY, in a viewport of w x h) into the rotated UI's own
 * coordinates, so the gesture code sees a wide pad: x grows along the long side.
 * Using the mapped points, drags, scroll directions and pinch all follow the new axes.
 */
export function mapPoint(layout, x, y, w, h) {
  if (layout === "wide-left") return { x: y, y: w - x }; // UI turned clockwise
  if (layout === "wide-right") return { x: h - y, y: x }; // UI turned counter-clockwise
  return { x, y };
}
