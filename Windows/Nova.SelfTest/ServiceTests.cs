using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Nova.Core;

namespace Nova.SelfTest;

/// The service in console mode, driven through the real pipe client (`ServiceCore`), like the app does.
static class ServiceTests
{
    public static async Task Run(string serviceExe)
    {
        T.Section("Служба: команды через pipe (тестовый режим)");
        var data = Path.Combine(T.Work, "service-data");
        var ownerSets = Path.Combine(T.Work, "service-owner-sets");
        Directory.CreateDirectory(ownerSets);
        new RuleSetStore(ownerSets).Seed(Path.Combine(Path.GetDirectoryName(T.SingBox)!, "rule-sets"));
        var pipeName = $"nova-test-{Environment.ProcessId}";

        var psi = new ProcessStartInfo(serviceExe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "console", "--data", data, "--singbox", T.SingBox, "--rulesets", ownerSets, "--pipe", pipeName }) psi.ArgumentList.Add(a);
        using var service = Process.Start(psi)!;
        try
        {
            var core = new ServiceCore(pipeName);
            T.Check(await T.WaitFor(() => Task.FromResult(!core.IsRunning() && !service.HasExited && Ping(pipeName)), 80), "служба слушает pipe",
                    service.HasExited ? service.StandardError.ReadToEnd() : "");

            var p = Profile.Starter();
            p.Servers = [T.MakeServer("de")];
            p.DomainRules.Add(new DomainRule(DomainRuleKind.Ruleset, "geosite-youtube", "de"));
            p.Settings.DefaultTarget = "de";
            var good = new ConfigBuilder(p) { RuleSetDirectory = ownerSets }.Data();
            T.Check(Try(() => core.Apply(good)) == null, "конфиг Nova принят", Try(() => core.Apply(good)) ?? "");
            var installed = File.Exists(Path.Combine(data, "config.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(data, "config.json"))) : null;
            var paths = (installed?["route"]?["rule_set"] as JsonArray)?.Select(x => x?["path"].StringValue() ?? "").ToList() ?? [];
            T.Check(paths.Count > 0 && paths.All(x => x.StartsWith(Path.Combine(data, "rule-sets"))) && paths.All(File.Exists),
                    "наборы правил скопированы в папку службы, пути в конфиге переписаны", string.Join(", ", paths));

            var attack = (JsonObject)JsonNode.Parse(good)!;
            attack["experimental"]!["clash_api"]!["external_ui"] = "/";
            var err = Try(() => core.Apply(Encoding.UTF8.GetBytes(attack.ToJsonString())));
            T.Check(err?.Contains("отклонён") == true, "атака external_ui отклонена службой", err ?? "принято!");

            var outside = (JsonObject)JsonNode.Parse(good)!;
            outside["route"]!["rule_set"]![0]!["path"] = Path.Combine(T.Work, "elsewhere.srs");
            File.Copy(Path.Combine(ownerSets, "geosite-youtube.srs"), Path.Combine(T.Work, "elsewhere.srs"), true);
            err = Try(() => core.Apply(Encoding.UTF8.GetBytes(outside.ToJsonString())));
            T.Check(err?.Contains("вне папки") == true, "набор правил не из папки владельца отклонён", err ?? "принято!");

            var badMethod = p.Clone();
            badMethod.Servers[0].Outbound["method"] = "no-such-cipher";
            err = Try(() => core.Apply(new ConfigBuilder(badMethod) { RuleSetDirectory = ownerSets }.Data()));
            T.Check(err == "sing-box отклонил конфиг", "конфиг, который не принял sing-box, не подменяет действующий", err ?? "принято!");
            T.Check(File.ReadAllText(Path.Combine(data, "config.json")).Contains("aes-256-gcm"), "действующий config.json остался прежним");

            T.Check(!core.IsRunning(), "статус: остановлено");
            err = Try(core.Start);
            if (err == null)
            {
                // Running as administrator/root: TUN really came up.
                T.Check(core.IsRunning(), "ядро запущено (права администратора)");
                T.Check(Try(core.Stop) == null && !core.IsRunning(), "и остановлено");
            }
            else
            {
                T.Check(err.StartsWith("sing-box сразу завершился"), "без прав администратора TUN не поднимается — служба сообщает причину", err);
                T.Check(!core.IsRunning(), "после неудачного старта ядро не висит в перезапусках");
            }
            T.Check(File.Exists(Path.Combine(data, "logs", "sing-box.log")), "лог ядра пишется в папку службы");

            T.Check(await Raw(pipeName, "format-disk\n") is { } r1 && r1.StartsWith("err\nнеизвестная команда"), "неизвестная команда отклонена");
            T.Check(await Raw(pipeName, "apply 99999999\n") is { } r2 && r2.StartsWith("err\n"), "запрос с огромной длиной отклонён");
            T.Check(await Raw(pipeName, "status\n") is { } r3 && r3.StartsWith("ok\n"), "после мусорных запросов служба отвечает");
        }
        finally
        {
            if (!service.HasExited) service.Kill(true);
            service.WaitForExit();
        }
    }

    static string? Try(Action a)
    {
        try { a(); return null; }
        catch (CoreException e) { return e.Message; }
    }

    static bool Ping(string pipe)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
            c.Connect(200);
            return true;
        }
        catch (Exception) { return false; }
    }

    static async Task<string?> Raw(string pipe, string request)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(2000);
            await c.WriteAsync(Encoding.UTF8.GetBytes(request));
            await c.FlushAsync();
            using var ms = new MemoryStream();
            using var cts = new CancellationTokenSource(5000);
            await c.CopyToAsync(ms, cts.Token);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception e) { return "exception: " + e.Message; }
    }
}
