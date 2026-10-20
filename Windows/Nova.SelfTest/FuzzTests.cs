using System.Text.Json.Nodes;
using Nova.Core;

namespace Nova.SelfTest;

/// Random profiles (seeded, reproducible) must always produce a config the guard and sing-box accept.
static class FuzzTests
{
    public static void Run(int count)
    {
        T.Section($"Фаззинг: {count} случайных профилей → ConfigGuard и sing-box check");
        var rng = new SeededRng(20261003);
        JsonObject[] protocols =
        [
            T.Obj("""{"type":"shadowsocks","server":"1.2.3.4","server_port":8388,"method":"aes-128-gcm","password":"p"}"""),
            T.Obj("""{"type":"vless","server":"h.example","server_port":443,"uuid":"11111111-2222-3333-4444-555555555555","flow":"xtls-rprx-vision","tls":{"enabled":true,"server_name":"a.com","utls":{"enabled":true,"fingerprint":"chrome"},"reality":{"enabled":true,"public_key":"jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0","short_id":""}}}"""),
            T.Obj("""{"type":"trojan","server":"t.example","server_port":443,"password":"p","tls":{"enabled":true}}"""),
            T.Obj("""{"type":"vmess","server":"v.example","server_port":10086,"uuid":"11111111-2222-3333-4444-555555555555","security":"auto"}"""),
            T.Obj("""{"type":"hysteria2","server":"h2.example","server_port":443,"password":"p","tls":{"enabled":true}}"""),
            T.Obj("""{"type":"tuic","server":"tu.example","server_port":443,"uuid":"11111111-2222-3333-4444-555555555555","password":"p","tls":{"enabled":true,"alpn":["h3"]}}"""),
        ];
        var values = new Dictionary<DomainRuleKind, string[]>
        {
            [DomainRuleKind.Domain] = ["youtube.com", ".ru", "*.cn", "https://A.B.c/x", "x.y:8080", "bad host", "", "例子.cn", "xn--p1ai", "a.b."],
            [DomainRuleKind.Keyword] = ["google", "OpenAI", "", "a b", "яндекс", "\"quote"],
            [DomainRuleKind.Ip] = ["1.1.1.1", "10.0.0.0/8", "2001:db8::/32", "999.1.1.1", "x", "fe80::1%en0", "1.2.3.4/40"],
            [DomainRuleKind.Ruleset] = ["geosite-youtube", "geosite-cn,geoip-cn", "geoip-ru", "junk", "geosite-x y", ",,", "GEOSITE-CN"],
        };
        string[] paths =
        [
            @"C:\Users\a\AppData\Roaming\Telegram Desktop\Telegram.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"D:\C++ [x](y).exe", @"C:\Program Files\Яндекс\browser.exe", @"C:\Program Files\Mozilla Firefox\", "", @"C:\Games",
            @"C:\a\b\$^{}.exe", "relative.exe", "C:/forward/slash.exe",
        ];

        var failed = 0;
        for (var i = 0; i < count; i++)
        {
            var p = new Profile();
            var nServers = rng.Next(0, 5);
            for (var j = 0; j < nServers; j++)
                p.Servers.Add(new Server($"S{j}", $"S{j}", "#fff", (JsonObject)rng.Pick(protocols).DeepClone(), $"s{j}"));
            var targets = p.Servers.Select(s => s.Id).Append(Target.Direct).Append("ghost").ToArray();
            for (var k = rng.Next(0, 25); k > 0; k--)
            {
                var kind = rng.Pick(DomainRuleKindInfo.All);
                p.DomainRules.Add(new DomainRule(kind, rng.Pick(values[kind]), rng.Pick(targets)) { Enabled = rng.Next(0, 5) > 0 });
            }
            for (var k = rng.Next(0, 8); k > 0; k--)
                p.AppRules.Add(new AppRule("a", rng.Pick(paths), rng.Pick(targets)) { Enabled = rng.Next(0, 5) > 0 });
            p.Settings.DefaultTarget = rng.Pick(targets);
            p.Settings.AutoUpdateRuleSets = rng.Next(0, 1) == 1;
            var data = new ConfigBuilder(p).Data();
            var guardErrors = ConfigGuard.Validate(data, "/nonexistent");
            if (guardErrors.Count > 0)
            {
                failed++;
                if (failed <= 3) Console.WriteLine($"    профиль #{i} отклонён ConfigGuard: {string.Join("; ", guardErrors.Take(3))}");
                continue;
            }
            var (good, output) = T.SingBoxCheck(data, "fuzz");
            if (!good)
            {
                failed++;
                if (failed <= 3)
                {
                    var dump = Path.Combine(T.Work, $"fuzz-fail-{i}.json");
                    File.WriteAllText(dump, p.ToJson());
                    Console.WriteLine($"    профиль #{i} не прошёл: {output[..Math.Min(300, output.Length)]} → {dump}");
                }
            }
        }
        T.Check(failed == 0, $"все {count} профилей проходят ConfigGuard и sing-box check", $"не прошли: {failed}");
    }
}

/// SplitMix64 — deterministic, so a failing fuzz case can be reproduced.
sealed class SeededRng(ulong seed)
{
    ulong _state = seed;

    ulong NextU64()
    {
        _state += 0x9E3779B97F4A7C15;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    /// Inclusive range, like Swift's `Int.random(in: lo...hi)`.
    public int Next(int lo, int hi) => lo + (int)(NextU64() % (ulong)(hi - lo + 1));

    public TItem Pick<TItem>(TItem[] items) => items[Next(0, items.Length - 1)];
}
