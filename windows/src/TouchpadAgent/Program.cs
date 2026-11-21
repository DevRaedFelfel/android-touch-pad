// Phone Touchpad agent for Windows: a tray application (no console window) that does what
// "python -m agent" does. Command-line options: --port N  --https  --dry-run  --web-dir PATH
//                                               --revoke-all  --self-test  --help
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using TouchpadAgent.Core;

namespace TouchpadAgent;

public static class Program
{
    private const string Help =
        "Phone Touchpad agent (tray app)\n\n" +
        "  --port N       listen on port N (default 8765, remembered)\n" +
        "  --https        serve HTTPS with a self-signed certificate (remembered)\n" +
        "  --dry-run      do not inject input\n" +
        "  --web-dir P    serve the phone client from folder P instead of the built-in copy\n" +
        "  --revoke-all   forget all paired phones and exit\n" +
        "  --self-test    start the agent, move the cursor through a local WebSocket and exit (Windows only)\n";

    [STAThread]
    public static int Main(string[] args)
    {
        // A windowed app has no console of its own; when started from a terminal, borrow that terminal for --help and --self-test output.
        if (OperatingSystem.IsWindows() && args.Length > 0) Kernel32.AttachConsole(0xFFFFFFFF); // ATTACH_PARENT_PROCESS

        int? port = null;
        bool https = false, dry = false, revoke = false, selfTest = false, help = false;
        string? webDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int p) && p is > 0 and < 65535: port = p; i++; break;
                case "--https": https = true; break;
                case "--dry-run": dry = true; break;
                case "--web-dir" when i + 1 < args.Length: webDir = args[++i]; break;
                case "--revoke-all": revoke = true; break;
                case "--self-test": selfTest = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default: Console.Error.WriteLine("Unknown or incomplete option: " + args[i] + "\n\n" + Help); return 2;
            }
        }
        if (help) { Console.WriteLine(Help); return 0; }

        string cfgDir = Config.Dir();
        var tokens = new TokenStore(Path.Combine(cfgDir, "config.json"));
        if (revoke)
        {
            tokens.RevokeAll();
            Console.WriteLine("All paired phones have been forgotten.");
            return 0;
        }

        if (!OperatingSystem.IsWindows() && !dry)
        {
            Console.Error.WriteLine("Input injection is only supported on Windows. Use --dry-run to test on this machine.");
            return 1;
        }
        if (selfTest)
        {
            if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("--self-test needs Windows."); return 1; }
            return SelfTest.Run();
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The tray icon is Windows only.");
            return 1;
        }
        return RunTray(cfgDir, tokens, port, https, dry, webDir);
    }

    [SupportedOSPlatform("windows")]
    private static int RunTray(string cfgDir, TokenStore tokens, int? port, bool https, bool dry, string? webDir)
    {
        using var mutex = new Mutex(true, @"Local\PhoneTouchpadAgent", out bool first);
        if (!first)
        {
            User32.MessageBoxW(IntPtr.Zero, "Phone Touchpad is already running. Look for its icon in the notification area.", "Phone Touchpad", 0x40);
            return 0;
        }

        var settings = TrayApp.LoadSettings(cfgDir);
        if (port is int p) settings = settings with { Port = p };
        if (https) settings = settings with { Https = true };

        IInputInjector injector = dry ? new DryRunInjector() : new WindowsInjector(new Win32Input());
        if (injector is WindowsInjector wi) wi.Warn = m => System.Diagnostics.Trace.TraceWarning(m);

        Kernel32.timeBeginPeriod(1); // finer timers: no 15 ms steps in the injector
        Kernel32.SetPriorityClass(Kernel32.GetCurrentProcess(), 0x00000080); // HIGH_PRIORITY_CLASS
        PowerTuning.DisableThrottling(); // a tray app has no visible window, so Windows 11 would otherwise throttle it when idle
        try
        {
            using var app = new TrayApp(tokens, injector, cfgDir, settings, webDir);
            return app.Run();
        }
        finally
        {
            Kernel32.timeEndPeriod(1);
        }
    }
}

/// <summary>--self-test: proves on a real Windows machine that a phone message ends up moving the real cursor.</summary>
[SupportedOSPlatform("windows")]
internal static class SelfTest
{
    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "phone-touchpad-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tokens = new TokenStore(Path.Combine(dir, "config.json"));
            string token = tokens.Create();
            var injector = new WindowsInjector(new Win32Input());
            var server = new AgentServer(injector, tokens, new AgentOptions { Port = 0, BindAddress = System.Net.IPAddress.Loopback });
            server.StartAsync().GetAwaiter().GetResult();
            try { return RunClient(server.Port, token).GetAwaiter().GetResult(); }
            finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static async Task<int> RunClient(int port, string token)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"t\":\"auth\",\"token\":\"{token}\",\"v\":1}}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buf = new byte[512];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        string reply = Encoding.UTF8.GetString(buf, 0, r.Count);
        if (!reply.Contains("\"ok\"")) { Console.Error.WriteLine("SELF-TEST FAILED: auth reply " + reply); return 1; }

        User32.SetCursorPos(400, 400);
        User32.GetCursorPos(out POINT before);
        for (ushort i = 0; i < 10; i++)
            await ws.SendAsync(Protocol.EncodeMove(i, 20, 10), WebSocketMessageType.Binary, true, CancellationToken.None);
        for (int tries = 0; tries < 50; tries++)
        {
            await Task.Delay(40);
            User32.GetCursorPos(out POINT after);
            // Windows may scale relative moves (pointer speed, "Enhance pointer precision"): check the direction, not the distance.
            if (after.x > before.x && after.y > before.y)
            {
                Console.WriteLine($"SELF-TEST OK: cursor moved from {before.x},{before.y} to {after.x},{after.y}");
                return 0;
            }
        }
        Console.Error.WriteLine("SELF-TEST FAILED: the cursor did not move");
        return 1;
    }
}
