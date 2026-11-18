// A tiny test runner: no external packages are needed.
using System.Diagnostics;

namespace TouchpadAgent.Tests;

public static class T
{
    private static readonly List<(string name, Func<Task> body)> Tests = new();

    public static void Test(string name, Func<Task> body) => Tests.Add((name, body));
    public static void Test(string name, Action body) => Tests.Add((name, () => { body(); return Task.CompletedTask; }));

    public static async Task<int> RunAll(string? filter)
    {
        int failed = 0, ran = 0;
        foreach (var (name, body) in Tests)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            ran++;
            var sw = Stopwatch.StartNew();
            try
            {
                await body().WaitAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine($"ok   {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"FAIL {name}\n     {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine($"\n{ran - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    public static void True(bool cond, string? msg = null) { if (!cond) throw new Exception(msg ?? "expected true"); }
    public static void Equal<T2>(T2 expected, T2 actual, string? msg = null)
    {
        if (!EqualityComparer<T2>.Default.Equals(expected, actual))
            throw new Exception($"{msg ?? "not equal"}: expected {expected}, got {actual}");
    }
    public static void Bytes(byte[] expected, byte[] actual, string? msg = null) =>
        Equal(Convert.ToHexString(expected), Convert.ToHexString(actual), msg);

    public static async Task WaitFor(Func<bool> cond, int ms = 3000, string? what = null)
    {
        var end = Environment.TickCount64 + ms;
        while (!cond())
        {
            if (Environment.TickCount64 > end) throw new Exception("timed out waiting for " + (what ?? "condition"));
            await Task.Delay(10);
        }
    }

    public static string RepoWebDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "web", "index.html"))) return Path.Combine(d.FullName, "web");
        throw new DirectoryNotFoundException("web/ folder not found above " + AppContext.BaseDirectory);
    }

    public static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "tp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
