using System.Text.Json;

namespace Nova.Core;

/// Service-side allowlist for configs handed to the core. sing-box runs as SYSTEM, so a config is
/// effectively code: `external_ui`, `log.output`, `*_path` options and extra inbounds would let
/// any process running as the user read or write files as SYSTEM. The guard accepts exactly the
/// shape `ConfigBuilder` produces and rejects everything else.
public static class ConfigGuard
{
    public static List<string> Validate(byte[] data, string ruleSetDirectory)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(data);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ["конфиг не JSON-объект"];
        }
        if (root.ValueKind != JsonValueKind.Object) return ["конфиг не JSON-объект"];

        var errors = new List<string>();
        void Fail(string m) => errors.Add(m);

        // Anything that names a file is off limits everywhere, except the rule-set paths checked below.
        void ScanPaths(JsonElement v, string at)
        {
            if (v.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in v.EnumerateObject())
                {
                    var here = at.Length == 0 ? p.Name : $"{at}.{p.Name}";
                    if (p.Name.EndsWith("_path", StringComparison.Ordinal) || p.Name is "external_ui" or "external_ui_download_url" or "output")
                        Fail($"запрещённый ключ {here}");
                    ScanPaths(p.Value, here);
                }
            }
            else if (v.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var sub in v.EnumerateArray()) ScanPaths(sub, $"{at}[{i++}]");
            }
        }
        ScanPaths(root, "");

        string[] allowedTop = ["log", "dns", "inbounds", "outbounds", "route", "experimental"];
        foreach (var p in root.EnumerateObject()) if (!allowedTop.Contains(p.Name)) Fail($"неизвестный раздел {p.Name}");

        if (Obj(root, "log") is { } log)
            foreach (var k in Keys(log)) if (k is not ("level" or "timestamp" or "disabled")) Fail($"log.{k}");

        // Exactly one TUN inbound, nothing listening on the network.
        var inbounds = Objects(root, "inbounds");
        if (inbounds.Count != 1 || Str(inbounds[0], "type") != "tun") Fail("нужен ровно один tun-инбаунд");
        string[] tunKeys = ["type", "tag", "interface_name", "address", "auto_route", "strict_route", "stack", "mtu"];
        foreach (var ib in inbounds) foreach (var k in Keys(ib)) if (!tunKeys.Contains(k)) Fail($"inbound.{k}");

        if (Obj(root, "experimental") is { } exp)
        {
            foreach (var k in Keys(exp)) if (k is not ("clash_api" or "cache_file")) Fail($"experimental.{k}");
            if (Obj(exp, "clash_api") is { } api)
            {
                foreach (var k in Keys(api)) if (k is not ("external_controller" or "secret")) Fail($"clash_api.{k}");
                var ctl = Str(api, "external_controller") ?? "";
                if (!ctl.StartsWith("127.0.0.1:", StringComparison.Ordinal) || Ints.Parse(ctl["127.0.0.1:".Length..]) == null)
                    Fail("clash_api только на 127.0.0.1");
                if ((Str(api, "secret") ?? "").Length < 16) Fail("clash_api без секрета");
            }
            if (Obj(exp, "cache_file") is { } cache)
                foreach (var k in Keys(cache)) if (k is not ("enabled" or "store_fakeip")) Fail($"cache_file.{k}");
        }

        if (Obj(root, "dns") is { } dns)
        {
            foreach (var k in Keys(dns)) if (k is not ("servers" or "rules" or "final" or "strategy")) Fail($"dns.{k}");
            foreach (var s in Objects(dns, "servers"))
            {
                var type = Str(s, "type") ?? "";
                if (type is not ("local" or "fakeip")) Fail($"dns-сервер {(type.Length > 0 ? type : "?")}");
                foreach (var k in Keys(s)) if (k is not ("type" or "tag" or "inet4_range" or "inet6_range")) Fail($"dns.server.{k}");
            }
        }

        string[] okTypes = ["vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "wireguard",
                            "anytls", "shadowtls", "http", "socks", "direct", "selector"];
        foreach (var ob in Objects(root, "outbounds"))
        {
            var type = Str(ob, "type") ?? "";
            if (!okTypes.Contains(type)) Fail($"outbound {(type.Length > 0 ? type : "?")}");
        }

        // Rule sets: local files only from Nova's own folder (no links out), or https downloads.
        var route = Obj(root, "route");
        foreach (var rs in route is { } r ? Objects(r, "rule_set") : [])
        {
            switch (Str(rs, "type"))
            {
                case "local":
                    var p = Str(rs, "path") ?? "";
                    if (!IsInsideRuleSetDir(p, ruleSetDirectory)) Fail($"набор правил вне папки Nova: {SafeName(p)}");
                    foreach (var k in Keys(rs)) if (k is not ("type" or "tag" or "format" or "path")) Fail($"rule_set.{k}");
                    break;
                case "remote":
                    if (!(Str(rs, "url") ?? "").StartsWith("https://", StringComparison.Ordinal)) Fail("rule_set только https");
                    foreach (var k in Keys(rs))
                        if (k is not ("type" or "tag" or "format" or "url" or "download_detour" or "update_interval")) Fail($"rule_set.{k}");
                    break;
                default:
                    Fail("rule_set неизвестного типа");
                    break;
            }
        }
        return errors;
    }

    /// A plain `.srs` file directly in the rule-set folder: no `..`, no UNC/device paths, no alternate
    /// data streams, and neither the file nor the folder chain is a link (symlink/junction) elsewhere.
    public static bool IsInsideRuleSetDir(string path, string ruleSetDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(ruleSetDirectory)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        // `C:\x\a.srs:stream` — only the drive colon is allowed.
        if (path.IndexOf(':', OperatingSystem.IsWindows() ? 2 : 0) >= 0) return false;
        string full, dir;
        try
        {
            full = Path.GetFullPath(path);
            dir = Path.GetFullPath(ruleSetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return false;
        }
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), dir, cmp)) return false;
        if (!full.EndsWith(".srs", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var file = new FileInfo(full);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget != null) return false;
            for (var d = file.Directory; d != null; d = d.Parent)
            {
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsTrustedSystemLink(d)) return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }

    /// macOS test runs: /var and /tmp are system symlinks into /private (the Windows profile path has none).
    static bool IsTrustedSystemLink(DirectoryInfo d) =>
        !OperatingSystem.IsWindows() && d.Parent?.Parent == null && d.LinkTarget is { } t && t.StartsWith("private/", StringComparison.Ordinal);

    static string SafeName(string p)
    {
        try { return Path.GetFileName(p); }
        catch (ArgumentException) { return "?"; }
    }

    static JsonElement? Obj(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    static List<JsonElement> Objects(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList()
            : [];

    static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static IEnumerable<string> Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name);

    static IEnumerable<string> Keys(JsonElement? e) => e is { } x ? Keys(x) : [];
}
