"""Input injection.

WindowsInjector calls user32.SendInput through ctypes. DryRunInjector records
what would have been injected (used by tests, the latency tool and --dry-run).

The structure layouts below use fixed-width ctypes types so they are identical on
every platform; that lets the layout be unit-tested off Windows.
"""

from __future__ import annotations

import ctypes
import logging
import struct
import sys
from collections import deque

log = logging.getLogger("touchpad")

INPUT_MOUSE = 0
INPUT_KEYBOARD = 1

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_WHEEL = 0x0800
MOUSEEVENTF_HWHEEL = 0x1000
# button index (0=left, 1=right, 2=middle) -> (down flag, up flag)
_BUTTON_FLAGS = {0: (0x0002, 0x0004), 1: (0x0008, 0x0010), 2: (0x0020, 0x0040)}

KEYEVENTF_EXTENDEDKEY = 0x0001
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
VK_CONTROL = 0x11
VK_TAB = 0x09
VK_RETURN = 0x0D
_TEXT_CHUNK = 128  # max INPUT records per SendInput call
# Keys that need the extended-key flag so Windows treats them as the dedicated
# navigation keys rather than numpad keys.
_EXTENDED_VKS = frozenset(
    {0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5}
)

# Windows' own DWORD/WORD/LONG, spelled with fixed-width types.
_DWORD = ctypes.c_uint32
_LONG = ctypes.c_int32
_WORD = ctypes.c_uint16
_ULONG_PTR = ctypes.c_size_t  # pointer-sized, matches 32- and 64-bit Windows


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", _LONG),
        ("dy", _LONG),
        ("mouseData", _DWORD),
        ("dwFlags", _DWORD),
        ("time", _DWORD),
        ("dwExtraInfo", _ULONG_PTR),
    ]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", _WORD),
        ("wScan", _WORD),
        ("dwFlags", _DWORD),
        ("time", _DWORD),
        ("dwExtraInfo", _ULONG_PTR),
    ]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", _DWORD), ("wParamL", _WORD), ("wParamH", _WORD)]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("type", _DWORD), ("u", _INPUTUNION)]


class DryRunInjector:
    """Records instead of injecting. Safe on any OS.

    `events` keeps the most recent events in order, as tuples such as
    ("move", dx, dy), ("button", b, down), ("key", vk, down), ("scroll", dx, dy),
    ("zoom", d). Totals are kept as counters so long runs don't grow memory.
    """

    def __init__(self, screen: tuple[int, int] = (1920, 1080), keep: int = 10_000):
        self.screen = screen
        self.events: deque[tuple] = deque(maxlen=keep)
        self.move_calls = 0
        self.total_dx = 0
        self.total_dy = 0
        self.scroll_dx = 0
        self.scroll_dy = 0
        self.zoom_total = 0
        self.buttons_down: set[int] = set()
        self.keys_down: set[int] = set()

    def screen_size(self) -> tuple[int, int]:
        return self.screen

    def move(self, dx: int, dy: int) -> None:
        self.move_calls += 1
        self.total_dx += dx
        self.total_dy += dy
        self.events.append(("move", dx, dy))

    def scroll(self, dx: int, dy: int) -> None:
        self.scroll_dx += dx
        self.scroll_dy += dy
        self.events.append(("scroll", dx, dy))

    def zoom(self, delta: int) -> None:
        self.zoom_total += delta
        self.events.append(("zoom", delta))

    def button(self, button: int, down: bool) -> None:
        (self.buttons_down.add if down else self.buttons_down.discard)(button)
        self.events.append(("button", button, down))

    def key(self, vk: int, down: bool) -> None:
        (self.keys_down.add if down else self.keys_down.discard)(vk)
        self.events.append(("key", vk, down))

    def text(self, text: str) -> None:
        self.events.append(("text", text))

    def release_all(self) -> None:
        for b in sorted(self.buttons_down):
            self.button(b, False)
        for vk in sorted(self.keys_down):
            self.key(vk, False)


class WindowsInjector:
    def __init__(self) -> None:
        if sys.platform != "win32":
            raise RuntimeError("WindowsInjector only works on Windows")
        self._user32 = ctypes.windll.user32  # type: ignore[attr-defined]
        self._user32.SendInput.argtypes = (
            ctypes.c_uint,
            ctypes.POINTER(INPUT),
            ctypes.c_int,
        )
        self._user32.SendInput.restype = ctypes.c_uint
        self._user32.MapVirtualKeyW.argtypes = (ctypes.c_uint, ctypes.c_uint)
        self._user32.MapVirtualKeyW.restype = ctypes.c_uint
        # One preallocated INPUT, reused for every relative mouse move.
        self._move = INPUT(type=INPUT_MOUSE)
        self._move.mi.dwFlags = MOUSEEVENTF_MOVE
        self._size = ctypes.sizeof(INPUT)
        self._buttons_down: set[int] = set()
        self._keys_down: set[int] = set()
        self._warned = False

    def _check(self, sent: int, expected: int) -> None:
        """SendInput returns how many events were injected; fewer means Windows blocked it."""
        if sent < expected and not self._warned:
            self._warned = True
            log.warning(
                "Windows blocked injected input (%d of %d events). This happens over an "
                "elevated window or a UAC prompt. Run the agent as administrator to control those.",
                sent,
                expected,
            )

    def _send(self, *inputs: INPUT) -> None:
        arr = (INPUT * len(inputs))(*inputs)
        self._check(self._user32.SendInput(len(inputs), arr, self._size), len(inputs))

    @staticmethod
    def _mouse(flags: int, data: int = 0) -> INPUT:
        inp = INPUT(type=INPUT_MOUSE)
        inp.mi.dwFlags = flags
        inp.mi.mouseData = data & 0xFFFFFFFF  # negative wheel deltas: two's complement
        return inp

    def _keyboard(self, vk: int, down: bool) -> INPUT:
        inp = INPUT(type=INPUT_KEYBOARD)
        inp.ki.wVk = vk
        inp.ki.wScan = self._user32.MapVirtualKeyW(vk, 0)  # MAPVK_VK_TO_VSC
        flags = 0 if down else KEYEVENTF_KEYUP
        if vk in _EXTENDED_VKS:
            flags |= KEYEVENTF_EXTENDEDKEY
        inp.ki.dwFlags = flags
        return inp

    def screen_size(self) -> tuple[int, int]:
        return self._user32.GetSystemMetrics(0), self._user32.GetSystemMetrics(1)

    def move(self, dx: int, dy: int) -> None:
        mi = self._move.mi
        mi.dx = dx
        mi.dy = dy
        self._check(self._user32.SendInput(1, ctypes.byref(self._move), self._size), 1)

    def scroll(self, dx: int, dy: int) -> None:
        inputs = []
        if dy:
            inputs.append(self._mouse(MOUSEEVENTF_WHEEL, dy))
        if dx:
            inputs.append(self._mouse(MOUSEEVENTF_HWHEEL, dx))
        if inputs:
            self._send(*inputs)

    def zoom(self, delta: int) -> None:
        # Ctrl + wheel is the universal zoom gesture; one atomic SendInput call.
        self._send(
            self._keyboard(VK_CONTROL, True),
            self._mouse(MOUSEEVENTF_WHEEL, delta),
            self._keyboard(VK_CONTROL, False),
        )

    def button(self, button: int, down: bool) -> None:
        flags = _BUTTON_FLAGS.get(button)
        if flags is None:
            return
        (self._buttons_down.add if down else self._buttons_down.discard)(button)
        self._send(self._mouse(flags[0] if down else flags[1]))

    def key(self, vk: int, down: bool) -> None:
        (self._keys_down.add if down else self._keys_down.discard)(vk)
        self._send(self._keyboard(vk, down))

    def _unicode(self, unit: int, down: bool) -> INPUT:
        inp = INPUT(type=INPUT_KEYBOARD)
        inp.ki.wVk = 0
        inp.ki.wScan = unit
        inp.ki.dwFlags = KEYEVENTF_UNICODE | (0 if down else KEYEVENTF_KEYUP)
        return inp

    def text(self, text: str) -> None:
        """Type text as Unicode key events (layout independent, handles emoji).

        Newlines and tabs become Enter and Tab key presses; other control characters
        are dropped.
        """
        text = text.replace("\r\n", "\n").replace("\r", "\n")
        data = text.encode("utf-16-le", errors="replace")
        units = struct.unpack(f"<{len(data) // 2}H", data)
        batch: list[INPUT] = []
        for u in units:
            if u == 0x0A:
                batch += [self._keyboard(VK_RETURN, True), self._keyboard(VK_RETURN, False)]
            elif u == 0x09:
                batch += [self._keyboard(VK_TAB, True), self._keyboard(VK_TAB, False)]
            elif u < 0x20 or u == 0x7F:
                continue
            else:
                batch += [self._unicode(u, True), self._unicode(u, False)]
            if len(batch) >= _TEXT_CHUNK:
                self._send(*batch)
                batch = []
        if batch:
            self._send(*batch)

    def release_all(self) -> None:
        for b in sorted(self._buttons_down):
            self.button(b, False)
        for vk in sorted(self._keys_down):
            self.key(vk, False)


class _PowerThrottlingState(ctypes.Structure):
    _fields_ = [("Version", ctypes.c_uint32), ("ControlMask", ctypes.c_uint32), ("StateMask", ctypes.c_uint32)]


def disable_power_throttling(kernel32) -> bool:
    """Tell Windows 10/11 not to throttle this process when it is in the background or idle.

    Without this, an idle agent can be put on efficiency cores and have its 1 ms timer request ignored,
    so the first moves after a pause arrive late and in a burst (the cursor stalls, then jumps).
    Returns True if Windows accepted the request (older Windows rejects it; that is fine).
    """
    state = _PowerThrottlingState(
        1,  # PROCESS_POWER_THROTTLING_CURRENT_VERSION
        0x1 | 0x4,  # control EXECUTION_SPEED and IGNORE_TIMER_RESOLUTION ...
        0,  # ... and set both to "off" (StateMask 0 = do not throttle)
    )
    handle = kernel32.GetCurrentProcess()
    return bool(
        kernel32.SetProcessInformation(handle, 4, ctypes.byref(state), ctypes.sizeof(state))  # ProcessPowerThrottling
    )


def tune_process() -> None:
    """Lower timer granularity and raise priority. No-op off Windows."""
    if sys.platform != "win32":
        return
    try:
        ctypes.windll.winmm.timeBeginPeriod(1)  # type: ignore[attr-defined]
        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        kernel32.SetPriorityClass.argtypes = (ctypes.c_void_p, ctypes.c_uint32)
        kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), 0x00000080)  # HIGH
        kernel32.SetProcessInformation.argtypes = (ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32)
        disable_power_throttling(kernel32)
    except (OSError, AttributeError):  # AttributeError: Windows older than 8 has no SetProcessInformation
        pass


def untune_process() -> None:
    if sys.platform != "win32":
        return
    try:
        ctypes.windll.winmm.timeEndPeriod(1)  # type: ignore[attr-defined]
    except OSError:
        pass


def make_injector(dry_run: bool):
    if dry_run:
        return DryRunInjector()
    if sys.platform != "win32":
        raise SystemExit(
            "Input injection is only supported on Windows. "
            "Use --dry-run to test on this machine."
        )
    return WindowsInjector()
