// Pairing tokens and failed-login rate limiting (same behaviour and file format as agent/auth.py).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TouchpadAgent.Core;

public static class Config
{
    public const string AppDir = "phone-touchpad";

    /// <summary>%APPDATA%\phone-touchpad on Windows, ~/.config/phone-touchpad elsewhere (the Python agent uses the same folder).</summary>
    public static string Dir()
    {
        string? baseDir = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("APPDATA")
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(baseDir))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            baseDir = OperatingSystem.IsWindows() ? Path.Combine(home, "AppData", "Roaming") : Path.Combine(home, ".config");
        }
        return Path.Combine(baseDir, AppDir);
    }

    public static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Stores only SHA-256 hashes of pairing tokens, never the tokens themselves.
/// The file is {"tokens": [hex, ...]}, shared with the Python agent, so a phone paired with one works with the other.</summary>
public sealed class TokenStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<string> _hashes = new();

    public TokenStore(string path)
    {
        _path = path;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("tokens", out var arr) && arr.ValueKind == JsonValueKind.Array)
                _hashes = arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public bool HasTokens() { lock (_gate) return _hashes.Count > 0; }

    /// <summary>Create a 128-bit URL-safe token (22 characters, like Python's token_urlsafe(16)).</summary>
    public string Create()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            _hashes.Add(Hash(token));
            Save();
        }
        return token;
    }

    public bool Verify(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        byte[] h = Encoding.ASCII.GetBytes(Hash(token));
        bool ok = false;
        string[] snapshot;
        lock (_gate) snapshot = _hashes.ToArray();
        foreach (var stored in snapshot) // no early exit: constant-time over the list
            ok |= CryptographicOperations.FixedTimeEquals(h, Encoding.ASCII.GetBytes(stored));
        return ok;
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            _hashes = new();
            Save();
        }
    }

    private void Save() => Config.WriteAtomic(_path, JsonSerializer.Serialize(new { tokens = _hashes }, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Allows at most <c>maxFailures</c> failed logins per <c>window</c> per IP.</summary>
public sealed class RateLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Func<TimeSpan> _clock;
    private readonly Dictionary<string, Queue<TimeSpan>> _failures = new();
    private readonly object _gate = new();

    public RateLimiter(int maxFailures = 5, double windowSeconds = 60, Func<TimeSpan>? clock = null)
    {
        _max = maxFailures;
        _window = TimeSpan.FromSeconds(windowSeconds);
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
    }

    private Queue<TimeSpan> Prune(string ip)
    {
        if (!_failures.TryGetValue(ip, out var q)) _failures[ip] = q = new();
        var cutoff = _clock() - _window;
        while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
        return q;
    }

    public bool Allow(string ip) { lock (_gate) return Prune(ip).Count < _max; }
    public void RecordFailure(string ip) { lock (_gate) Prune(ip).Enqueue(_clock()); }
}
