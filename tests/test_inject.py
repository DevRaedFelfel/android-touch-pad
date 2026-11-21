import sys

import pytest

from agent.inject_win import DryRunInjector, WindowsInjector, make_injector


def test_dry_run_tracks_and_releases_held_input():
    inj = DryRunInjector()
    inj.button(0, True)
    inj.button(2, True)
    inj.key(0x11, True)
    inj.button(2, False)
    inj.release_all()
    assert inj.buttons_down == set() and inj.keys_down == set()
    assert list(inj.events)[-2:] == [("button", 0, False), ("key", 0x11, False)]


def test_dry_run_event_log_is_bounded():
    inj = DryRunInjector(keep=100)
    for _ in range(1000):
        inj.move(1, 1)
    assert len(inj.events) == 100 and inj.total_dx == 1000


@pytest.mark.skipif(sys.platform == "win32", reason="only meaningful off Windows")
def test_real_injection_is_refused_off_windows():
    with pytest.raises(RuntimeError):
        WindowsInjector()
    with pytest.raises(SystemExit):
        make_injector(dry_run=False)


class _FakeUser32:
    """Stands in for ctypes.windll.user32 so WindowsInjector can be tested anywhere."""

    def __init__(self):
        self.calls = []  # one list of (kind, fields) tuples per SendInput call

        def send_input(n, arg, size):
            items = arg._obj if hasattr(arg, "_obj") and not isinstance(arg, list) else arg
            inputs = [items] if not hasattr(items, "__len__") else list(items)
            rec = []
            for inp in inputs[:n]:
                if inp.type == 0:
                    mi = inp.mi
                    rec.append(("mouse", mi.dx, mi.dy, mi.mouseData, mi.dwFlags))
                else:
                    ki = inp.ki
                    if ki.dwFlags & 0x0004:  # KEYEVENTF_UNICODE
                        rec.append(("uni", ki.wScan, ki.dwFlags))
                    else:
                        rec.append(("key", ki.wVk, ki.dwFlags))
            self.calls.append(rec)
            return n

        self.SendInput = send_input
        self.MapVirtualKeyW = lambda vk, kind: vk + 1000
        self.GetSystemMetrics = lambda i: (2560, 1440)[i]


@pytest.fixture
def win(monkeypatch):
    import ctypes

    import agent.inject_win as mod

    fake = _FakeUser32()
    monkeypatch.setattr(mod.sys, "platform", "win32")
    monkeypatch.setattr(ctypes, "windll", type("W", (), {"user32": fake}), raising=False)
    return mod.WindowsInjector(), fake


def test_windows_move_is_relative(win):
    inj, fake = win
    inj.move(-7, 12)
    assert fake.calls == [[("mouse", -7, 12, 0, 0x0001)]]
    assert inj.screen_size() == (2560, 1440)


def test_windows_buttons_and_release(win):
    inj, fake = win
    inj.button(0, True)
    inj.button(1, True)
    inj.button(1, False)
    inj.release_all()
    flags = [c[0][4] for c in fake.calls]
    assert flags == [0x0002, 0x0008, 0x0010, 0x0004]  # L down, R down, R up, L up (released)
    inj.button(9, True)  # unknown button: ignored
    assert len(fake.calls) == 4


def test_windows_wheel_uses_twos_complement_for_negative_deltas(win):
    inj, fake = win
    inj.scroll(0, -120)
    inj.scroll(60, 0)
    assert fake.calls[0] == [("mouse", 0, 0, 0xFFFFFF88, 0x0800)]
    assert fake.calls[1] == [("mouse", 0, 0, 60, 0x1000)]


def test_windows_zoom_is_one_atomic_ctrl_wheel_ctrl_call(win):
    inj, fake = win
    inj.zoom(120)
    assert fake.calls == [[("key", 0x11, 0), ("mouse", 0, 0, 120, 0x0800), ("key", 0x11, 0x0002)]]


def test_windows_keys_flag_extended_keys_and_release_on_exit(win):
    inj, fake = win
    inj.key(0x25, True)  # Left arrow: extended
    inj.key(0x41, True)  # 'A': not extended
    inj.release_all()
    assert fake.calls[0] == [("key", 0x25, 0x0001)]
    assert fake.calls[1] == [("key", 0x41, 0)]
    assert fake.calls[2] == [("key", 0x25, 0x0003)]  # key up + extended
    assert fake.calls[3] == [("key", 0x41, 0x0002)]


def test_windows_text_is_typed_as_unicode_down_up_pairs(win):
    inj, fake = win
    inj.text("aé")
    flat = [item for call in fake.calls for item in call]
    assert flat == [
        ("uni", ord("a"), 0x0004),
        ("uni", ord("a"), 0x0006),
        ("uni", ord("é"), 0x0004),
        ("uni", ord("é"), 0x0006),
    ]


def test_windows_text_sends_surrogate_pairs_for_emoji(win):
    inj, fake = win
    inj.text("\U0001F600")  # U+1F600 = D83D DE00
    downs = [item[1] for call in fake.calls for item in call if item[2] == 0x0004]
    assert downs == [0xD83D, 0xDE00]


def test_windows_text_maps_newline_and_tab_and_drops_control_chars(win):
    inj, fake = win
    inj.text("a\r\nb\tc\x07")
    flat = [item for call in fake.calls for item in call]
    assert flat == [
        ("uni", ord("a"), 0x0004), ("uni", ord("a"), 0x0006),
        ("key", 0x0D, 0), ("key", 0x0D, 0x0002),  # CRLF is a single Enter
        ("uni", ord("b"), 0x0004), ("uni", ord("b"), 0x0006),
        ("key", 0x09, 0), ("key", 0x09, 0x0002),
        ("uni", ord("c"), 0x0004), ("uni", ord("c"), 0x0006),
    ]


def test_windows_long_text_is_sent_in_bounded_chunks(win):
    inj, fake = win
    inj.text("x" * 1000)
    assert all(len(call) <= 130 for call in fake.calls)
    assert sum(len(call) for call in fake.calls) == 2000


def test_power_throttling_opt_out_asks_windows_not_to_throttle():
    import ctypes

    from agent import inject_win

    calls = []

    class FakeKernel32:
        def GetCurrentProcess(self):
            return 123

        def SetProcessInformation(self, handle, cls, ptr, size):
            state = ctypes.cast(ptr, ctypes.POINTER(inject_win._PowerThrottlingState)).contents
            calls.append((handle, cls, state.Version, state.ControlMask, state.StateMask, size))
            return 1

    assert inject_win.disable_power_throttling(FakeKernel32()) is True
    # ProcessPowerThrottling (4), version 1, control execution speed + timer resolution, state 0 = not throttled
    assert calls == [(123, 4, 1, 0x5, 0, 12)]

    class Rejecting(FakeKernel32):
        def SetProcessInformation(self, *a):
            return 0

    assert inject_win.disable_power_throttling(Rejecting()) is False
