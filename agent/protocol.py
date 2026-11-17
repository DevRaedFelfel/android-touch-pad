"""Binary wire protocol (SPEC.md section 5). All integers are little-endian.

Hot-path messages are binary; control messages (auth etc.) are JSON text frames
and are handled in server.py.
"""

from __future__ import annotations

import struct

PROTOCOL_VERSION = 1

# Client -> agent message types
T_MOVE = 0x01

_MOVE = struct.Struct("<BHhh")  # type, seq, dx, dy


def parse_binary(data: bytes) -> tuple | None:
    """Parse a binary client frame.

    Returns a tuple whose first item names the message, or None if the frame is
    malformed or has an unknown type (callers silently ignore those).
    """
    if not data:
        return None
    t = data[0]
    if t == T_MOVE:
        if len(data) != _MOVE.size:
            return None
        _, seq, dx, dy = _MOVE.unpack(data)
        return ("move", seq, dx, dy)
    return None


def encode_move(seq: int, dx: int, dy: int) -> bytes:
    return _MOVE.pack(T_MOVE, seq & 0xFFFF, dx, dy)
