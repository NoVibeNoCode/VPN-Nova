using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Nova.Core;

/// Fixed system locations used by the service, its installer and the app.
public static class SystemPaths
{
    public const string ServiceName = "NovaVPN";
    public const string ServiceDisplayName = "VPN Nova";
    public const string PipeName = "nova-vpn";

    /// C:\ProgramData\Nova: config, cache.db, logs. SYSTEM and Administrators only; the owner may read the logs.
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nova");
    /// C:\Program Files\VPN Nova\Service: the service binary and sing-box.exe, copied there by the installer
    /// (only administrators can write there, so nothing running as the user can swap them).
    public static string ServiceDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VPN Nova", "Service");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string LogFile => Path.Combine(LogDir, "sing-box.log");
    public static string VersionFile => Path.Combine(DataDir, "installed-version");
}

public sealed class CoreException(string message) : Exception(message);

/// Starts/stops the sing-box core.
public interface ICoreService
{
    void Apply(byte[] config);
    void Start();
    void Stop();
    bool IsRunning();
}

/// The wire format between the app and the service over the named pipe. One request per connection:
/// `<command>\n` or `apply <length>\n<config bytes>`; the reply is `ok\n<text>` or `err\n<text>`.
public static class PipeProtocol
{
    public const int MaxConfig = 4 * 1024 * 1024;

    public static async Task WriteRequest(Stream s, string command, byte[]? payload, CancellationToken ct)
    {
        var head = payload == null ? $"{command}\n" : $"{command} {payload.Length}\n";
        await s.WriteAsync(Encoding.UTF8.GetBytes(head), ct).ConfigureAwait(false);
        if (payload != null) await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// Reads one request; null on anything malformed (oversized, truncated, garbage).
    public static async Task<(string Command, byte[]? Payload)?> ReadRequest(Stream s, CancellationToken ct)
    {
        var line = await ReadLine(s, 64, ct).ConfigureAwait(false);
        if (line == null) return null;
        var parts = line.Split(' ');
        if (parts.Length == 1) return (parts[0], null);
        if (parts.Length != 2 || Ints.Parse(parts[1]) is not { } len || len < 0 || len > MaxConfig) return null;
        var buf = new byte[len];
        try { await s.ReadExactlyAsync(buf, ct).ConfigureAwait(false); }
        catch (EndOfStreamException) { return null; }
        return (parts[0], buf);
    }

    public static async Task WriteReply(Stream s, bool ok, string text, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.UTF8.GetBytes((ok ? "ok\n" : "err\n") + text), ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(bool Ok, string Text)> ReadReply(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        var nl = text.IndexOf('\n');
        var status = nl >= 0 ? text[..nl] : text;
        return (status == "ok", nl >= 0 ? text[(nl + 1)..] : "");
    }

    static async Task<string?> ReadLine(Stream s, int max, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count <= max)
        {
            if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
            if (one[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        return null;
    }
}

/// Production core: the Windows service, driven over its named pipe (only the owner may connect).
public sealed class ServiceCore(string pipeName = SystemPaths.PipeName) : ICoreService
{
    public static bool IsInstalled => File.Exists(SystemPaths.VersionFile);

    public static string? InstalledVersion
    {
        get
        {
            try { return File.ReadAllText(SystemPaths.VersionFile).Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    string Call(string command, byte[]? payload = null, int timeoutMs = 30000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.ConnectAsync(5000, cts.Token).GetAwaiter().GetResult();
            PipeProtocol.WriteRequest(pipe, command, payload, cts.Token).GetAwaiter().GetResult();
            var (ok, text) = PipeProtocol.ReadReply(pipe, cts.Token).GetAwaiter().GetResult();
            if (!ok) throw new CoreException(text.Length > 0 ? text : $"служба отказала: {command}");
            return text;
        }
        catch (TimeoutException) { throw new CoreException("Служба VPN Nova не отвечает. Переустанови её в настройках."); }
        catch (OperationCanceledException) { throw new CoreException("Служба VPN Nova не ответила вовремя."); }
        catch (UnauthorizedAccessException) { throw new CoreException("Служба VPN Nova установлена для другого пользователя Windows."); }
        catch (IOException e) { throw new CoreException($"Связь со службой оборвалась: {e.Message}"); }
    }

    /// The config goes to the service over the pipe: SYSTEM never opens a path chosen by the user.
    public void Apply(byte[] config) => Call("apply", config);
    public void Start() => Call("start");
    public void Stop() => Call("stop");

    public bool IsRunning()
    {
        try { return Call("status", timeoutMs: 5000).StartsWith("running", StringComparison.Ordinal); }
        catch (CoreException) { return false; }
    }

    /// One-time install (and update/uninstall): the service exe runs elevated through UAC.
    public static void Install(string appDir, string version) =>
        RunElevated(Path.Combine(appDir, "Nova.Service.exe"),
                    $"install --from \"{appDir.TrimEnd('\\')}\" --owner {CurrentUserSid()} --version {version}");

    public static void Uninstall(string appDir) =>
        RunElevated(Path.Combine(appDir, "Nova.Service.exe"), "uninstall");

    /// Where the elevated installer reports errors: our temp folder (the elevated process may run as
    /// another account with its own %TEMP%).
    static string InstallLog => Path.Combine(Path.GetTempPath(), "nova-service-install.log");

    static string CurrentUserSid() =>
        OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value : "S-1-0-0";

    static void RunElevated(string exe, string args)
    {
        try { File.Delete(InstallLog); } catch (IOException) { }
        var psi = new ProcessStartInfo(exe, $"{args} --log \"{InstallLog}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new CoreException("Отменено: Windows не получила разрешение администратора.");
        }
        if (p == null) throw new CoreException("Не удалось запустить установщик службы.");
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            var detail = File.Exists(InstallLog) ? File.ReadAllText(InstallLog).Trim() : $"код {p.ExitCode}";
            throw new CoreException(detail.Split('\n').Last().Trim());
        }
    }
}

/// Unprivileged core for tests: runs sing-box as a child process.
public sealed class LocalCore(string binary, string workDir) : ICoreService
{
    Process? _process;
    public string LogPath => Path.Combine(workDir, "sing-box.log");
    string ConfigPath => Path.Combine(workDir, "config.json");

    public void Apply(byte[] config)
    {
        Directory.CreateDirectory(workDir);
        File.WriteAllBytes(ConfigPath, config);
        var (code, output) = Shell.Run(binary, ["check", "-c", ConfigPath]);
        if (code != 0) throw new CoreException(output.Trim());
    }

    public void Start()
    {
        Stop();
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "run", "-c", ConfigPath, "-D", workDir }) psi.ArgumentList.Add(a);
        var log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        _process = Process.Start(psi) ?? throw new CoreException("sing-box не запустился");
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Stop()
    {
        if (_process is not { } p) return;
        if (!p.HasExited)
        {
            p.Kill();
            p.WaitForExit();
        }
        p.Dispose();
        _process = null;
    }

    public bool IsRunning() => _process is { HasExited: false };
}

public static class Shell
{
    /// Runs a program, returns exit code and combined output; kills it after `timeoutMs`.
    public static (int Code, string Output) Run(string exe, IEnumerable<string> args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e) { return (-1, e.Message); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
        }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
