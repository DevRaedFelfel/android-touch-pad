import ctypes

from agent.inject_win import INPUT, KEYBDINPUT, MOUSEINPUT
from agent.protocol import encode_move, parse_binary


def test_move_roundtrip():
    assert parse_binary(encode_move(7, -12, 345)) == ("move", 7, -12, 345)


def test_move_extremes_and_seq_wrap():
    assert parse_binary(encode_move(0x10001, -32768, 32767)) == ("move", 1, -32768, 32767)


def test_malformed_frames_are_ignored():
    assert parse_binary(b"") is None
    assert parse_binary(b"\x01\x00") is None  # truncated move
    assert parse_binary(encode_move(1, 1, 1) + b"\x00") is None  # trailing byte
    assert parse_binary(b"\xff\x00\x00\x00\x00\x00\x00") is None  # unknown type


def test_move_is_seven_bytes():
    assert len(encode_move(0, 0, 0)) == 7


def test_win32_struct_layout_matches_windows():
    # Sizes documented for Windows: INPUT is 40 bytes on x64, 28 on x86.
    ptr = ctypes.sizeof(ctypes.c_void_p)
    assert ctypes.sizeof(MOUSEINPUT) == (32 if ptr == 8 else 24)
    assert ctypes.sizeof(INPUT) == (40 if ptr == 8 else 28)
    assert ctypes.sizeof(KEYBDINPUT) == (24 if ptr == 8 else 16)
