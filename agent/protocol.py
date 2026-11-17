"""Binary wire protocol (SPEC.md section 5). All integers are little-endian.

Hot-path messages are binary; control messages (auth etc.) are JSON text frames
and are handled in server.py.
"""

from __future__ import annotations

import struct

PROTOCOL_VERSION = 1

# Client -> agent message types
T_MOVE = 0x01
T_SCROLL = 0x02
T_BUTTON = 0x03
T_KEY = 0x04
T_TEXT = 0x05
T_PING = 0x06
T_ZOOM = 0x07

# Agent -> client message types
T_PONG = 0x81

MAX_TEXT_BYTES = 4096  # per TEXT frame, UTF-8

_XY = struct.Struct("<BHhh")  # type, seq, dx, dy   (MOVE and SCROLL)
_BUTTON = struct.Struct("<BBB")  # type, button (0=L 1=R 2=M), state (0=up 1=down)
_KEY = struct.Struct("<BHB")  # type, Windows virtual-key, state
_ZOOM = struct.Struct("<Bh")  # type, delta (120 = one wheel notch)
_U32 = struct.Struct("<BI")  # type, u32 (PING / PONG)


def parse_binary(data: bytes) -> tuple | None:
    """Parse a binary client frame.

    Returns a tuple whose first item names the message, or None if the frame is
    malformed or has an unknown type (callers silently ignore those).
    """
    if not data:
        return None
    t = data[0]
    n = len(data)
    if t == T_MOVE or t == T_SCROLL:
        if n != _XY.size:
            return None
        _, seq, dx, dy = _XY.unpack(data)
        return ("move" if t == T_MOVE else "scroll", seq, dx, dy)
    if t == T_BUTTON:
        if n != _BUTTON.size:
            return None
        _, button, state = _BUTTON.unpack(data)
        if button > 2 or state > 1:
            return None
        return ("button", button, state)
    if t == T_KEY:
        if n != _KEY.size:
            return None
        _, vk, state = _KEY.unpack(data)
        if not 1 <= vk <= 254 or state > 1:
            return None
        return ("key", vk, state)
    if t == T_TEXT:
        if n < 2 or n - 1 > MAX_TEXT_BYTES:
            return None
        try:
            return ("text", data[1:].decode("utf-8"))
        except UnicodeDecodeError:
            return None
    if t == T_PING:
        if n != _U32.size:
            return None
        return ("ping", _U32.unpack(data)[1])
    if t == T_ZOOM:
        if n != _ZOOM.size:
            return None
        _, delta = _ZOOM.unpack(data)
        return ("zoom", delta)
    return None


def encode_move(seq: int, dx: int, dy: int) -> bytes:
    return _XY.pack(T_MOVE, seq & 0xFFFF, dx, dy)


def encode_scroll(seq: int, dx: int, dy: int) -> bytes:
    return _XY.pack(T_SCROLL, seq & 0xFFFF, dx, dy)


def encode_button(button: int, state: int) -> bytes:
    return _BUTTON.pack(T_BUTTON, button, state)


def encode_key(vk: int, state: int) -> bytes:
    return _KEY.pack(T_KEY, vk, state)


def encode_text(text: str) -> bytes:
    return bytes([T_TEXT]) + text.encode("utf-8")


def encode_zoom(delta: int) -> bytes:
    return _ZOOM.pack(T_ZOOM, delta)


def encode_ping(t: int) -> bytes:
    return _U32.pack(T_PING, t & 0xFFFFFFFF)


def encode_pong(t: int) -> bytes:
    return _U32.pack(T_PONG, t & 0xFFFFFFFF)
