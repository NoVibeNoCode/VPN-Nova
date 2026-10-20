using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Nova.Core;

namespace Nova.SelfTest;

/// Tiny test harness: named sections, pass/fail counters, shell helpers.
static class T
{
    public static int Failures, Passes, Skips;
    public static string SingBox = "";
    public static string Work = "";

    public static void Section(string title) => Console.WriteLine($"\n{title}");

    public static void Check(bool cond, string name, string detail = "")
    {
        if (cond)
        {
            Passes++;
            Console.WriteLine($"  ✓ {name}");
        }
        else
        {
            Failures++;
            Console.WriteLine($"  ✗ {name}{(detail.Length == 0 ? "" : "\n      " + detail.Replace("\n", "\n      "))}");
        }
    }

    public static void Skip(string name, string why)
    {
        Skips++;
        Console.WriteLine($"  – {name}: {why}");
    }

    /// Runs `sing-box check` on a config.
    public static (bool Ok, string Output) SingBoxCheck(byte[] data, string name)
    {
        var path = Path.Combine(Work, $"check-{name}.json");
        File.WriteAllBytes(path, data);
        var (code, output) = Shell.Run(SingBox, ["check", "-c", path]);
        return (code == 0, output);
    }

    /// One HTTPS request through the local mixed proxy (fresh connection every time).
    public static bool Fetch(string url, int proxyPort, int timeout = 8)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
        try
        {
            using var resp = http.GetAsync(url).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<bool> WaitFor(Func<Task<bool>> cond, int attempts = 50)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (await cond()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    public static Server MakeServer(string id, int port = 443) =>
        new(id.ToUpperInvariant(), id.ToUpperInvariant(), "#8B7CFF", new JsonObject
        {
            ["type"] = "shadowsocks", ["server"] = $"203.0.113.{port % 250}", ["server_port"] = port,
            ["method"] = "aes-256-gcm", ["password"] = "x",
        }, id);

    public static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    public static List<JsonObject> RouteRules(JsonObject config) => (config["route"]?["rules"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    public static List<JsonObject> DnsRules(JsonObject config) => (config["dns"]?["rules"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    /// The route rule whose matcher key holds `value`.
    public static JsonObject? RouteRule(JsonObject config, string key, string value) =>
        RouteRules(config).FirstOrDefault(r => r[key].StringList().Contains(value));

    public static bool Eq(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
    public static JsonArray Arr(params string[] items) => JsonExt.Strings(items);
}

/// A sing-box process running as a local "VPN server" for end-to-end tests.
sealed class TestServer : IDisposable
{
    public readonly Process Process;
    readonly string _log;
    readonly StreamWriter _writer;

    public TestServer(JsonObject config, string name)
    {
        var dir = Path.Combine(T.Work, $"server-{name}");
        Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, "config.json");
        File.WriteAllText(cfg, config.ToJsonString());
        _log = Path.Combine(dir, "log.txt");
        _writer = new StreamWriter(new FileStream(_log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var psi = new ProcessStartInfo(T.SingBox) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "-c", cfg, "-D", dir }) psi.ArgumentList.Add(a);
        Process = Process.Start(psi)!;
        Process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_writer) _writer.WriteLine(e.Data); };
        Process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_writer) _writer.WriteLine(e.Data); };
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    public bool IsRunning => !Process.HasExited;

    public void Stop()
    {
        if (!Process.HasExited)
        {
            Process.Kill();
            Process.WaitForExit();
        }
    }

    public string LogText
    {
        get
        {
            try
            {
                using var fs = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return new StreamReader(fs).ReadToEnd();
            }
            catch (IOException) { return ""; }
        }
    }

    public void Dispose()
    {
        Stop();
        Process.Dispose();
        _writer.Dispose();
    }
}
