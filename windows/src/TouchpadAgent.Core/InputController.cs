// Ordered, coalescing queue between the socket handler and the injector (agent/server.py InputController).
//
// Latency design: the WebSocket handler never injects input itself. It appends to an ordered op list,
// merging consecutive moves into one, and wakes a single injector thread that applies the whole list in
// one go. Consecutive moves (or scrolls, or zooms) are summed; any other op (button, key, text) is a
// barrier, so ordering relative to movement is preserved.
using System.Diagnostics;

namespace TouchpadAgent.Core;

public sealed class InputController : IDisposable
{
    public const int MaxStep = 4000;   // clamp for a single injected movement, in pixels
    public const int MaxWheel = 6000;  // clamp for a single wheel/zoom injection (120 = one notch)

    private enum Op { Move, Scroll, Zoom, Button, Key, Text }
    private struct Item { public Op Op; public int A, B; public string? Text; }

    private readonly IInputInjector _injector;
    private readonly object _opsLock = new();
    private readonly object _injectLock = new(); // held while injecting and while releasing, so they never interleave
    private List<Item> _ops = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _packets, _flushes;

    public long Packets => Interlocked.Read(ref _packets);
    public long Flushes => Interlocked.Read(ref _flushes);

    public InputController(IInputInjector injector, bool startThread = true)
    {
        _injector = injector;
        _thread = new Thread(Run) { IsBackground = true, Name = "touchpad-injector", Priority = ThreadPriority.Highest };
        if (startThread) _thread.Start();
    }

    private void AddSummed(Op op, int a, int b)
    {
        lock (_opsLock)
        {
            if (_ops.Count > 0 && _ops[^1].Op == op)
            {
                var last = _ops[^1];
                last.A += a;
                last.B += b;
                _ops[^1] = last;
            }
            else _ops.Add(new Item { Op = op, A = a, B = b });
        }
        _wake.Set();
    }

    private void AddBarrier(Item item)
    {
        lock (_opsLock) _ops.Add(item);
        _wake.Set();
    }

    public void Move(int dx, int dy) { Interlocked.Increment(ref _packets); AddSummed(Op.Move, dx, dy); }
    public void Scroll(int dx, int dy) => AddSummed(Op.Scroll, dx, dy);
    public void Zoom(int delta) => AddSummed(Op.Zoom, delta, 0);
    public void Button(int button, int state) => AddBarrier(new Item { Op = Op.Button, A = button, B = state });
    public void Key(int vk, int state) => AddBarrier(new Item { Op = Op.Key, A = vk, B = state });
    public void Text(string text) => AddBarrier(new Item { Op = Op.Text, Text = text });

    /// <summary>Route a message from <see cref="Protocol.Parse"/> (pings are answered by the caller).</summary>
    public void Handle(in Message m)
    {
        switch (m.Kind)
        {
            case MsgKind.Move: Move(m.A, m.B); break;
            case MsgKind.Scroll: Scroll(m.A, m.B); break;
            case MsgKind.Button: Button(m.A, m.B); break;
            case MsgKind.Key: Key(m.A, m.B); break;
            case MsgKind.Zoom: Zoom(m.A); break;
            case MsgKind.Text: Text(m.Text!); break;
        }
    }

    private static int Clamp(int v, int limit) => Math.Max(-limit, Math.Min(limit, v));

    private void Apply(in Item it)
    {
        switch (it.Op)
        {
            case Op.Move:
                int mx = Clamp(it.A, MaxStep), my = Clamp(it.B, MaxStep);
                if (mx != 0 || my != 0) _injector.Move(mx, my);
                break;
            case Op.Scroll:
                int sx = Clamp(it.A, MaxWheel), sy = Clamp(it.B, MaxWheel);
                if (sx != 0 || sy != 0) _injector.Scroll(sx, sy);
                break;
            case Op.Zoom:
                int z = Clamp(it.A, MaxWheel);
                if (z != 0) _injector.Zoom(z);
                break;
            case Op.Button: _injector.Button(it.A, it.B == 1); break;
            case Op.Key: _injector.Key(it.A, it.B == 1); break;
            case Op.Text: _injector.Text(it.Text!); break;
        }
    }

    /// <summary>Apply everything queued so far. Runs on the injector thread; tests with startThread=false call it directly.</summary>
    public void Flush()
    {
        lock (_injectLock)
        {
            List<Item> ops;
            lock (_opsLock) { ops = _ops; _ops = new(); } // taken inside _injectLock: ReleaseAll can't be overtaken by stale ops
            if (ops.Count == 0) return;
            Interlocked.Increment(ref _flushes);
            foreach (var op in ops)
            {
                try { Apply(op); }
                catch (Exception e) { Trace.TraceError("input injection failed: " + e); } // keep the injector alive
            }
        }
    }

    private void Run()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            Flush();
        }
    }

    /// <summary>Drop anything still queued (stale once the client is gone) and release held buttons and keys.</summary>
    public void ReleaseAll()
    {
        lock (_injectLock)
        {
            lock (_opsLock) _ops.Clear();
            _injector.ReleaseAll();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(500);
        ReleaseAll();
    }
}
