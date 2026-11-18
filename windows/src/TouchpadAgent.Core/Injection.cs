// What the agent needs from an input injector, plus a recording one for tests and --dry-run.
namespace TouchpadAgent.Core;

public interface IInputInjector
{
    (int w, int h) ScreenSize();
    void Move(int dx, int dy);
    void Scroll(int dx, int dy);
    void Zoom(int delta);
    void Button(int button, bool down);
    void Key(int vk, bool down);
    void Text(string text);
    /// <summary>Release every button and key this injector still holds down.</summary>
    void ReleaseAll();
}

/// <summary>Records instead of injecting. Safe on any OS and thread-safe (the injector runs on its own thread).</summary>
public sealed class DryRunInjector : IInputInjector
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();
    private readonly HashSet<int> _buttonsDown = new(), _keysDown = new();
    private readonly (int, int) _screen;
    public long MoveCalls, TotalDx, TotalDy, ScrollDx, ScrollDy, ZoomTotal;

    public DryRunInjector((int, int)? screen = null) => _screen = screen ?? (1920, 1080);

    public (int w, int h) ScreenSize() => _screen;

    private void Log(string e) => _events.Add(e); // callers hold _gate

    public void Move(int dx, int dy) { lock (_gate) { MoveCalls++; TotalDx += dx; TotalDy += dy; Log($"move {dx} {dy}"); } }
    public void Scroll(int dx, int dy) { lock (_gate) { ScrollDx += dx; ScrollDy += dy; Log($"scroll {dx} {dy}"); } }
    public void Zoom(int delta) { lock (_gate) { ZoomTotal += delta; Log($"zoom {delta}"); } }
    public void Button(int button, bool down) { lock (_gate) { if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button); Log($"button {button} {(down ? 1 : 0)}"); } }
    public void Key(int vk, bool down) { lock (_gate) { if (down) _keysDown.Add(vk); else _keysDown.Remove(vk); Log($"key {vk} {(down ? 1 : 0)}"); } }
    public void Text(string text) { lock (_gate) Log($"text {text}"); }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (int b in _buttonsDown.Order().ToList()) { _buttonsDown.Remove(b); Log($"button {b} 0"); }
            foreach (int k in _keysDown.Order().ToList()) { _keysDown.Remove(k); Log($"key {k} 0"); }
        }
    }

    public string[] Events() { lock (_gate) return _events.ToArray(); }
    public bool AnyButtonDown() { lock (_gate) return _buttonsDown.Count > 0; }
    public bool AnyKeyDown() { lock (_gate) return _keysDown.Count > 0; }
}
