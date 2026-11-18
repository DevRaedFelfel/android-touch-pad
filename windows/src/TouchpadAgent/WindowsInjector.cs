// Input injection through user32.SendInput (agent/inject_win.py).
//
// The injector talks to Windows through INativeInput so the logic (which INPUT records are built, in what
// order, with which flags) can be unit-tested off Windows with a fake. The structure layouts are fixed so
// their sizes can be checked everywhere: 40 bytes on 64-bit Windows, 28 on 32-bit.
using System.Runtime.InteropServices;
using TouchpadAgent.Core;

namespace TouchpadAgent;

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx, dy;
    public uint mouseData, dwFlags, time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk, wScan;
    public uint dwFlags, time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL, wParamH;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public INPUTUNION u;
}

public interface INativeInput
{
    /// <summary>user32.SendInput: how many of the records Windows accepted.</summary>
    uint SendInput(INPUT[] inputs, int count);
    /// <summary>MapVirtualKeyW(vk, MAPVK_VK_TO_VSC).</summary>
    ushort ScanCode(uint vk);
    (int w, int h) ScreenSize();
}

public sealed class WindowsInjector : IInputInjector
{
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    public const int VK_CONTROL = 0x11, VK_TAB = 0x09, VK_RETURN = 0x0D;
    public const int TextChunk = 128; // max INPUT records per SendInput call

    // button index (0 = left, 1 = right, 2 = middle) -> (down flag, up flag)
    private static readonly (uint down, uint up)[] ButtonFlags = { (0x0002, 0x0004), (0x0008, 0x0010), (0x0020, 0x0040) };

    // Keys that need the extended-key flag so Windows treats them as the dedicated navigation keys, not numpad keys.
    private static readonly HashSet<int> ExtendedVks = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5,
    };

    private readonly INativeInput _native;
    private readonly int _size = Marshal.SizeOf<INPUT>();
    private readonly INPUT[] _move = new INPUT[1]; // reused for every relative mouse move
    private readonly HashSet<int> _buttonsDown = new(), _keysDown = new();
    private bool _warned;

    public Action<string>? Warn { get; set; }

    public WindowsInjector(INativeInput native)
    {
        _native = native;
        _move[0].type = INPUT_MOUSE;
        _move[0].u.mi.dwFlags = MOUSEEVENTF_MOVE;
    }

    /// <summary>SendInput returns how many events were injected; fewer means Windows blocked it (UAC prompt, elevated window).</summary>
    private void Check(uint sent, int expected)
    {
        if (sent >= expected || _warned) return;
        _warned = true;
        Warn?.Invoke($"Windows blocked injected input ({sent} of {expected} events). This happens over an elevated window or a UAC prompt. Run the agent as administrator to control those.");
    }

    private void Send(INPUT[] inputs) => Check(_native.SendInput(inputs, inputs.Length), inputs.Length);

    private static INPUT Mouse(uint flags, int data = 0)
    {
        var i = new INPUT { type = INPUT_MOUSE };
        i.u.mi.dwFlags = flags;
        i.u.mi.mouseData = unchecked((uint)data); // negative wheel deltas: two's complement
        return i;
    }

    private INPUT Keyboard(int vk, bool down)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = (ushort)vk;
        i.u.ki.wScan = _native.ScanCode((uint)vk);
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (ExtendedVks.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        i.u.ki.dwFlags = flags;
        return i;
    }

    private static INPUT UnicodeKey(ushort unit, bool down)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = 0;
        i.u.ki.wScan = unit;
        i.u.ki.dwFlags = KEYEVENTF_UNICODE | (down ? 0u : KEYEVENTF_KEYUP);
        return i;
    }

    public (int w, int h) ScreenSize() => _native.ScreenSize();

    public void Move(int dx, int dy)
    {
        _move[0].u.mi.dx = dx;
        _move[0].u.mi.dy = dy;
        Check(_native.SendInput(_move, 1), 1);
    }

    public void Scroll(int dx, int dy)
    {
        var inputs = new List<INPUT>(2);
        if (dy != 0) inputs.Add(Mouse(MOUSEEVENTF_WHEEL, dy));
        if (dx != 0) inputs.Add(Mouse(MOUSEEVENTF_HWHEEL, dx));
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    public void Zoom(int delta) =>
        // Ctrl + wheel is the universal zoom gesture; one atomic SendInput call.
        Send(new[] { Keyboard(VK_CONTROL, true), Mouse(MOUSEEVENTF_WHEEL, delta), Keyboard(VK_CONTROL, false) });

    public void Button(int button, bool down)
    {
        if (button < 0 || button >= ButtonFlags.Length) return;
        if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
        Send(new[] { Mouse(down ? ButtonFlags[button].down : ButtonFlags[button].up) });
    }

    public void Key(int vk, bool down)
    {
        if (down) _keysDown.Add(vk); else _keysDown.Remove(vk);
        Send(new[] { Keyboard(vk, down) });
    }

    /// <summary>Type text as Unicode key events (layout independent, handles emoji). Newlines and tabs become Enter
    /// and Tab key presses; other control characters are dropped.</summary>
    public void Text(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var batch = new List<INPUT>(TextChunk + 4);
        foreach (char c in text) // UTF-16 code units: a surrogate pair is sent as two units, as Windows expects
        {
            if (c == '\n') { batch.Add(Keyboard(VK_RETURN, true)); batch.Add(Keyboard(VK_RETURN, false)); }
            else if (c == '\t') { batch.Add(Keyboard(VK_TAB, true)); batch.Add(Keyboard(VK_TAB, false)); }
            else if (c < 0x20 || c == 0x7F) continue;
            else { batch.Add(UnicodeKey(c, true)); batch.Add(UnicodeKey(c, false)); }
            if (batch.Count >= TextChunk) { Send(batch.ToArray()); batch.Clear(); }
        }
        if (batch.Count > 0) Send(batch.ToArray());
    }

    public void ReleaseAll()
    {
        foreach (int b in _buttonsDown.Order().ToList()) Button(b, false);
        foreach (int vk in _keysDown.Order().ToList()) Key(vk, false);
    }
}
