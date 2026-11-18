// P/Invoke declarations. Everything here runs on Windows only.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TouchpadAgent;

[SupportedOSPlatform("windows")]
internal static class User32
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    // ---- window / tray plumbing ----
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    [DllImport("user32.dll")] public static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}

[SupportedOSPlatform("windows")]
internal static class Shell32
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
}

[SupportedOSPlatform("windows")]
internal static class Kernel32
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] public static extern bool SetPriorityClass(IntPtr process, uint priorityClass);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int x, y; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public UIntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEX
{
    public uint cbSize, style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public IntPtr hInstance, hIcon, hCursor, hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID, uFlags, uCallbackMessage;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    public uint dwState, dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
    public uint uTimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

/// <summary>The real Windows implementation of <see cref="INativeInput"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32Input : INativeInput
{
    public uint SendInput(INPUT[] inputs, int count) => User32.SendInput((uint)count, inputs, Marshal.SizeOf<INPUT>());
    public ushort ScanCode(uint vk) => (ushort)User32.MapVirtualKeyW(vk, 0); // MAPVK_VK_TO_VSC
    public (int w, int h) ScreenSize() => (User32.GetSystemMetrics(0), User32.GetSystemMetrics(1));
}
