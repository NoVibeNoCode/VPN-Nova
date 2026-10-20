using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Nova.Core;

namespace Nova.Service;

/// Owns the sing-box process: checked config in, start/stop, restart after a crash (like launchd's KeepAlive).
sealed class CoreHost(ServiceLayout layout) : IDisposable
{
    readonly object _lock = new();
    // Separate from _lock: stopping waits for sing-box to exit while holding _lock, and sing-box's last
    // log lines must still drain meanwhile.
    readonly object _logLock = new();
    Process? _process;
    bool _wanted;
    int _restarts;
    DateTime _restartWindow = DateTime.UtcNow;
    FileStream? _log;

    public string Apply(byte[] config)
    {
        lock (_lock)
        {
            // Rule sets may only come from the owner's Nova folder; they are copied into the service's own
            // folder (users can't write there), so sing-box never opens a user-controlled path.
            var errors = ConfigGuard.Validate(config, layout.OwnerRuleSetDir);
            if (errors.Count > 0) throw new CoreException("конфиг отклонён: " + string.Join("; ", errors.Take(5)));

            var root = (JsonObject)JsonNode.Parse(config)!;
            Directory.CreateDirectory(layout.RuleSetDir);
            foreach (var rs in (root["route"]?["rule_set"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (rs.Str("type") != "local") continue;
                var src = rs.Str("path")!;
                var dst = Path.Combine(layout.RuleSetDir, Path.GetFileName(src));
                SafeCopy.CopyRegularFile(src, dst, layout.OwnerRuleSetDir);
                rs["path"] = dst;
            }

            var next = Path.Combine(layout.DataDir, "config.next.json");
            File.WriteAllText(next, root.ToJsonString(JsonExt.PrettyOptions), new UTF8Encoding(false));
            Directory.CreateDirectory(layout.WorkDir);
            var (code, _) = Shell.Run(layout.SingBox, ["check", "-c", next, "-D", layout.WorkDir], 30000);
            if (code != 0)
            {
                File.Delete(next);
                // Nothing from the rejected config is echoed back.
                throw new CoreException("sing-box отклонил конфиг");
            }
            File.Move(next, layout.ConfigFile, overwrite: true);
            return "";
        }
    }

    public string Start()
    {
        lock (_lock)
        {
            if (!File.Exists(layout.ConfigFile)) throw new CoreException("нет конфига: сначала apply");
            StopLocked();
            _wanted = true;
            _restarts = 0;
            _restartWindow = DateTime.UtcNow;
            Spawn();
        }
        // A config that sing-box accepts can still fail at runtime (port taken, TUN refused): report it now.
        Thread.Sleep(700);
        lock (_lock)
        {
            if (_process is { HasExited: false }) return "";
            _wanted = false;
            throw new CoreException("sing-box сразу завершился: " + LastLogLine());
        }
    }

    public string Stop()
    {
        lock (_lock)
        {
            _wanted = false;
            StopLocked();
            return "";
        }
    }

    public string Status()
    {
        lock (_lock)
            return _process is { HasExited: false } p ? $"running pid {p.Id}" : "stopped";
    }

    void Spawn()
    {
        RotateLog();
        var psi = new ProcessStartInfo(layout.SingBox)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Shares the service's hidden console so it can be stopped gracefully with Ctrl+Break.
            CreateNoWindow = false,
            WorkingDirectory = layout.DataDir,
        };
        foreach (var a in new[] { "run", "-c", layout.ConfigFile, "-D", layout.WorkDir }) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => WriteLog(e.Data);
        p.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        p.Exited += (_, _) => OnExited(p);
        p.Start();
        Native.AddToKillJob(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
    }

    void OnExited(Process p)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(p, _process) || !_wanted) return;
            WriteLog($"[nova] sing-box завершился с кодом {p.ExitCode}");
            if (DateTime.UtcNow - _restartWindow > TimeSpan.FromMinutes(1))
            {
                _restartWindow = DateTime.UtcNow;
                _restarts = 0;
            }
            if (++_restarts > 5)
            {
                WriteLog("[nova] ядро падает раз за разом — перезапуски остановлены");
                _wanted = false;
                return;
            }
        }
        // Back off a little so a crash loop doesn't spin.
        Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 << _restarts))).ContinueWith(_ =>
        {
            lock (_lock)
            {
                if (_wanted && ReferenceEquals(p, _process)) Spawn();
            }
        });
    }

    void StopLocked()
    {
        if (_process is not { } p) return;
        _process = null;
        if (!p.HasExited)
        {
            // Graceful first: sing-box removes its routes and the Wintun adapter on exit.
            Native.SendCtrlBreak();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                p.WaitForExit(3000);
            }
        }
        p.Dispose();
    }

    FileStream OpenLog()
    {
        Directory.CreateDirectory(layout.LogDir);
        return new FileStream(layout.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    void RotateLog()
    {
        lock (_logLock)
        try
        {
            var f = new FileInfo(layout.LogFile);
            if (!f.Exists || f.Length <= 5 * 1024 * 1024) return;
            _log?.Dispose();
            _log = null;
            File.Move(layout.LogFile, layout.LogFile + ".1", overwrite: true);
        }
        catch (IOException) { }
    }

    void WriteLog(string? line)
    {
        if (line == null) return;
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_logLock)
        {
            try
            {
                _log ??= OpenLog();
                _log.Write(bytes);
                _log.Flush();
            }
            catch (IOException) { }
        }
    }

    string LastLogLine()
    {
        try
        {
            using var fs = new FileStream(layout.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - 4096), SeekOrigin.Begin);
            var tail = new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return tail.LastOrDefault(l => !l.StartsWith("[nova]"))?.Trim() ?? "подробности в логе";
        }
        catch (IOException) { return "подробности в логе"; }
    }

    public void Dispose()
    {
        Stop();
        lock (_logLock) _log?.Dispose();
    }
}
