// HTTP + WebSocket server (agent/server.py): serves the phone web client and a /ws input socket.
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TouchpadAgent.Core;

public sealed class AgentOptions
{
    public int Port { get; set; } = 8765; // 0 = pick a free port (tests)
    public X509Certificate2? Certificate { get; set; } // set to serve HTTPS
    public IPAddress BindAddress { get; set; } = IPAddress.Any;
    public int? PairPort { get; set; } // loopback-only plain-HTTP listener for the QR page (null = none)
    public double AuthTimeoutSeconds { get; set; } = 3;
    public double IdleTimeoutSeconds { get; set; } = 3;
    public IWebAssets? Web { get; set; } // default: the embedded client
}

public sealed class AgentServer : IAsyncDisposable
{
    private readonly IInputInjector _injector;
    private readonly TokenStore _tokens;
    private readonly AgentOptions _opt;
    private readonly RateLimiter _limiter;
    private readonly IWebAssets _web;
    private readonly InputController _ctrl;
    private readonly object _gate = new();
    private WebSocket? _active;
    private string _activeIp = "";
    private long _lastRx; // Stopwatch timestamp of the last frame from the active client
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private Task? _watchdog;

    public int Port { get; private set; }
    public int? PairPort { get; private set; }
    public bool HasClient { get { lock (_gate) return _active is not null; } }
    public string? ClientIp { get { lock (_gate) return _active is null ? null : _activeIp; } }
    public InputController Controller => _ctrl;

    /// <summary>Raised (on a worker thread) when a phone connects (true) or the active phone goes away (false).</summary>
    public event Action<bool, string>? ClientChanged;

    public AgentServer(IInputInjector injector, TokenStore tokens, AgentOptions? options = null, RateLimiter? limiter = null)
    {
        _injector = injector;
        _tokens = tokens;
        _opt = options ?? new AgentOptions();
        _limiter = limiter ?? new RateLimiter();
        _web = _opt.Web ?? new EmbeddedWebAssets();
        _ctrl = new InputController(injector);
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(_opt.BindAddress, _opt.Port, lo =>
            {
                if (_opt.Certificate is not null) lo.UseHttps(_opt.Certificate);
            });
            if (_opt.PairPort is int pp) k.Listen(IPAddress.Loopback, pp);
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero }); // liveness is the app-level ping
        app.Run(Handle);
        await app.StartAsync();
        _app = app;

        // Find the ports we actually got (matters when Port is 0).
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        foreach (var a in addresses)
        {
            var uri = new Uri(a.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost"));
            if (_opt.PairPort is int pp && uri.Host is "localhost" or "127.0.0.1" && uri.Port == pp && _opt.Port != pp) PairPort = uri.Port;
            else Port = uri.Port;
        }
        if (_opt.PairPort is int want && PairPort is null) PairPort = want;

        _cts = new CancellationTokenSource();
        _watchdog = Task.Run(() => WatchdogAsync(_cts.Token));
    }

    private async Task Handle(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (!NetInfo.IsAllowedIp(remote))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("forbidden");
            return;
        }
        string path = ctx.Request.Path.Value ?? "/";
        bool onPairListener = PairPort is int pp && ctx.Connection.LocalPort == pp && pp != Port;

        if (onPairListener)
        {
            // The pairing listener serves only the QR page, and only to this laptop.
            if (remote is null || !IPAddress.IsLoopback(remote)) { ctx.Response.StatusCode = 403; return; }
            byte[]? body = path switch
            {
                "/pair" or "/" => Assets.Embedded("pair.html"),
                "/qrcode.js" => Assets.Embedded("qrcode.js"),
                _ => null,
            };
            await WriteStatic(ctx, body, path == "/qrcode.js" ? "qrcode.js" : "pair.html");
            return;
        }

        if (path == "/ws")
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            await HandleSocket(ctx);
            return;
        }

        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.StatusCode = 405;
            return;
        }
        string file = path == "/" ? "index.html" : path.TrimStart('/');
        await WriteStatic(ctx, _web.Get(file), file);
    }

    private static async Task WriteStatic(HttpContext ctx, byte[]? body, string name)
    {
        if (body is null) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.ContentType = Assets.ContentType(name);
        ctx.Response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.Body.WriteAsync(body);
    }

    // ---------- WebSocket ----------
    private const int MaxMessage = 64 * 1024;

    private static async Task SendJson(WebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task Reject(WebSocket ws, string reason, Task? pendingReceive = null)
    {
        try
        {
            await SendJson(ws, JsonSerializer.Serialize(new { t = "err", reason }));
            if (pendingReceive is null)
            {
                using var cts = new CancellationTokenSource(2000);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
            }
            else
            {
                // A receive is still outstanding, so CloseAsync would have to wait for it: send our close frame
                // and give the phone a moment to answer instead.
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                await Task.WhenAny(pendingReceive, Task.Delay(1000));
                _ = pendingReceive.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observe
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Read one whole message into buf. Returns (type, length), or null if the socket closed.</summary>
    private static async Task<(WebSocketMessageType type, int len)?> ReceiveMessage(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        int len = 0;
        while (true)
        {
            if (len >= buf.Length) throw new InvalidDataException("message too large");
            var r = await ws.ReceiveAsync(buf.AsMemory(len), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            len += r.Count;
            if (r.EndOfMessage) return (r.MessageType, len);
        }
    }

    private async Task HandleSocket(HttpContext ctx)
    {
        string ip = ctx.Connection.RemoteIpAddress is { } a ? (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString() : "";
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var buf = new byte[MaxMessage];

        if (!_limiter.Allow(ip)) { await Reject(ws, "rate_limited"); return; }

        // ---- authenticate ----
        // The timeout must not cancel the receive: cancelling a WebSocket receive aborts the whole socket,
        // and then the "auth_timeout" reply could not be delivered. Race it against a timer instead.
        Task<(WebSocketMessageType type, int len)?> recv = ReceiveMessage(ws, buf, CancellationToken.None);
        (WebSocketMessageType type, int len)? first;
        try
        {
            var done = await Task.WhenAny(recv, Task.Delay(TimeSpan.FromSeconds(_opt.AuthTimeoutSeconds), ctx.RequestAborted));
            if (done != recv)
            {
                await Reject(ws, "auth_timeout", pendingReceive: recv);
                return;
            }
            first = await recv;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or InvalidDataException) { return; }
        if (first is null || first.Value.type != WebSocketMessageType.Text) { await Reject(ws, "bad_request"); return; }

        string? token = null;
        int version = -1;
        try
        {
            using var doc = JsonDocument.Parse(buf.AsMemory(0, first.Value.len));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "auth")
                throw new JsonException();
            if (root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int vi)) version = vi;
            if (root.TryGetProperty("token", out var tk) && tk.ValueKind == JsonValueKind.String) token = tk.GetString();
        }
        catch (JsonException) { await Reject(ws, "bad_request"); return; }
        if (version != Protocol.Version) { await Reject(ws, "bad_version"); return; }
        if (!_tokens.Verify(token)) { _limiter.RecordFailure(ip); await Reject(ws, "bad_token"); return; }

        // Only one active client: a new authenticated connection replaces the old one. Claim the slot
        // first and close the old socket in the background, so a vanished phone can't delay the new one.
        WebSocket? old;
        lock (_gate)
        {
            old = _active;
            _active = ws;
            _activeIp = ip;
            Volatile.Write(ref _lastRx, Stopwatch.GetTimestamp());
        }
        if (old is not null && old.State == WebSocketState.Open)
        {
            _ctrl.ReleaseAll();
            CloseInBackground(old);
        }

        try
        {
            var (w, h) = _injector.ScreenSize();
            await SendJson(ws, $"{{\"t\":\"ok\",\"v\":{Protocol.Version},\"screen\":{{\"w\":{w},\"h\":{h}}}}}");
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { Detach(ws, ip); return; }
        RaiseChanged(true, ip);

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = await ReceiveMessage(ws, buf, ctx.RequestAborted);
                if (msg is null) break;
                lock (_gate) { if (!ReferenceEquals(_active, ws)) break; } // replaced by a newer phone, or dropped by the watchdog
                Volatile.Write(ref _lastRx, Stopwatch.GetTimestamp());      // liveness for the idle watchdog
                if (msg.Value.type != WebSocketMessageType.Binary) continue;
                var parsed = Protocol.Parse(buf.AsSpan(0, msg.Value.len));
                if (parsed is not { } m) continue;
                if (m.Kind == MsgKind.Ping) await ws.SendAsync(Protocol.EncodePong(m.Token), WebSocketMessageType.Binary, true, CancellationToken.None); // answered right here
                else _ctrl.Handle(m);
            }
            // The phone asked to close: answer, or it sees an abrupt disconnect instead of a clean one.
            if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or InvalidDataException or ObjectDisposedException or InvalidOperationException) { }
        finally { Detach(ws, ip); }
    }

    private void Detach(WebSocket ws, string ip)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = ReferenceEquals(_active, ws);
            if (wasActive) _active = null;
        }
        if (wasActive)
        {
            _ctrl.ReleaseAll();
            RaiseChanged(false, ip);
        }
    }

    private void RaiseChanged(bool connected, string ip)
    {
        try { ClientChanged?.Invoke(connected, ip); }
        catch (Exception e) { Trace.TraceError("ClientChanged handler failed: " + e); }
    }

    private static void CloseInBackground(WebSocket ws) => _ = Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(2000); // a dead peer never answers the close handshake
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", cts.Token);
        }
        catch { ws.Abort(); }
    });

    /// <summary>Drop a client that went silent (Wi-Fi lost, phone asleep) and release its input. The phone pings every
    /// second, so silence for the idle timeout means the link is dead even if TCP hasn't noticed. Without this a held
    /// mouse button or modifier key could stay pressed for minutes.</summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                WebSocket? ws;
                string ip;
                lock (_gate) { ws = _active; ip = _activeIp; }
                if (ws is null) continue;
                double idle = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastRx)).TotalSeconds;
                if (idle <= _opt.IdleTimeoutSeconds) continue;
                bool dropped;
                lock (_gate)
                {
                    dropped = ReferenceEquals(_active, ws);
                    if (dropped) _active = null;
                }
                if (!dropped) continue;
                Trace.TraceWarning($"client silent for {idle:F1} s: releasing input and dropping it");
                _ctrl.ReleaseAll();
                CloseInBackground(ws);
                RaiseChanged(false, ip);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_watchdog is not null) await _watchdog.ConfigureAwait(false);
        if (_app is not null)
        {
            await _app.StopAsync(TimeSpan.FromSeconds(2));
            await _app.DisposeAsync();
            _app = null;
        }
        _ctrl.ReleaseAll();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ctrl.Dispose();
    }
}
