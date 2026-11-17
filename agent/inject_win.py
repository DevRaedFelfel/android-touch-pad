"""Input injection.

WindowsInjector calls user32.SendInput through ctypes. DryRunInjector records
what would have been injected (used by tests, the latency tool and --dry-run).

The structure layouts below use fixed-width ctypes types so they are identical on
every platform; that lets the layout be unit-tested off Windows.
"""

from __future__ import annotations

import ctypes
import sys

INPUT_MOUSE = 0
MOUSEEVENTF_MOVE = 0x0001

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
    """Counts instead of injecting. Safe on any OS."""

    def __init__(self, screen: tuple[int, int] = (1920, 1080)):
        self.screen = screen
        self.move_calls = 0
        self.total_dx = 0
        self.total_dy = 0

    def screen_size(self) -> tuple[int, int]:
        return self.screen

    def move(self, dx: int, dy: int) -> None:
        self.move_calls += 1
        self.total_dx += dx
        self.total_dy += dy

    def release_all(self) -> None:
        pass


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
        # One preallocated INPUT, reused for every relative mouse move.
        self._move = INPUT(type=INPUT_MOUSE)
        self._move.mi.dwFlags = MOUSEEVENTF_MOVE
        self._size = ctypes.sizeof(INPUT)

    def screen_size(self) -> tuple[int, int]:
        return self._user32.GetSystemMetrics(0), self._user32.GetSystemMetrics(1)

    def move(self, dx: int, dy: int) -> None:
        mi = self._move.mi
        mi.dx = dx
        mi.dy = dy
        self._user32.SendInput(1, ctypes.byref(self._move), self._size)

    def release_all(self) -> None:
        pass


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
    except OSError:
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
