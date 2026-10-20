using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nova.Core;

/// Turns a user `Profile` into a sing-box 1.14 config.
///
/// Route order (decision "вариант А"):
/// sniff → DNS hijack → private IPs → domain rules → app rules → default target.
public sealed class ConfigBuilder(Profile profile)
{
    public abstract record InboundKind
    {
        public sealed record Tun : InboundKind;
        /// Local HTTP/SOCKS proxy, used by the self-test (no admin rights needed).
        public sealed record Mixed(int Port) : InboundKind;
    }

    public const string DirectTag = "direct";
    /// Selector for "всё остальное", switched live from the menu.
    public const string DefaultRouteTag = "default-route";
    public const string LocalDnsTag = "dns-local";
    const string FakeDnsTag = "dns-fake";
    /// Name of the Wintun adapter; the app tells it apart from other VPNs by this name.
    public const string TunInterfaceName = "Nova";

    public static string ServerTag(string id) => $"srv-{id}";
    public static string SelectorTag(string id) => $"via-{id}";

    public Profile Profile { get; } = profile;
    public InboundKind Inbound { get; init; } = new InboundKind.Tun();
    /// null → from the profile: "info" with verbose logging, otherwise "warn" (no per-connection lines).
    public string? LogLevel { get; init; }
    /// When set, rule sets are read from `<dir>/<tag>.srs` (see `RuleSetStore`) instead of downloaded by the core.
    public string? RuleSetDirectory { get; init; }

    /// Outbound tag for a target; unknown servers fall back to direct.
    public string OutboundTag(string target) =>
        target != Target.Direct && Profile.Server(target) != null ? SelectorTag(target) : DirectTag;

    public JsonObject Build()
    {
        var ruleSetTags = new List<string>();
        void UseRuleSets(IEnumerable<string> tags)
        {
            foreach (var t in tags) if (!ruleSetTags.Contains(t)) ruleSetTags.Add(t);
        }

        // Outbounds: each server is wrapped in a selector [server, direct] so the
        // kill switch monitor can fall back to direct without a restart.
        var outbounds = new JsonArray();
        foreach (var s in Profile.Servers)
        {
            var ob = (JsonObject)s.Outbound.DeepClone();
            ob["tag"] = ServerTag(s.Id);
            outbounds.Add(ob);
            outbounds.Add(new JsonObject
            {
                ["type"] = "selector",
                ["tag"] = SelectorTag(s.Id),
                ["outbounds"] = JsonExt.Strings([ServerTag(s.Id), DirectTag]),
                ["default"] = ServerTag(s.Id),
                ["interrupt_exist_connections"] = true,
            });
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });

        // Route + DNS rules.
        var routeRules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag },
        };
        // FakeIP range 198.19.0.0/16: other clients (Clash-based apps and the like) commonly take 198.18.0.0/16.
        // DNS: A/AAAA get fake IPs, so proxied connections carry the domain and the
        // server resolves it (no ISP DNS poisoning, no dependency on one server's DoH).
        // Local names and domains routed DIRECT keep real answers from the system resolver.
        var dnsRules = new JsonArray
        {
            new JsonObject
            {
                ["domain_suffix"] = JsonExt.Strings(["local", "lan", "localhost", "home.arpa", "internal"]),
                ["server"] = LocalDnsTag,
            },
        };

        foreach (var r in Profile.DomainRules.Where(r => r.Enabled))
        {
            if (Matcher(r) is not { } match) continue;
            var rule = (JsonObject)match.DeepClone();
            rule["outbound"] = OutboundTag(r.Target);
            routeRules.Add(rule);

            if (r.Kind == DomainRuleKind.Ruleset) UseRuleSets(AvailableRuleSets(r.Value));
            if (!(r.Target == Target.Direct || Profile.Server(r.Target) == null)) continue;
            switch (r.Kind)
            {
                case DomainRuleKind.Ruleset:
                    // DNS rules may only reference domain rule sets.
                    var domainSets = AvailableRuleSets(r.Value).Where(t => t.StartsWith("geosite-", StringComparison.Ordinal)).ToList();
                    if (domainSets.Count > 0)
                        dnsRules.Add(new JsonObject { ["rule_set"] = JsonExt.Strings(domainSets), ["server"] = LocalDnsTag });
                    break;
                case DomainRuleKind.Domain:
                case DomainRuleKind.Keyword:
                    var d = (JsonObject)match.DeepClone();
                    d["server"] = LocalDnsTag;
                    dnsRules.Add(d);
                    break;
            }
        }

        foreach (var a in Profile.AppRules.Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Path)))
        {
            routeRules.Add(new JsonObject
            {
                ["process_path_regex"] = JsonExt.Strings(AppPathRegexes(a.Path)),
                ["outbound"] = OutboundTag(a.Target),
            });
        }

        var finalOutbound = OutboundTag(Profile.Settings.DefaultTarget);
        outbounds.Add(new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = DefaultRouteTag,
            ["outbounds"] = JsonExt.Strings(Profile.Servers.Select(s => SelectorTag(s.Id)).Append(DirectTag)),
            ["default"] = finalOutbound,
            ["interrupt_exist_connections"] = true,
        });

        dnsRules.Add(new JsonObject { ["query_type"] = JsonExt.Strings(["A", "AAAA"]), ["server"] = FakeDnsTag });
        var dnsServers = new JsonArray
        {
            new JsonObject { ["type"] = "local", ["tag"] = LocalDnsTag },
            new JsonObject { ["type"] = "fakeip", ["tag"] = FakeDnsTag, ["inet4_range"] = "198.19.0.0/16", ["inet6_range"] = "fc00::/18" },
        };

        var ruleSets = new JsonArray();
        foreach (var tag in ruleSetTags)
        {
            if (RuleSetDirectory != null)
            {
                ruleSets.Add(new JsonObject
                {
                    ["type"] = "local",
                    ["tag"] = tag,
                    ["format"] = "binary",
                    ["path"] = Path.Combine(RuleSetDirectory, $"{tag}.srs"),
                });
            }
            else
            {
                ruleSets.Add(new JsonObject
                {
                    ["type"] = "remote",
                    ["tag"] = tag,
                    ["format"] = "binary",
                    ["url"] = RuleSetUrl(tag),
                    ["download_detour"] = DefaultRouteTag,
                    ["update_interval"] = Profile.Settings.AutoUpdateRuleSets ? "7d" : "365d",
                });
            }
        }

        var route = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = DefaultRouteTag,
            ["auto_detect_interface"] = true,
            ["find_process"] = true,
            ["default_domain_resolver"] = LocalDnsTag,
        };
        if (ruleSets.Count > 0) route["rule_set"] = ruleSets;

        var dns = new JsonObject
        {
            ["servers"] = dnsServers,
            ["rules"] = dnsRules,
            ["final"] = LocalDnsTag,
            ["strategy"] = "prefer_ipv4",
        };

        JsonObject inbound = Inbound switch
        {
            InboundKind.Mixed m => new JsonObject
            {
                ["type"] = "mixed", ["tag"] = "mixed-in", ["listen"] = "127.0.0.1", ["listen_port"] = m.Port,
            },
            // strict_route also stops Windows from leaking DNS through other adapters
            // (Smart Multi-Homed Name Resolution).
            _ => new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = TunInterfaceName,
                ["address"] = JsonExt.Strings(["172.19.0.1/30", "fdfe:dcba:9876::1/126"]),
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "mixed",
            },
        };

        return new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = LogLevel ?? (Profile.Settings.VerboseLog ? "info" : "warn"),
                ["timestamp"] = true,
            },
            ["dns"] = dns,
            ["inbounds"] = new JsonArray(inbound),
            ["outbounds"] = outbounds,
            ["route"] = route,
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{Profile.Settings.ClashPort}",
                    ["secret"] = Profile.ClashSecret,
                },
                ["cache_file"] = new JsonObject { ["enabled"] = true, ["store_fakeip"] = true },
            },
        };
    }

    public byte[] Data() => Encoding.UTF8.GetBytes(Build().ToJsonString(JsonExt.PrettyOptions));

    // MARK: - Matchers

    /// sing-box matcher for a rule, or null if the rule is empty or invalid (then it's skipped,
    /// so one bad rule can't stop the core from starting).
    internal JsonObject? Matcher(DomainRule r)
    {
        var v = r.Value.Trim();
        if (v.Length == 0) return null;
        switch (r.Kind)
        {
            case DomainRuleKind.Domain:
                var d = NormalizeDomain(v);
                return IsValidDomain(d) ? new JsonObject { ["domain_suffix"] = JsonExt.Strings([d]) } : null;
            case DomainRuleKind.Keyword:
                return new JsonObject { ["domain_keyword"] = JsonExt.Strings([v.ToLowerInvariant()]) };
            case DomainRuleKind.Ip:
                return NormalizeCidr(v) is { } cidr ? new JsonObject { ["ip_cidr"] = JsonExt.Strings([cidr]) } : null;
            default:
                var tags = AvailableRuleSets(v);
                return tags.Count == 0 ? null : new JsonObject { ["rule_set"] = JsonExt.Strings(tags) };
        }
    }

    /// Tags of a rule that can actually be loaded (files present when reading from disk).
    internal List<string> AvailableRuleSets(string value)
    {
        var tags = new List<string>();
        foreach (var t in RuleSetTags(value)) if (IsValidRuleSetTag(t) && !tags.Contains(t)) tags.Add(t);
        if (RuleSetDirectory == null) return tags;
        return tags.Where(t => File.Exists(Path.Combine(RuleSetDirectory, $"{t}.srs"))).ToList();
    }

    /// `*.youtube.com`, `.ru`, `https://YouTube.com:443/x`, `example.com.` → `youtube.com`, `ru`, `example.com`.
    public static string NormalizeDomain(string raw)
    {
        var s = raw.ToLowerInvariant().Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var cut = s.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) s = s[..cut];
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        var colon = s.LastIndexOf(':');
        if (colon >= 0 && Ints.Parse(s[(colon + 1)..]) != null) s = s[..colon];
        while (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        s = s.TrimStart('.').TrimEnd('.');
        return s;
    }

    public static bool IsValidDomain(string d) =>
        d.Length is > 0 and <= 253 && d.All(c => char.IsLetterOrDigit(c) || c is '-' or '.') && !d.Contains("..");

    static readonly Regex Ipv4 = new(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$");

    /// `1.2.3.4` → `1.2.3.4/32`, `2001:db8::1` → `/128`; null for anything that isn't an IP or subnet.
    public static string? NormalizeCidr(string raw)
    {
        var s = raw.Trim();
        var parts = s.Split('/');
        if (parts.Length > 2 || parts[0].Length == 0) return null;
        var ip = parts[0];
        // Strict like inet_pton: dotted quad only, IPv6 without a zone (`%en0`).
        var isV4 = Ipv4.IsMatch(ip);
        var isV6 = !isV4 && ip.Contains(':') && !ip.Contains('%')
            && System.Net.IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        if (!isV4 && !isV6) return null;
        var maxBits = isV4 ? 32 : 128;
        if (parts.Length == 2)
        {
            if (Ints.Parse(parts[1]) is not { } bits || bits < 0 || bits > maxBits) return null;
            return $"{ip}/{bits}";
        }
        return $"{ip}/{maxBits}";
    }

    internal static bool IsValidRuleSetTag(string tag) =>
        (tag.StartsWith("geosite-", StringComparison.Ordinal) || tag.StartsWith("geoip-", StringComparison.Ordinal))
        && tag.All(c => char.IsLower(c) || char.IsDigit(c) || c is '-' or '_' or '!' or '@');

    public static List<string> RuleSetTags(string value) =>
        value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    public static string RuleSetUrl(string tag)
    {
        var repo = tag.StartsWith("geoip-", StringComparison.Ordinal) ? "sing-geoip" : "sing-geosite";
        return $"https://raw.githubusercontent.com/SagerNet/{repo}/rule-set/{tag}.srs";
    }

    /// Regex for an app rule (sing-box matches it against the full process path, case-insensitively):
    /// - an .exe by its file name, wherever it lives: per-version folders (`app-1.0.9\Discord.exe`),
    ///   reinstalls and moves keep the rule working, and every process of a Chromium/Electron app
    ///   (all named like the main exe) is caught;
    /// - a folder: every program under it.
    public static List<string> AppPathRegexes(string path)
    {
        var p = path.Trim().Replace('/', '\\').TrimEnd('\\');
        var name = p[(p.LastIndexOf('\\') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return [$"(?i)\\\\{EscapeRegex(name)}$"];
        return [AppPathRegex(p)];
    }

    /// Matches every process under the folder `path` (absolute).
    public static string AppPathRegex(string path) => $"(?i)^{EscapeRegex(path.Replace('/', '\\').TrimEnd('\\'))}\\\\";

    static string EscapeRegex(string s)
    {
        const string special = "\\.+*?()|[]{}^$";
        var sb = new StringBuilder(s.Length * 2);
        foreach (var c in s)
        {
            if (special.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
