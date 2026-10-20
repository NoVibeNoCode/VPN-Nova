using System.Globalization;

namespace Nova.Core;

/// Small pure helpers used by the UI, kept here so the self-test covers them.
public static class Naming
{
    /// "🇩🇪 Frankfurt" → "DE", "Germany-1" → "GE", "" → "S".
    public static string SuggestedCode(string name)
    {
        var flags = new List<int>();
        for (var i = 0; i < name.Length; i++)
        {
            var cp = char.ConvertToUtf32(name, i);
            if (char.IsHighSurrogate(name[i])) i++;
            if (cp is >= 0x1F1E6 and <= 0x1F1FF) flags.Add(cp);
        }
        if (flags.Count >= 2) return string.Concat(flags.Take(2).Select(f => (char)(f - 0x1F1E6 + 'A')));
        var letters = new string(name.Where(char.IsLetter).Take(2).ToArray()).ToUpper(CultureInfo.CurrentCulture);
        return letters.Length == 0 ? "S" : letters;
    }

    /// The program a connection belongs to, for grouping on the map: the process path itself
    /// (Chromium/Electron helpers already share the main exe's name). Empty → null.
    public static string? ProgramPath(string? processPath) => string.IsNullOrWhiteSpace(processPath) ? null : processPath;

    /// `C:\Program Files\Telegram Desktop\Telegram.exe` → `Telegram`; a folder → its name.
    public static string DisplayName(string path)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\');
        var name = p[(p.LastIndexOf('\\') + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// Programs whose traffic goes out through a shared WebView2 runtime (msedgewebview2.exe),
    /// outside their own exe, so an app rule can't catch it.
    public static bool UsesSharedWebView(string path) =>
        DisplayName(path).ToLowerInvariant() is "olk" or "ms-teams" or "msteams" or "widgets" or "searchhost";
}

public static class ConnectionInfo
{
    /// The server a live connection actually went through (by its outbound chain), or direct.
    public static string Target(IReadOnlyCollection<string> chains, IEnumerable<string> serverIds)
    {
        foreach (var id in serverIds)
            if (chains.Contains(ConfigBuilder.SelectorTag(id)) || chains.Contains(ConfigBuilder.ServerTag(id))) return id;
        return Core.Target.Direct;
    }

    /// True when the core clearly can't attribute connections to processes (app rules would never match).
    public static bool ProcessLookupBroken(IReadOnlyCollection<ClashApi.Connection> connections)
    {
        var tcp = connections.Where(c => c.Metadata.Network == "tcp").ToList();
        return tcp.Count >= 6 && tcp.All(c => string.IsNullOrEmpty(c.Metadata.ProcessPath));
    }
}
