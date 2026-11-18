using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TouchpadAgent.Core;

namespace TouchpadAgent.Tests;

public static class UnitTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h);

    public static void Register()
    {
        // ---------- protocol ----------
        // The hex strings were produced by agent/protocol.py, so this checks both agents speak the same wire format.
        T.Test("protocol: encoders match the Python agent's bytes", () =>
        {
            T.Bytes(Hex("010100fbff0700"), Protocol.EncodeMove(1, -5, 7));
            T.Bytes(Hex("02ffff64009cff"), Protocol.EncodeScroll(65535, 100, -100));
            T.Bytes(Hex("030101"), Protocol.EncodeButton(1, 1));
            T.Bytes(Hex("04410001"), Protocol.EncodeKey(0x41, 1));
            T.Bytes(Hex("0568c3a96c6c6f20e29c9320f09f9880"), Protocol.EncodeText("héllo ✓ 😀"));
            T.Bytes(Hex("0788ff"), Protocol.EncodeZoom(-120));
            T.Bytes(Hex("06efbeadde"), Protocol.EncodePing(0xDEADBEEF));
            T.Bytes(Hex("81efbeadde"), Protocol.EncodePong(0xDEADBEEF));
        });

        T.Test("protocol: parse round trip", () =>
        {
            var m = Protocol.Parse(Hex("010100fbff0700"))!.Value;
            T.True(m.Kind == MsgKind.Move && m.A == -5 && m.B == 7);
            m = Protocol.Parse(Hex("02ffff64009cff"))!.Value;
            T.True(m.Kind == MsgKind.Scroll && m.A == 100 && m.B == -100);
            m = Protocol.Parse(Hex("030101"))!.Value;
            T.True(m.Kind == MsgKind.Button && m.A == 1 && m.B == 1);
            m = Protocol.Parse(Hex("04410001"))!.Value;
            T.True(m.Kind == MsgKind.Key && m.A == 0x41 && m.B == 1);
            m = Protocol.Parse(Hex("0568c3a96c6c6f20e29c9320f09f9880"))!.Value;
            T.Equal("héllo ✓ 😀", m.Text);
            m = Protocol.Parse(Hex("0788ff"))!.Value;
            T.True(m.Kind == MsgKind.Zoom && m.A == -120);
            m = Protocol.Parse(Hex("06efbeadde"))!.Value;
            T.True(m.Kind == MsgKind.Ping && m.Token == 0xDEADBEEF);
        });

        T.Test("protocol: malformed frames are rejected", () =>
        {
            foreach (string bad in new[]
            {
                "", "00", "7f", "81efbeadde",           // empty, unknown types, a PONG sent by a client
                "0101", "010100fbff07", "010100fbff070000", // wrong move lengths
                "030301", "030102", "0301",             // bad button, bad state, short
                "04000001", "04ff0001", "04410002",     // vk 0, vk 255, bad state
                "05", "05ff",                            // empty text, invalid UTF-8
                "06ef", "0788", "0788ff00",              // short / long ping and zoom
            })
                T.True(Protocol.Parse(Hex(bad)) is null, "should reject " + bad);
            var big = new byte[1 + Protocol.MaxTextBytes + 1];
            big[0] = Protocol.TText;
            Array.Fill(big, (byte)'a', 1, big.Length - 1);
            T.True(Protocol.Parse(big) is null, "text over the limit");
            Array.Resize(ref big, 1 + Protocol.MaxTextBytes);
            T.True(Protocol.Parse(big) is not null, "text at the limit");
        });

        // ---------- auth ----------
        T.Test("tokens: only hashes are stored, in the Python agent's file format", () =>
        {
            string dir = T.TempDir(), path = Path.Combine(dir, "config.json");
            var store = new TokenStore(path);
            T.True(!store.HasTokens());
            string token = store.Create();
            T.Equal(22, token.Length, "128-bit URL-safe token");
            T.True(token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
            string json = File.ReadAllText(path);
            T.True(!json.Contains(token), "token itself must not be stored");
            using var doc = JsonDocument.Parse(json);
            string stored = doc.RootElement.GetProperty("tokens")[0].GetString()!;
            T.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), stored);
            T.True(store.Verify(token));
            T.True(!store.Verify("nope") && !store.Verify("") && !store.Verify(null));
            T.True(new TokenStore(path).Verify(token), "survives a restart");
            store.RevokeAll();
            T.True(!store.Verify(token) && !new TokenStore(path).HasTokens());
        });

        T.Test("tokens: a token hash written by the Python agent is accepted", () =>
        {
            string dir = T.TempDir(), path = Path.Combine(dir, "config.json");
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("abc"))).ToLowerInvariant(); // sha256("abc")
            T.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
            File.WriteAllText(path, "{\"tokens\": [\"" + hash + "\"]}");
            T.True(new TokenStore(path).Verify("abc"));
        });

        T.Test("rate limiter: 5 failures per minute per IP", () =>
        {
            var now = TimeSpan.Zero;
            var rl = new RateLimiter(5, 60, () => now);
            for (int i = 0; i < 5; i++) { T.True(rl.Allow("1.2.3.4")); rl.RecordFailure("1.2.3.4"); }
            T.True(!rl.Allow("1.2.3.4"));
            T.True(rl.Allow("5.6.7.8"), "other IPs unaffected");
            now = TimeSpan.FromSeconds(61);
            T.True(rl.Allow("1.2.3.4"), "window expired");
        });

        // ---------- network ----------
        T.Test("netinfo: private addresses allowed, public rejected (same cases as the Python tests)", () =>
        {
            foreach (var ip in new[] { "192.168.1.20", "10.0.0.5", "172.16.0.1", "172.31.255.254", "169.254.10.10", "127.0.0.1", "::1", "fe80::1", "fd12:3456::1", "::ffff:192.168.0.9", "fe80::1%wlan0" })
                T.True(NetInfo.IsAllowedIp(ip), "should allow " + ip);
            foreach (var ip in new[] { "8.8.8.8", "1.1.1.1", "172.32.0.1", "11.0.0.1", "2001:4860:4860::8888", "::ffff:8.8.8.8", "", null, "nonsense" })
                T.True(!NetInfo.IsAllowedIp(ip), "should reject " + (ip ?? "null"));
            T.True(!NetInfo.IsAllowedIp((IPAddress?)null));
        });

        // ---------- input controller ----------
        T.Test("controller: consecutive moves merge, buttons are barriers", () =>
        {
            var inj = new DryRunInjector();
            using var c = new InputController(inj, startThread: false);
            c.Move(1, 2); c.Move(3, 4); c.Move(5, 6);
            c.Button(0, 1);
            c.Move(1, 1);
            c.Button(0, 0);
            c.Flush();
            T.Equal("move 9 12|button 0 1|move 1 1|button 0 0", string.Join("|", inj.Events()));
            T.Equal(4L, c.Packets);
            T.Equal(1L, c.Flushes);
        });

        T.Test("controller: scroll and zoom merge separately and results are clamped", () =>
        {
            var inj = new DryRunInjector();
            using var c = new InputController(inj, startThread: false);
            c.Scroll(0, 100); c.Scroll(0, 50);
            c.Zoom(10); c.Zoom(20);
            c.Move(30000, -30000);
            c.Scroll(0, 30000);
            c.Zoom(-30000);
            c.Flush();
            T.Equal("scroll 0 150|zoom 30|move 4000 -4000|scroll 0 6000|zoom -6000", string.Join("|", inj.Events()));
        });

        T.Test("controller: moves that cancel out inject nothing", () =>
        {
            var inj = new DryRunInjector();
            using var c = new InputController(inj, startThread: false);
            c.Move(5, 5); c.Move(-5, -5);
            c.Flush();
            T.Equal(0, inj.Events().Length);
        });

        T.Test("controller: release_all drops stale queued ops and frees held input", () =>
        {
            var inj = new DryRunInjector();
            using var c = new InputController(inj, startThread: false);
            c.Button(0, 1); c.Key(0x11, 1);
            c.Flush();
            c.Move(10, 10); c.Button(1, 1); // queued but not yet injected
            c.ReleaseAll();
            c.Flush();
            T.True(!inj.AnyButtonDown() && !inj.AnyKeyDown());
            T.True(!inj.Events().Contains("move 10 10") && !inj.Events().Contains("button 1 1"), "stale ops must not run after release");
        });

        T.Test("controller: the injector thread applies ops without a manual flush", async () =>
        {
            var inj = new DryRunInjector();
            using var c = new InputController(inj);
            c.Move(7, 8);
            await T.WaitFor(() => inj.TotalDx == 7 && inj.TotalDy == 8, what: "move to be injected");
        });

        T.Test("controller: an injector failure does not kill the injector thread", async () =>
        {
            var inj = new ThrowingOnce();
            using var c = new InputController(inj);
            c.Move(1, 1);
            await T.WaitFor(() => inj.Calls >= 1);
            c.Move(2, 2);
            await T.WaitFor(() => inj.Calls >= 2, what: "second move after a failure");
        });

        // ---------- Windows injector (against a fake user32) ----------
        T.Test("windows injector: INPUT structure sizes match the Win32 ABI", () =>
        {
            int size = Marshal.SizeOf<INPUT>();
            T.Equal(IntPtr.Size == 8 ? 40 : 28, size, "sizeof(INPUT)");
            T.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf<INPUTUNION>(), "sizeof(union)");
        });

        T.Test("windows injector: move, scroll, buttons", () =>
        {
            var fake = new FakeNative();
            var w = new WindowsInjector(fake);
            w.Move(-7, 9);
            w.Scroll(0, -120);
            w.Scroll(240, 120);
            w.Button(2, true); w.Button(2, false); w.Button(7, true);
            var all = fake.Sent;
            T.Equal(5, all.Count, "the invalid button index sends nothing");
            T.True(all[0].Length == 1 && all[0][0].type == 0 && all[0][0].u.mi.dwFlags == 0x0001 && all[0][0].u.mi.dx == -7 && all[0][0].u.mi.dy == 9);
            T.Equal(0xFFFFFF88u, all[1][0].u.mi.mouseData, "negative wheel delta is two's complement");
            T.Equal(0x0800u, all[1][0].u.mi.dwFlags);
            T.True(all[2].Length == 2 && all[2][0].u.mi.dwFlags == 0x0800 && all[2][1].u.mi.dwFlags == 0x1000 && all[2][1].u.mi.mouseData == 240);
            T.Equal(0x0020u, all[3][0].u.mi.dwFlags, "middle down");
            T.Equal(0x0040u, all[4][0].u.mi.dwFlags, "middle up");
        });

        T.Test("windows injector: zoom is one atomic Ctrl+wheel call", () =>
        {
            var fake = new FakeNative();
            new WindowsInjector(fake).Zoom(120);
            T.Equal(1, fake.Sent.Count);
            var r = fake.Sent[0];
            T.Equal(3, r.Length);
            T.True(r[0].type == 1 && r[0].u.ki.wVk == 0x11 && r[0].u.ki.dwFlags == 0);
            T.True(r[1].type == 0 && r[1].u.mi.dwFlags == 0x0800 && r[1].u.mi.mouseData == 120);
            T.True(r[2].u.ki.wVk == 0x11 && r[2].u.ki.dwFlags == 0x0002);
        });

        T.Test("windows injector: keys use scan codes and the extended flag where needed", () =>
        {
            var fake = new FakeNative();
            var w = new WindowsInjector(fake);
            w.Key(0x41, true); w.Key(0x41, false); w.Key(0x25, true); w.Key(0x5B, false);
            var k = fake.Sent.Select(s => s[0].u.ki).ToArray();
            T.Equal((ushort)(0x41 + 0x100), k[0].wScan, "fake scan code");
            T.Equal(0u, k[0].dwFlags);
            T.Equal(0x0002u, k[1].dwFlags);
            T.Equal(0x0001u, k[2].dwFlags, "left arrow is extended");
            T.Equal(0x0003u, k[3].dwFlags, "Win key up is extended + keyup");
        });

        T.Test("windows injector: text is sent as Unicode events, in chunks of at most 128", () =>
        {
            var fake = new FakeNative();
            var w = new WindowsInjector(fake);
            w.Text("a\r\nb\tc\u0001😀");
            var recs = fake.Sent.SelectMany(s => s).ToArray();
            // a(2) Enter(2) b(2) Tab(2) c(2) then the emoji's two UTF-16 units (4); the control character is dropped
            T.Equal(14, recs.Length);
            T.True(recs[0].u.ki.wVk == 0 && recs[0].u.ki.wScan == 'a' && recs[0].u.ki.dwFlags == 0x0004);
            T.True(recs[1].u.ki.dwFlags == 0x0006);
            T.True(recs[2].u.ki.wVk == 0x0D && recs[4].u.ki.wScan == 'b' && recs[6].u.ki.wVk == 0x09);
            T.Equal((ushort)0xD83D, recs[10].u.ki.wScan, "high surrogate");
            T.Equal((ushort)0xDE00, recs[12].u.ki.wScan, "low surrogate");

            fake.Sent.Clear();
            w.Text(new string('x', 500));
            T.True(fake.Sent.All(s => s.Length <= WindowsInjector.TextChunk), "chunk size");
            T.Equal(1000, fake.Sent.Sum(s => s.Length));
        });

        T.Test("windows injector: release_all frees held buttons and keys, and a blocked SendInput warns once", () =>
        {
            var fake = new FakeNative();
            var w = new WindowsInjector(fake);
            w.Button(0, true); w.Key(0x10, true); w.Key(0x11, true);
            fake.Sent.Clear();
            w.ReleaseAll();
            T.Equal(3, fake.Sent.Count);
            T.Equal(0x0004u, fake.Sent[0][0].u.mi.dwFlags);
            T.True(fake.Sent[1][0].u.ki.wVk == 0x10 && fake.Sent[2][0].u.ki.wVk == 0x11);

            var blocked = new FakeNative { Accept = false };
            var w2 = new WindowsInjector(blocked);
            int warnings = 0;
            w2.Warn = _ => warnings++;
            w2.Move(1, 1); w2.Move(1, 1); w2.Key(0x41, true);
            T.Equal(1, warnings);
        });

        // ---------- TLS ----------
        T.Test("tls: a certificate is created, reused, and renewed when addresses change", () =>
        {
            string dir = T.TempDir();
            var c1 = Tls.EnsureCert(dir, new[] { "192.168.1.5" });
            T.True(c1.HasPrivateKey);
            var c2 = Tls.EnsureCert(dir, new[] { "192.168.1.5" });
            T.Equal(c1.Thumbprint, c2.Thumbprint, "reused");
            var c3 = Tls.EnsureCert(dir, new[] { "192.168.1.5", "10.0.0.9" });
            T.True(c3.Thumbprint != c1.Thumbprint, "new address needs a new certificate");
            T.True(File.ReadAllLines(Path.Combine(dir, Tls.IpsName)).Contains("10.0.0.9"));
        });

        // ---------- web assets ----------
        T.Test("assets: every file in web/ is embedded, and directory serving rejects path escapes", () =>
        {
            string web = T.RepoWebDir();
            var embedded = new EmbeddedWebAssets();
            foreach (var f in Directory.GetFiles(web))
            {
                string name = Path.GetFileName(f);
                T.True(embedded.Get(name) is not null, "missing from the .exe: " + name);
                T.Bytes(File.ReadAllBytes(f), embedded.Get(name)!, name);
            }
            T.True(embedded.Get("nope.js") is null && embedded.Get("../app.js") is null);
            var dir = new DirectoryWebAssets(web);
            T.True(dir.Get("index.html") is not null);
            T.True(dir.Get("../README.md") is null && dir.Get("..\\README.md") is null && dir.Get("/etc/passwd") is null);
            T.True(Assets.Embedded("pair.html") is not null && Assets.Embedded("qrcode.js") is not null);
        });
    }

    private sealed class ThrowingOnce : IInputInjector
    {
        public int Calls;
        public (int w, int h) ScreenSize() => (1, 1);
        public void Move(int dx, int dy) { if (Interlocked.Increment(ref Calls) == 1) throw new InvalidOperationException("boom"); }
        public void Scroll(int dx, int dy) { }
        public void Zoom(int delta) { }
        public void Button(int button, bool down) { }
        public void Key(int vk, bool down) { }
        public void Text(string text) { }
        public void ReleaseAll() { }
    }

    private sealed class FakeNative : INativeInput
    {
        public readonly List<INPUT[]> Sent = new();
        public bool Accept = true;
        public uint SendInput(INPUT[] inputs, int count)
        {
            Sent.Add(inputs.Take(count).ToArray()); // copy: the injector reuses its move buffer
            return Accept ? (uint)count : 0;
        }
        public ushort ScanCode(uint vk) => (ushort)(vk + 0x100);
        public (int w, int h) ScreenSize() => (1920, 1080);
    }
}
