// End-to-end tests: a real Kestrel server on a free loopback port, a real WebSocket client, a recording injector.
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TouchpadAgent.Core;

namespace TouchpadAgent.Tests;

public static class ServerTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public DryRunInjector Injector = new();
        public TokenStore Tokens;
        public AgentServer Server;
        public string Token;
        public Rig(AgentOptions? opts = null, RateLimiter? limiter = null)
        {
            Tokens = new TokenStore(Path.Combine(T.TempDir(), "config.json"));
            Token = Tokens.Create();
            opts ??= new AgentOptions();
            opts.Port = 0;
            opts.BindAddress = IPAddress.Loopback;
            opts.Web ??= new DirectoryWebAssets(T.RepoWebDir());
            Server = new AgentServer(Injector, Tokens, opts, limiter);
        }
        public async Task<Rig> Start() { await Server.StartAsync(); return this; }
        public ValueTask DisposeAsync() => Server.DisposeAsync();
        public string Url(string scheme = "ws") => $"{scheme}://127.0.0.1:{Server.Port}";
    }

    private sealed class Phone : IDisposable
    {
        public readonly ClientWebSocket Ws = new();
        private readonly byte[] _buf = new byte[4096];
        public async Task Connect(string url, bool acceptAnyCert = false)
        {
            if (acceptAnyCert) Ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            await Ws.ConnectAsync(new Uri(url + "/ws"), CancellationToken.None);
        }
        public Task SendText(string s) => Ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, CancellationToken.None);
        public Task Send(byte[] b) => Ws.SendAsync(b, WebSocketMessageType.Binary, true, CancellationToken.None);
        /// <summary>Next message as (type, bytes); null when the server closed the socket.</summary>
        public async Task<(WebSocketMessageType, byte[])?> Receive()
        {
            var r = await Ws.ReceiveAsync(_buf, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            return (r.MessageType, _buf.AsSpan(0, r.Count).ToArray());
        }
        public async Task<JsonElement?> ReceiveJson()
        {
            var m = await Receive();
            return m is null ? null : JsonDocument.Parse(m.Value.Item2).RootElement.Clone();
        }
        public async Task<JsonElement> Login(string token)
        {
            await SendText($"{{\"t\":\"auth\",\"token\":\"{token}\",\"v\":1}}");
            return (await ReceiveJson())!.Value;
        }
        public void Dispose() => Ws.Dispose();
    }

    public static void Register()
    {
        T.Test("server: a paired phone logs in, moves the cursor, and gets pongs", async () =>
        {
            await using var rig = await new Rig().Start();
            using var p = new Phone();
            await p.Connect(rig.Url());
            var ok = await p.Login(rig.Token);
            T.Equal("ok", ok.GetProperty("t").GetString());
            T.Equal(1920, ok.GetProperty("screen").GetProperty("w").GetInt32());
            await p.Send(Protocol.EncodeMove(0, 10, -4));
            await p.Send(Protocol.EncodeMove(1, 5, 5));
            await p.Send(Protocol.EncodePing(12345));
            var pong = await p.Receive();
            T.Bytes(Protocol.EncodePong(12345), pong!.Value.Item2);
            await T.WaitFor(() => rig.Injector.TotalDx == 15 && rig.Injector.TotalDy == 1, what: "moves");
            await p.Send(Protocol.EncodeButton(0, 1));
            await p.Send(Protocol.EncodeKey(0x41, 1));
            await p.Send(Protocol.EncodeText("hi"));
            await T.WaitFor(() => rig.Injector.Events().Contains("text hi"), what: "text");
            T.True(rig.Injector.AnyButtonDown() && rig.Injector.AnyKeyDown());
        });

        T.Test("server: ordering of button and move is preserved", async () =>
        {
            await using var rig = await new Rig().Start();
            using var p = new Phone();
            await p.Connect(rig.Url());
            await p.Login(rig.Token);
            await p.Send(Protocol.EncodeButton(0, 1));
            await p.Send(Protocol.EncodeMove(0, 3, 3));
            await p.Send(Protocol.EncodeButton(0, 0));
            await T.WaitFor(() => rig.Injector.Events().Length >= 3, what: "three events");
            T.Equal("button 0 1|move 3 3|button 0 0", string.Join("|", rig.Injector.Events()));
        });

        T.Test("server: wrong token, wrong version and junk are rejected", async () =>
        {
            await using var rig = await new Rig().Start();
            foreach (var (msg, reason) in new[]
            {
                ("{\"t\":\"auth\",\"token\":\"wrong\",\"v\":1}", "bad_token"),
                ("{\"t\":\"auth\",\"token\":\"x\",\"v\":2}", "bad_version"),
                ("{\"t\":\"hello\"}", "bad_request"),
                ("not json", "bad_request"),
                ("[1,2]", "bad_request"),
            })
            {
                using var p = new Phone();
                await p.Connect(rig.Url());
                await p.SendText(msg);
                var r = (await p.ReceiveJson())!.Value;
                T.Equal("err", r.GetProperty("t").GetString());
                T.Equal(reason, r.GetProperty("reason").GetString(), msg);
            }
            using var bin = new Phone();
            await bin.Connect(rig.Url());
            await bin.Send(Protocol.EncodeMove(0, 1, 1)); // binary before auth
            T.Equal("bad_request", (await bin.ReceiveJson())!.Value.GetProperty("reason").GetString());
            T.Equal(0L, rig.Injector.MoveCalls);
        });

        T.Test("server: a silent connection is dropped after the auth timeout", async () =>
        {
            await using var rig = await new Rig(new AgentOptions { AuthTimeoutSeconds = 0.3 }).Start();
            using var p = new Phone();
            await p.Connect(rig.Url());
            T.Equal("auth_timeout", (await p.ReceiveJson())!.Value.GetProperty("reason").GetString());
        });

        T.Test("server: five bad logins from one IP trigger the rate limit", async () =>
        {
            await using var rig = await new Rig().Start();
            for (int i = 0; i < 5; i++)
            {
                using var p = new Phone();
                await p.Connect(rig.Url());
                T.Equal("bad_token", (await p.Login("wrong")).GetProperty("reason").GetString());
            }
            using var good = new Phone();
            await good.Connect(rig.Url());
            T.Equal("rate_limited", (await good.ReceiveJson())!.Value.GetProperty("reason").GetString(), "even the right token waits");
        });

        T.Test("server: a new phone replaces the old one and the old one cannot inject", async () =>
        {
            await using var rig = await new Rig().Start();
            using var a = new Phone();
            await a.Connect(rig.Url());
            await a.Login(rig.Token);
            await a.Send(Protocol.EncodeButton(0, 1)); // phone A holds the left button
            await T.WaitFor(() => rig.Injector.AnyButtonDown());

            using var b = new Phone();
            await b.Connect(rig.Url());
            T.Equal("ok", (await b.Login(rig.Token)).GetProperty("t").GetString());
            await T.WaitFor(() => !rig.Injector.AnyButtonDown(), what: "A's button to be released");
            await a.Send(Protocol.EncodeMove(0, 50, 50)); // stale socket: must be ignored
            await b.Send(Protocol.EncodeMove(0, 1, 1));
            await T.WaitFor(() => rig.Injector.TotalDx >= 1, what: "B's move");
            await Task.Delay(100);
            T.Equal(1L, rig.Injector.TotalDx, "A's move was injected");
        });

        T.Test("server: disconnecting releases held buttons and keys", async () =>
        {
            await using var rig = await new Rig().Start();
            using var p = new Phone();
            await p.Connect(rig.Url());
            await p.Login(rig.Token);
            await p.Send(Protocol.EncodeButton(0, 1));
            await p.Send(Protocol.EncodeKey(0x11, 1));
            await T.WaitFor(() => rig.Injector.AnyButtonDown() && rig.Injector.AnyKeyDown());
            await p.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            await T.WaitFor(() => !rig.Injector.AnyButtonDown() && !rig.Injector.AnyKeyDown(), what: "release on disconnect");
            await T.WaitFor(() => !rig.Server.HasClient);
        });

        T.Test("server: a phone that goes silent is dropped and its input released", async () =>
        {
            await using var rig = await new Rig(new AgentOptions { IdleTimeoutSeconds = 0.5 }).Start();
            using var p = new Phone();
            await p.Connect(rig.Url());
            await p.Login(rig.Token);
            await p.Send(Protocol.EncodeButton(0, 1));
            await T.WaitFor(() => rig.Injector.AnyButtonDown());
            await T.WaitFor(() => !rig.Injector.AnyButtonDown(), 3000, "watchdog to release the button");
            T.True(!rig.Server.HasClient);
        });

        T.Test("server: a phone that keeps pinging is not dropped by the watchdog", async () =>
        {
            await using var rig = await new Rig(new AgentOptions { IdleTimeoutSeconds = 0.6 }).Start();
            using var p = new Phone();
            await p.Connect(rig.Url());
            await p.Login(rig.Token);
            for (int i = 0; i < 6; i++)
            {
                await p.Send(Protocol.EncodePing((uint)i));
                await p.Receive();
                await Task.Delay(250);
            }
            T.True(rig.Server.HasClient);
        });

        T.Test("server: ClientChanged fires for connect and disconnect", async () =>
        {
            await using var rig = await new Rig().Start();
            var events = new List<string>();
            rig.Server.ClientChanged += (c, ip) => { lock (events) events.Add($"{c}:{ip}"); };
            using var p = new Phone();
            await p.Connect(rig.Url());
            await p.Login(rig.Token);
            await T.WaitFor(() => { lock (events) return events.Count == 1; });
            await p.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            await T.WaitFor(() => { lock (events) return events.Count == 2; });
            T.Equal("True:127.0.0.1", events[0]);
            T.Equal("False:127.0.0.1", events[1]);
        });

        T.Test("http: the phone client is served with no-cache, 404 otherwise, path escapes refused", async () =>
        {
            await using var rig = await new Rig().Start();
            using var http = new HttpClient { BaseAddress = new Uri(rig.Url("http")) };
            var index = await http.GetAsync("/");
            T.Equal(HttpStatusCode.OK, index.StatusCode);
            T.True((await index.Content.ReadAsStringAsync()).Contains("<title>Touchpad</title>"));
            T.Equal("text/html; charset=utf-8", index.Content.Headers.ContentType!.ToString());
            T.Equal("no-cache", index.Headers.CacheControl!.ToString());
            var js = await http.GetAsync("/app.js");
            T.Equal(HttpStatusCode.OK, js.StatusCode);
            T.True(js.Content.Headers.ContentType!.MediaType == "text/javascript");
            T.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/missing.js")).StatusCode);
            T.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/%2e%2e/README.md")).StatusCode);
            T.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/pair")).StatusCode); // the QR page is not on the phone port
            T.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync("/", new StringContent(""))).StatusCode);
            T.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/ws")).StatusCode); // not a WebSocket upgrade
        });

        T.Test("http: the embedded client is served when no web dir is given", async () =>
        {
            var opts = new AgentOptions { Web = new EmbeddedWebAssets() };
            await using var rig = await new Rig(opts).Start();
            using var http = new HttpClient { BaseAddress = new Uri(rig.Url("http")) };
            T.True((await http.GetStringAsync("/")).Contains("<title>Touchpad</title>"));
            T.True((await http.GetStringAsync("/layout.js")).Contains("mapPoint"));
        });

        T.Test("pairing page: served only on the loopback pair port, with the QR library", async () =>
        {
            int pairPort = FreePort();
            await using var rig = await new Rig(new AgentOptions { PairPort = pairPort }).Start();
            T.Equal(pairPort, rig.Server.PairPort ?? -1);
            T.True(rig.Server.Port != pairPort && rig.Server.Port > 0);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{pairPort}") };
            var page = await http.GetStringAsync("/pair?u=http%3A%2F%2F192.168.1.5%3A8765%2F%3Ft%3Dabc");
            T.True(page.Contains("Pair a phone") && page.Contains("qrcode"));
            var lib = await http.GetStringAsync("/qrcode.js");
            T.True(lib.Contains("QR Code Generator") && lib.Contains("MIT"));
            T.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/app.js")).StatusCode); // only the QR page lives here
        });

        T.Test("https: the phone can connect over TLS with the self-signed certificate", async () =>
        {
            var cert = Tls.EnsureCert(T.TempDir(), new[] { "127.0.0.1" });
            await using var rig = await new Rig(new AgentOptions { Certificate = cert }).Start();
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(rig.Url("https")) };
            T.True((await http.GetStringAsync("/")).Contains("Touchpad"));
            using var p = new Phone();
            await p.Connect(rig.Url("wss"), acceptAnyCert: true);
            T.Equal("ok", (await p.Login(rig.Token)).GetProperty("t").GetString());
            await p.Send(Protocol.EncodeMove(0, 2, 3));
            await T.WaitFor(() => rig.Injector.TotalDx == 2);
        });

        T.Test("server: 2000 move packets in a burst are all accounted for", async () =>
        {
            await using var rig = await new Rig().Start();
            using var p = new Phone();
            await p.Connect(rig.Url());
            await p.Login(rig.Token);
            for (ushort i = 0; i < 2000; i++) await p.Send(Protocol.EncodeMove(i, 1, 2));
            await T.WaitFor(() => rig.Injector.TotalDx == 2000 && rig.Injector.TotalDy == 4000, 5000, "all moves");
            T.True(rig.Injector.MoveCalls <= 2000);
            T.Equal(2000L, rig.Server.Controller.Packets);
        });
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
