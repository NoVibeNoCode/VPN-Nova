using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nova.Core;

/// Route targets: `Target.Direct` or a server id.
public static class Target
{
    public const string Direct = "direct";
}

public sealed class Server
{
    [JsonRequired, JsonPropertyName("id")] public string Id { get; set; } = MakeId();
    [JsonRequired, JsonPropertyName("name")] public string Name { get; set; } = "";
    /// Short label shown on chips, e.g. "DE".
    [JsonRequired, JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonRequired, JsonPropertyName("colorHex")] public string ColorHex { get; set; } = Palette[0];
    /// sing-box outbound object without `tag`.
    [JsonRequired, JsonPropertyName("outbound")] public JsonObject Outbound { get; set; } = new();

    public Server() { }

    public Server(string name, string code, string colorHex, JsonObject outbound, string? id = null)
    {
        Id = id ?? MakeId();
        Name = name;
        Code = code;
        ColorHex = colorHex;
        Outbound = outbound;
    }

    public static string MakeId() => Guid.NewGuid().ToString("N")[..8];

    [JsonIgnore] public string ProtocolName => Outbound.Str("type") ?? "?";
    [JsonIgnore] public string Host => Outbound.Str("server") ?? "";
    [JsonIgnore] public int? Port => Outbound["server_port"].IntValue();

    /// TLS certificate checking is turned off (`allowInsecure` / `insecure` in the link) and there's no
    /// REALITY to authenticate the server instead: traffic can be intercepted.
    [JsonIgnore]
    public bool SkipsCertificateCheck =>
        Outbound["tls"]?["insecure"].IsTrue() == true && Outbound["tls"]?["reality"]?["enabled"].IsTrue() != true;

    public Server Clone() => new(Name, Code, ColorHex, (JsonObject)Outbound.DeepClone(), Id);

    public static readonly string[] Palette = ["#8B7CFF", "#FF7A59", "#3BE8B0", "#4DA3FF", "#FFC14D", "#FF5C9A", "#A3E635"];
}

[JsonConverter(typeof(JsonStringEnumConverter<DomainRuleKind>))]
public enum DomainRuleKind
{
    /// Domain and all its subdomains (`youtube.com`), or a zone (`.ru`).
    [JsonStringEnumMemberName("domain")] Domain,
    [JsonStringEnumMemberName("keyword")] Keyword,
    [JsonStringEnumMemberName("ip")] Ip,
    /// One or more remote rule sets, comma-separated tags.
    [JsonStringEnumMemberName("ruleset")] Ruleset,
}

public static class DomainRuleKindInfo
{
    public static readonly DomainRuleKind[] All = [DomainRuleKind.Domain, DomainRuleKind.Keyword, DomainRuleKind.Ip, DomainRuleKind.Ruleset];

    public static string Title(this DomainRuleKind k) => k switch
    {
        DomainRuleKind.Domain => "Домен",
        DomainRuleKind.Keyword => "Ключевое слово",
        DomainRuleKind.Ip => "IP / подсеть",
        _ => "Набор",
    };
}

public sealed class DomainRule
{
    [JsonRequired, JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    [JsonRequired, JsonPropertyName("kind")] public DomainRuleKind Kind { get; set; }
    [JsonRequired, JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonRequired, JsonPropertyName("target")] public string Target { get; set; } = Core.Target.Direct;
    [JsonRequired, JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    public DomainRule() { }

    public DomainRule(DomainRuleKind kind, string value, string target)
    {
        Kind = kind;
        Value = value;
        Target = target;
    }
}

public sealed class AppRule
{
    [JsonRequired, JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    [JsonRequired, JsonPropertyName("name")] public string Name { get; set; } = "";
    /// Program path: an .exe (`C:\Program Files\Telegram Desktop\Telegram.exe`) or a folder.
    [JsonRequired, JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonRequired, JsonPropertyName("target")] public string Target { get; set; } = Core.Target.Direct;
    [JsonRequired, JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    public AppRule() { }

    public AppRule(string name, string path, string target)
    {
        Name = name;
        Path = path;
        Target = target;
    }
}

/// Every field is optional so older profiles keep loading.
public sealed class Settings
{
    [JsonPropertyName("defaultTarget")] public string DefaultTarget { get; set; } = Target.Direct;
    [JsonPropertyName("killSwitch")] public bool KillSwitch { get; set; } = true;
    [JsonPropertyName("launchAtLogin")] public bool LaunchAtLogin { get; set; }
    [JsonPropertyName("connectOnLaunch")] public bool ConnectOnLaunch { get; set; }
    /// Windows: the speed goes into the tray icon tooltip (the tray has no room for text).
    [JsonPropertyName("showSpeedInMenuBar")] public bool ShowSpeedInMenuBar { get; set; }
    /// Log every connection (domains, apps). Off by default: the log then holds only warnings and errors.
    [JsonPropertyName("verboseLog")] public bool VerboseLog { get; set; }
    [JsonPropertyName("autoUpdateRuleSets")] public bool AutoUpdateRuleSets { get; set; } = true;
    [JsonPropertyName("clashPort")] public int ClashPort { get; set; } = 19190;
}

public sealed class Profile
{
    [JsonRequired, JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonRequired, JsonPropertyName("servers")] public List<Server> Servers { get; set; } = [];
    [JsonRequired, JsonPropertyName("domainRules")] public List<DomainRule> DomainRules { get; set; } = [];
    [JsonRequired, JsonPropertyName("appRules")] public List<AppRule> AppRules { get; set; } = [];
    [JsonRequired, JsonPropertyName("settings")] public Settings Settings { get; set; } = new();
    [JsonRequired, JsonPropertyName("clashSecret")] public string ClashSecret { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();

    public Server? Server(string id) => Servers.FirstOrDefault(s => s.Id == id);

    /// Starter rules: local-only presets that don't depend on servers.
    public static Profile Starter() => new()
    {
        DomainRules = [new DomainRule(DomainRuleKind.Ruleset, "geosite-cn,geoip-cn", Target.Direct)],
    };

    // Readable profile.json: Cyrillic names stay as they are instead of \uXXXX escapes.
    static readonly NovaJson Json = new(new JsonSerializerOptions(NovaJson.Default.Options) { Encoder = JsonExt.PrettyOptions.Encoder });

    public string ToJson() => JsonSerializer.Serialize(this, Json.Profile);

    public static Profile FromJson(string json) =>
        JsonSerializer.Deserialize(json, Json.Profile) ?? throw new JsonException("пустой профиль");

    /// Deep copy (edits happen on a copy, then replace the live profile).
    public Profile Clone() => FromJson(ToJson());

    public bool SameAs(Profile other) => ToJson() == other.ToJson();
}

/// Built-in rule sets from SagerNet/sing-geosite and sing-geoip.
public sealed record RuleSetPreset(string Title, string[] Tags)
{
    public string Id => string.Join(",", Tags);

    public static readonly RuleSetPreset[] All =
    [
        new("YouTube", ["geosite-youtube"]),
        new("Google", ["geosite-google"]),
        new("Telegram", ["geosite-telegram"]),
        new("OpenAI / ChatGPT", ["geosite-openai"]),
        new("Anthropic / Claude", ["geosite-anthropic"]),
        new("Instagram", ["geosite-instagram"]),
        new("Facebook", ["geosite-facebook"]),
        new("Twitter / X", ["geosite-twitter"]),
        new("Discord", ["geosite-discord"]),
        new("Netflix", ["geosite-netflix"]),
        new("Spotify", ["geosite-spotify"]),
        new("GitHub", ["geosite-github"]),
        new("Китай (сайты и IP)", ["geosite-cn", "geoip-cn"]),
        new("Россия (IP)", ["geoip-ru"]),
    ];

    public static string TitleFor(string value) => All.FirstOrDefault(p => p.Id == value)?.Title ?? value;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Profile))]
internal partial class NovaJson : JsonSerializerContext;
