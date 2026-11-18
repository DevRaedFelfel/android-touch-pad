// The notification-area (tray) icon, its menu, and the application's message loop. Plain Win32, no WinForms.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using TouchpadAgent.Core;

namespace TouchpadAgent;

public sealed record AppSettings(int Port = 8765, bool Https = false);

[SupportedOSPlatform("windows")]
internal sealed class TrayApp : IDisposable
{
    private const uint WM_APP = 0x8000, WM_TRAY = WM_APP + 1, WM_RUN = WM_APP + 2;
    private const uint WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_DESTROY = 0x0002;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;
    private const uint MB_YESNO = 4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40;
    private const int IDYES = 6;

    private const int CmdPair = 1, CmdForget = 2, CmdHttps = 3, CmdAutostart = 4, CmdFirewall = 5, CmdQuit = 6;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PhoneTouchpad";

    private readonly TokenStore _tokens;
    private readonly IInputInjector _injector;
    private readonly string _cfgDir;
    private readonly string? _webDir;
    private AppSettings _settings;
    private AgentServer? _server;
    private string _startError = "";
    private IntPtr _hwnd, _icon;
    private uint _taskbarCreated;
    private User32.WndProc? _wndProc; // kept in a field so the GC can't collect the delegate Windows calls
    private readonly ConcurrentQueue<Action> _uiQueue = new();

    public TrayApp(TokenStore tokens, IInputInjector injector, string cfgDir, AppSettings settings, string? webDir)
    {
        _tokens = tokens;
        _injector = injector;
        _cfgDir = cfgDir;
        _settings = settings;
        _webDir = webDir;
    }

    private string SettingsPath => Path.Combine(_cfgDir, "tray-settings.json");

    public static AppSettings LoadSettings(string cfgDir)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(cfgDir, "tray-settings.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return new(); }
    }

    private void SaveSettings() => Config.WriteAtomic(SettingsPath, System.Text.Json.JsonSerializer.Serialize(_settings));

    // ---------- lifecycle ----------
    public int Run()
    {
        CreateWindow();
        LoadIcon();
        AddIcon();
        StartServer();
        if (!_tokens.HasTokens()) PairPhone(); // first run: show the QR code straight away

        while (User32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }
        return 0;
    }

    private void CreateWindow()
    {
        _taskbarCreated = User32.RegisterWindowMessageW("TaskbarCreated");
        _wndProc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Kernel32.GetModuleHandleW(null),
            lpszClassName = "PhoneTouchpadTray",
        };
        if (User32.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        _hwnd = User32.CreateWindowExW(0, wc.lpszClassName, "Phone Touchpad", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
    }

    private void LoadIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tray.png")!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] png = ms.ToArray();
            // Windows accepts PNG-compressed icon images here (Vista and later).
            _icon = User32.CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, 32, 32, 0);
        }
        catch (Exception e) when (e is IOException or NullReferenceException) { }
        if (_icon == IntPtr.Zero) _icon = User32.LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    private NOTIFYICONDATA Nid(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private void AddIcon()
    {
        var d = Nid(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        d.szTip = TooltipText();
        Shell32.Shell_NotifyIconW(NIM_ADD, ref d);
    }

    private void UpdateTip()
    {
        var d = Nid(NIF_TIP);
        d.szTip = TooltipText();
        Shell32.Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private void Balloon(string title, string text)
    {
        var d = Nid(NIF_INFO);
        d.szInfoTitle = title;
        d.szInfo = text;
        d.dwInfoFlags = 1; // NIIF_INFO
        Shell32.Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private string Address()
    {
        string? ip = NetInfo.LanIp();
        return ip is null ? "no network" : $"{Scheme}://{ip}:{_settings.Port}/";
    }

    private string Scheme => _settings.Https ? "https" : "http";

    private string StatusLine()
    {
        if (_server is null) return _startError.Length > 0 ? "Not running: " + _startError : "Not running";
        return _server.HasClient ? $"Phone connected ({_server.ClientIp})" : "Waiting for a phone: " + Address();
    }

    private string TooltipText()
    {
        string t = "Phone Touchpad - " + StatusLine();
        return t.Length > 127 ? t[..127] : t;
    }

    // ---------- server ----------
    private void StartServer()
    {
        StopServer();
        _startError = "";
        try
        {
            var opts = new AgentOptions
            {
                Port = _settings.Port,
                PairPort = _settings.Port + 1,
                Web = _webDir is null ? null : new DirectoryWebAssets(_webDir),
            };
            if (_settings.Https) opts.Certificate = Tls.EnsureCert(_cfgDir, NetInfo.PrivateIps());
            var server = new AgentServer(_injector, _tokens, opts);
            server.ClientChanged += OnClientChanged;
            server.StartAsync().GetAwaiter().GetResult();
            _server = server;
        }
        catch (Exception e)
        {
            _startError = e.InnerException?.Message ?? e.Message;
            Msg($"The agent could not start on port {_settings.Port}.\n\n{_startError}\n\nAnother program (for example the Python agent) may be using the port.", MB_ICONERROR);
        }
        UpdateTip();
    }

    private void StopServer()
    {
        var s = _server;
        _server = null;
        if (s is null) return;
        s.ClientChanged -= OnClientChanged;
        s.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OnClientChanged(bool connected, string ip)
    {
        // Called on a server thread: hand over to the UI thread.
        Post(() =>
        {
            UpdateTip();
            Balloon("Phone Touchpad", connected ? $"Phone connected ({ip})" : "Phone disconnected");
        });
    }

    private void Post(Action a)
    {
        _uiQueue.Enqueue(a);
        User32.PostMessageW(_hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    // ---------- commands ----------
    private int Msg(string text, uint type) => User32.MessageBoxW(_hwnd, text, "Phone Touchpad", type);

    private void PairPhone()
    {
        string? ip = NetInfo.LanIp();
        if (ip is null)
        {
            Msg("No private network address found. Connect the laptop to Wi-Fi (or start the Windows Mobile Hotspot) and try again.", MB_ICONERROR);
            return;
        }
        if (_server?.PairPort is not int pairPort)
        {
            Msg("The agent is not running, so a phone cannot be paired yet.", MB_ICONERROR);
            return;
        }
        string token = _tokens.Create();
        string link = $"{Scheme}://{ip}:{_settings.Port}/?t={token}";
        OpenUrl($"http://127.0.0.1:{pairPort}/pair?u={Uri.EscapeDataString(link)}");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private bool AutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string;
    }

    private void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, "\"" + Environment.ProcessPath + "\"");
        else k.DeleteValue(RunValue, false);
    }

    private void AllowFirewall()
    {
        if (Msg($"Windows Firewall blocks phones from reaching this laptop unless port {_settings.Port} is allowed on private networks.\n\nAdd the rule now? Windows will ask for administrator approval.", MB_YESNO | MB_ICONQUESTION) != IDYES) return;
        try
        {
            Process.Start(new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"Phone Touchpad\" dir=in action=allow protocol=TCP localport={_settings.Port} profile=private")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception) { } // the user declined the administrator prompt
    }

    private void ShowMenu()
    {
        IntPtr menu = User32.CreatePopupMenu();
        User32.AppendMenuW(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero, StatusLine());
        User32.AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        User32.AppendMenuW(menu, MF_STRING, (UIntPtr)CmdPair, "Pair a phone...");
        User32.AppendMenuW(menu, MF_STRING, (UIntPtr)CmdForget, "Forget all phones...");
        User32.AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        User32.AppendMenuW(menu, MF_STRING | (_settings.Https ? MF_CHECKED : 0), (UIntPtr)CmdHttps, "Use HTTPS (screen stays awake)");
        User32.AppendMenuW(menu, MF_STRING | (AutostartOn() ? MF_CHECKED : 0), (UIntPtr)CmdAutostart, "Start with Windows");
        User32.AppendMenuW(menu, MF_STRING, (UIntPtr)CmdFirewall, "Allow in Windows Firewall...");
        User32.AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        User32.AppendMenuW(menu, MF_STRING, (UIntPtr)CmdQuit, "Quit");

        User32.GetCursorPos(out POINT p);
        User32.SetForegroundWindow(_hwnd); // required, or the menu doesn't close when you click elsewhere
        int cmd = User32.TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN, p.x, p.y, 0, _hwnd, IntPtr.Zero);
        User32.PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL, per the TrackPopupMenu documentation
        User32.DestroyMenu(menu);
        Execute(cmd);
    }

    private void Execute(int cmd)
    {
        switch (cmd)
        {
            case CmdPair: PairPhone(); break;
            case CmdForget:
                if (Msg("Forget every paired phone? Each one will need to be paired again.", MB_YESNO | MB_ICONQUESTION) == IDYES)
                {
                    _tokens.RevokeAll();
                    _server?.Controller.ReleaseAll();
                }
                break;
            case CmdHttps:
                _settings = _settings with { Https = !_settings.Https };
                SaveSettings();
                StartServer(); // a paired phone must open the new address once; its token still works
                Msg($"The agent now uses {Scheme.ToUpperInvariant()}. Open {Address()} on the phone." +
                    (_settings.Https ? "\n\nThe phone warns about the certificate once: tap Advanced, then Proceed." : ""), MB_ICONINFORMATION);
                break;
            case CmdAutostart: SetAutostart(!AutostartOn()); break;
            case CmdFirewall: AllowFirewall(); break;
            case CmdQuit: User32.DestroyWindow(_hwnd); break;
        }
    }

    // ---------- window procedure ----------
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_RBUTTONUP) ShowMenu();
                else if (ev == WM_LBUTTONDBLCLK) PairPhone();
                return IntPtr.Zero;
            }
            if (msg == WM_RUN)
            {
                while (_uiQueue.TryDequeue(out var a)) a();
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0) { AddIcon(); return IntPtr.Zero; } // Explorer restarted
            if (msg == WM_DESTROY)
            {
                var d = Nid(0);
                Shell32.Shell_NotifyIconW(NIM_DELETE, ref d);
                User32.PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            Trace.TraceError("tray handler failed: " + e); // never let an exception unwind into Windows
        }
        return User32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        StopServer();
        if (_icon != IntPtr.Zero) User32.DestroyIcon(_icon);
    }
}
