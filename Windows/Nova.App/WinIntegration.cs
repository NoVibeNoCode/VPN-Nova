using System.Diagnostics;
using Avalonia.Media.Imaging;
using Microsoft.Win32;

namespace Nova.App;

/// Windows-specific bits: autostart, the installed-programs list, program icons, Explorer.
public static class WinIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "VPN Nova";

    /// HKCU Run entry; `--background` starts the app in the tray without opening a window.
    public static void SetLaunchAtLogin(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Nova.exe");
        if (on) key.SetValue(RunValue, $"\"{exe}\" --background");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static void RevealInExplorer(string path)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public sealed record InstalledApp(string Name, string Path, bool Running);

    /// Programs from the Start menu (shortcut targets) plus running programs with a window,
    /// running ones first. Takes ~100–300 ms: call off the UI thread.
    public static List<InstalledApp> ScanApps()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                var path = p.MainModule?.FileName;
                if (path == null || IsSystemPath(path)) continue;
                running.TryAdd(path, p.MainWindowTitle.Length > 0 ? DescriptionOf(path) : Nova.Core.Naming.DisplayName(path));
            }
            catch (Exception) { /* access denied for elevated/system processes */ }
            finally { p.Dispose(); }
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        dynamic? shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); }
        catch (Exception) { }
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in SafeFiles(root, "*.lnk"))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (Junk(name)) continue;
                string? target = null;
                try { target = shell?.CreateShortcut(lnk).TargetPath as string; }
                catch (Exception) { }
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsSystemPath(target)) continue;
                if (!File.Exists(target)) continue;
                found.TryAdd(target, name);
            }
        }

        var list = found.Select(kv => new InstalledApp(kv.Value, kv.Key, running.ContainsKey(kv.Key))).ToList();
        foreach (var (path, name) in running)
            if (!found.ContainsKey(path)) list.Add(new InstalledApp(name, path, true));
        return list.OrderByDescending(a => a.Running).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        static bool Junk(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("uninstall") || n.Contains("удалить") || n.Contains("readme") || n.Contains("help") || n.Contains("справка");
        }
    }

    static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static bool IsSystemPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
               && !path.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// "Google Chrome" rather than "chrome": the exe's version-info description when there is one.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);

    public static string DescriptionOf(string path)
    {
        // Read once per program: the routes map asks for every connection on every poll.
        if (DescriptionCache.TryGetValue(path, out var hit)) return hit;
        return DescriptionCache.GetOrAdd(path, ReadDescription);
    }

    static string ReadDescription(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var d = info.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d) && d.Length <= 40) return d;
            if (!string.IsNullOrEmpty(info.ProductName?.Trim())) return info.ProductName!.Trim();
        }
        catch (Exception) { }
        return Nova.Core.Naming.DisplayName(path);
    }

    static readonly Dictionary<string, Bitmap?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// Program icons are slow to fetch; each is extracted once.
    public static Bitmap? IconFor(string path)
    {
        if (IconCache.TryGetValue(path, out var hit)) return hit;
        Bitmap? bmp = null;
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    using var gdi = icon.ToBitmap();
                    using var ms = new MemoryStream();
                    gdi.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    bmp = new Bitmap(ms);
                }
            }
            catch (Exception) { }
        }
        IconCache[path] = bmp;
        return bmp;
    }

    /// Windows taskbar theme (for tray icon contrast).
    public static bool TaskbarIsLight()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }
}
