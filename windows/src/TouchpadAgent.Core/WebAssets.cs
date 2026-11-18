// The phone web client and the pairing page, served from memory.
using System.Reflection;

namespace TouchpadAgent.Core;

public interface IWebAssets
{
    /// <summary>Contents of a file such as "index.html", or null if there is none.</summary>
    byte[]? Get(string path);
}

/// <summary>Files compiled into the assembly (the web/ folder of the repository).</summary>
public sealed class EmbeddedWebAssets : IWebAssets
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public EmbeddedWebAssets(string prefix = "web/")
    {
        var asm = typeof(EmbeddedWebAssets).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _files[name[prefix.Length..].Replace('\\', '/')] = ms.ToArray();
        }
    }

    public byte[]? Get(string path) => _files.TryGetValue(path, out var b) ? b : null;
    public IEnumerable<string> Names => _files.Keys;
}

/// <summary>Files read from a folder on disk (--web-dir, handy while editing the client).</summary>
public sealed class DirectoryWebAssets : IWebAssets
{
    private readonly string _root;

    public DirectoryWebAssets(string root) => _root = Path.GetFullPath(root);

    public byte[]? Get(string path)
    {
        string full = Path.GetFullPath(Path.Combine(_root, path));
        // Reject ".." escapes and absolute paths.
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }
}

public static class Assets
{
    /// <summary>A file from the assets/ folder embedded in this assembly (qrcode.js, pair.html).</summary>
    public static byte[]? Embedded(string name)
    {
        using var s = typeof(Assets).Assembly.GetManifestResourceStream("assets/" + name);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".webmanifest" => "application/manifest+json",
        ".json" => "application/json",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}
