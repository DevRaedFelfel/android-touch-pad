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


def test_scroll_button_key_zoom_roundtrip():
    from agent.protocol import encode_button, encode_key, encode_scroll, encode_zoom

    assert parse_binary(encode_scroll(3, -240, 120)) == ("scroll", 3, -240, 120)
    assert parse_binary(encode_button(1, 1)) == ("button", 1, 1)
    assert parse_binary(encode_key(0x5B, 0)) == ("key", 0x5B, 0)
    assert parse_binary(encode_zoom(-360)) == ("zoom", -360)


def test_invalid_values_are_rejected():
    from agent.protocol import encode_button, encode_key

    assert parse_binary(encode_button(3, 1)) is None  # no such button
    assert parse_binary(encode_button(0, 2)) is None  # bad state
    assert parse_binary(encode_key(0, 1)) is None  # VK 0 is invalid
    assert parse_binary(encode_key(255, 1)) is None  # VK above 254
    assert parse_binary(encode_key(0x41, 2)) is None  # bad state
    assert parse_binary(b"\x03\x00") is None  # truncated button
    assert parse_binary(b"\x07\x00") is None  # truncated zoom


def test_text_roundtrip_and_limits():
    from agent.protocol import MAX_TEXT_BYTES, encode_text

    assert parse_binary(encode_text("hello, مرحبا 😀")) == ("text", "hello, مرحبا 😀")
    assert parse_binary(b"\x05") is None  # empty text
    assert parse_binary(b"\x05\xff\xfe") is None  # invalid UTF-8
    assert parse_binary(b"\x05" + b"a" * MAX_TEXT_BYTES) is not None
    assert parse_binary(b"\x05" + b"a" * (MAX_TEXT_BYTES + 1)) is None
