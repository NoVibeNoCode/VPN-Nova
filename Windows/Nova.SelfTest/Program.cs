using System.Text;
using System.Text.Json.Nodes;
using Nova.Core;
using Nova.SelfTest;

// VPN Nova for Windows self-test. Runs without admin rights against the real sing-box binary
// (sing-box.exe on Windows; the macOS build works too, so the C# core is checked on a Mac as well).
//
// Usage: Nova.SelfTest <path to sing-box> [--only links,config,rulesets,fuzz,pipe,service,live,e2e] [--fuzz N] [--service <Nova.Service>]

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 1)
{
    Console.WriteLine("usage: Nova.SelfTest <sing-box> [--only a,b] [--fuzz N]");
    return 2;
}
T.SingBox = Path.GetFullPath(args[0]);
T.Work = Path.Combine(Path.GetTempPath(), $"nova-selftest-{Environment.ProcessId}");
Directory.CreateDirectory(T.Work);

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

// `--emit-tun <out.json> <ss-port> <clash-port> <secret> [host]`: TUN config for the admin-only smoke test
// (Windows/Tests/tun-test.ps1).
if (Array.IndexOf(args, "--emit-tun") is var e and >= 0)
{
    var p = new Profile { ClashSecret = args[e + 4] };
    p.Settings.ClashPort = int.Parse(args[e + 3]);
    p.Settings.DefaultTarget = Target.Direct;
    var host = args.Length > e + 5 ? args[e + 5] : "127.0.0.1";
    p.Servers =
    [
        new Server("Local SS", "SS", "#8B7CFF",
                   LinkParser.Parse("ss://" + Convert.ToBase64String("aes-256-gcm:nova-tun-test"u8.ToArray()) + $"@{host}:{args[e + 2]}").Outbound, "ss"),
        new Server("Dead", "XX", "#FF5C6C", T.Obj("""{"type":"shadowsocks","server":"127.0.0.1","server_port":9,"method":"aes-256-gcm","password":"x"}"""), "dead"),
    ];
    p.DomainRules = [new DomainRule(DomainRuleKind.Domain, "example.net", "dead")];
    p.AppRules = [new AppRule("curl", @"C:\Windows\System32\curl.exe", "ss")];
    File.WriteAllBytes(args[e + 1], new ConfigBuilder(p) { LogLevel = "debug" }.Data());
    return 0;
}

var only = Arg("--only")?.Split(',').ToHashSet();
var fuzzCount = int.TryParse(Arg("--fuzz"), out var n) ? n : 150;
bool Enabled(string name) => only?.Contains(name) ?? true;

if (Enabled("links")) LinkTests.Run();

if (Enabled("rulesets") || Enabled("config"))
{
    T.Section("Наборы правил: скачивание");
    var store = new RuleSetStore(Path.Combine(T.Work, "rule-sets"));
    var failed = await store.Ensure(["geosite-telegram", "geoip-ru", "geosite-no-such-set-nova"], refresh: true);
    T.Check(File.Exists(store.PathFor("geosite-telegram")), "geosite-telegram скачан");
    T.Check(File.Exists(store.PathFor("geoip-ru")), "geoip-ru скачан");
    T.Check(failed.Keys.Order().SequenceEqual(["geosite-no-such-set-nova"]), "несуществующий набор — в списке ошибок, остальным не мешает", string.Join(",", failed.Keys));
    var before = File.GetLastWriteTimeUtc(store.PathFor("geoip-ru"));
    await store.Ensure(["geoip-ru"], refresh: true);
    T.Check(File.GetLastWriteTimeUtc(store.PathFor("geoip-ru")) == before, "свежий файл повторно не качается");

    T.Section("Наборы правил: вшитые в приложение");
    var bundled = Path.Combine(Path.GetDirectoryName(T.SingBox)!, "rule-sets");
    var fresh = new RuleSetStore(Path.Combine(T.Work, "seeded"));
    var presetTags = RuleSetPreset.All.SelectMany(x => x.Tags).ToList();
    T.Check(fresh.Missing(presetTags).Count == presetTags.Count, "пустое хранилище: всё отсутствует");
    var t0 = DateTime.UtcNow;
    fresh.Seed(bundled);
    T.Check(fresh.Missing(presetTags).Count == 0, $"все {presetTags.Count} пресетов есть в приложении и копируются без сети", "нет: " + string.Join(",", fresh.Missing(presetTags)));
    T.Check(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(0.5), "засев занимает < 0,5 с (подключение не ждёт GitHub)");
    var withSets = Profile.Starter();
    withSets.DomainRules.Add(new DomainRule(DomainRuleKind.Ruleset, "geosite-youtube,geoip-ru", Target.Direct));
    var (ok, output) = T.SingBoxCheck(new ConfigBuilder(withSets) { RuleSetDirectory = fresh.Directory }.Data(), "seeded");
    T.Check(ok, "конфиг на вшитых наборах проходит sing-box check", output);
    File.SetLastWriteTimeUtc(fresh.PathFor("geosite-youtube"), DateTime.UtcNow);
    File.SetLastWriteTimeUtc(fresh.PathFor("geoip-ru"), DateTime.UtcNow - TimeSpan.FromDays(8));
    var stale = fresh.Stale(["geosite-youtube", "geoip-ru", "geosite-missing"]);
    T.Check(stale.SequenceEqual(["geoip-ru"]), "устаревание: свежий — нет, 8 дней — да, отсутствующий — не «устаревший»", string.Join(",", stale));
}

if (Enabled("config"))
{
    ConfigTests.Run();
    ConfigTests.HelperTests();
    ConfigTests.GuardTests();
    ConfigTests.SecurityFlagTests();
}
if (Enabled("pipe") || Enabled("config")) await ConfigTests.PipeTests();
if (Enabled("fuzz")) FuzzTests.Run(fuzzCount);
if (Enabled("service"))
{
    if (Arg("--service") is { } svc) await ServiceTests.Run(Path.GetFullPath(svc));
    else T.Skip("служба", "не передан --service <путь к Nova.Service>");
}
if (Enabled("live")) await LiveTests.Routing();
if (Enabled("e2e")) await LiveTests.Protocols();

Console.WriteLine($"\nИтого: ✓ {T.Passes}  ✗ {T.Failures}  – {T.Skips}");
if (T.Failures == 0)
{
    try { Directory.Delete(T.Work, true); } catch (IOException) { }
}
else
{
    Console.WriteLine($"рабочая папка: {T.Work}");
}
return T.Failures == 0 ? 0 : 1;
