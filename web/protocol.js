// Binary wire protocol (SPEC.md section 5). Little-endian.
// Buffers are preallocated and reused so the move path allocates nothing.

export const T_MOVE = 0x01;

const moveBuf = new ArrayBuffer(7);
const moveView = new DataView(moveBuf);
let seq = 0;

/** Pack a relative move. dx/dy must be integers within int16. Returns a reused buffer. */
export function packMove(dx, dy) {
  moveView.setUint8(0, T_MOVE);
  moveView.setUint16(1, seq, true);
  seq = (seq + 1) & 0xffff;
  moveView.setInt16(3, dx, true);
  moveView.setInt16(5, dy, true);
  return moveBuf;
}
