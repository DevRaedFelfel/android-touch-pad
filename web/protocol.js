// Binary wire protocol (SPEC.md section 5). Little-endian.
// Move/scroll/zoom reuse preallocated buffers so the hot path allocates nothing.
// Rare reliable messages (button, key) allocate a tiny buffer each time.

export const T_MOVE = 0x01;
export const T_SCROLL = 0x02;
export const T_BUTTON = 0x03;
export const T_KEY = 0x04;
export const T_ZOOM = 0x07;

const moveBuf = new ArrayBuffer(7);
const moveView = new DataView(moveBuf);
const scrollBuf = new ArrayBuffer(7);
const scrollView = new DataView(scrollBuf);
const zoomBuf = new ArrayBuffer(3);
const zoomView = new DataView(zoomBuf);
let moveSeq = 0;
let scrollSeq = 0;

/** Relative move. dx/dy are integers within int16. Returns a reused buffer. */
export function packMove(dx, dy) {
  moveView.setUint8(0, T_MOVE);
  moveView.setUint16(1, moveSeq, true);
  moveSeq = (moveSeq + 1) & 0xffff;
  moveView.setInt16(3, dx, true);
  moveView.setInt16(5, dy, true);
  return moveBuf;
}

/** Scroll in wheel units (120 = one notch). Returns a reused buffer. */
export function packScroll(dx, dy) {
  scrollView.setUint8(0, T_SCROLL);
  scrollView.setUint16(1, scrollSeq, true);
  scrollSeq = (scrollSeq + 1) & 0xffff;
  scrollView.setInt16(3, dx, true);
  scrollView.setInt16(5, dy, true);
  return scrollBuf;
}

/** Ctrl+wheel zoom, delta in wheel units. Returns a reused buffer. */
export function packZoom(delta) {
  zoomView.setUint8(0, T_ZOOM);
  zoomView.setInt16(1, delta, true);
  return zoomBuf;
}

/** button: 0=left 1=right 2=middle; state: 1=down 0=up. */
export function packButton(button, state) {
  const b = new Uint8Array(3);
  b[0] = T_BUTTON;
  b[1] = button;
  b[2] = state;
  return b.buffer;
}

/** vk: Windows virtual-key code; state: 1=down 0=up. */
export function packKey(vk, state) {
  const buf = new ArrayBuffer(4);
  const v = new DataView(buf);
  v.setUint8(0, T_KEY);
  v.setUint16(1, vk, true);
  v.setUint8(3, state);
  return buf;
}
