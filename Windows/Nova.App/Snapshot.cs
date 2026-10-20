using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Nova.Core;

namespace Nova.App;

/// Debug renders with demo data, used to check the UI without a person at the screen.
/// `Nova --snapshot <dir>` opens real windows for every screen and state, captures them
/// (composition snapshot, so render-thread effects are included) to PNG and exits.
/// `--cpu-test` additionally measures CPU with the tray menu shown and hidden.
public static class Snapshot
{
    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var vpn = new VpnController(demo: true);
        await Popover(vpn, "popover", dir);
        await Interactions(vpn, dir);
        foreach (var (id, _, _) in MainWindow.Sections)
        {
            await Window(vpn, id, 1100, 690, $"window-{id}", dir);
            await Window(vpn, id, 980, 600, $"min-{id}", dir);
        }

        async Task Variant(string name, Action<VpnController> setup, string? window = null)
        {
            var v = new VpnController(demo: true);
            setup(v);
            if (window != null) await Window(v, window, 1100, 690, name, dir);
            else await Popover(v, name, dir);
        }
        await Variant("popover-setup", v => v.DemoSet(status: CoreStatus.NotInstalled));
        await Variant("st-needs-update", v => v.DemoSet(status: CoreStatus.NeedsUpdate));
        await Variant("st-error", v => v.DemoSet(status: CoreStatus.Error, message: "Ядро не запустилось. Загляни в логи.",
            lastError: "Служба VPN Nova установлена для другого пользователя Windows — очень длинное сообщение об ошибке, которое должно переноситься, а не обрезаться"));
        await Variant("st-warning", v => v.DemoSet(health: new() { ["de"] = new(null, 3), ["ru"] = new(18) }, otherVpns: ["Corp VPN", "wg-office"]));
        await Variant("st-fallback", v => v.DemoSet(health: new() { ["de"] = new(null, 3, true), ["ru"] = new(18) }));
        await Variant("st-connecting", v => v.DemoSet(status: CoreStatus.Connecting));
        await Variant("st-off-empty", v => v.DemoSet(status: CoreStatus.Off, profile: Profile.Starter()));

        var many = new Profile();
        string[] names = ["Германия — Франкфурт-на-Майне, очень длинное имя", "Россия", "Нидерланды", "Финляндия", "США", "Япония"];
        string[] codes = ["DE", "RU", "NL", "FI", "USA", "JPN"];
        for (var i = 0; i < 6; i++)
        {
            var ob = i == 3
                ? (JsonObject)JsonNode.Parse("""{"type":"trojan","server":"203.0.113.3","server_port":443,"tls":{"enabled":true,"insecure":true}}""")!
                : new JsonObject { ["type"] = "vless", ["server"] = $"203.0.113.{i}", ["server_port"] = 443 };
            many.Servers.Add(new Server(names[i], codes[i], Server.Palette[i], ob, $"s{i}"));
        }
        many.Settings.DefaultTarget = "s5";
        for (var i = 0; i < 40; i++) many.DomainRules.Add(new DomainRule(DomainRuleKind.Domain, $"very-long-subdomain-{i}.example-domain-name.com", $"s{i % 6}"));
        many.AppRules = [new AppRule("Очень длинное название программы, которое не помещается", @"C:\Program Files\Очень длинное название программы\app.exe", "s1")];
        await Variant("st-many-popover", v => v.DemoSet(profile: many.Clone()));
        await Variant("st-many-rules", v => v.DemoSet(profile: many.Clone()), "rules");
        await Variant("st-many-servers", v => v.DemoSet(profile: many.Clone()), "servers");
        await Variant("st-many-routes", v => v.DemoSet(profile: many.Clone()), "routes");
        await Variant("st-routes-off", v => v.DemoSet(status: CoreStatus.Off, otherVpns: ["Corp VPN"], connections: []), "routes");
        await Variant("st-routes-quiet", v => v.DemoSet(connections: []), "routes");

        // Sheets.
        await Sheet(vpn, "sheet-domain", w => AddDomainSheet.Show(w, vpn), dir);
        await Sheet(vpn, "sheet-apps", w => AppPickerSheet.Show(w, vpn), dir);
        await Sheet(vpn, "sheet-server-new", w => ServerEditor.Show(w, vpn, null), dir);
        await Sheet(vpn, "sheet-server-edit", w => ServerEditor.Show(w, vpn, vpn.Profile.Servers[0]), dir);
        await Sheet(vpn, "sheet-confirm", w => Dialogs.Confirm(w, "Удалить фоновую службу?", "Служба, ядро и логи будут удалены. Профиль с серверами и правилами останется.", "Удалить", () => { }), dir);

        if (Program.Has("--cpu-test")) await CpuTest(vpn);
    }

    /// Clicks through the paths that broke on macOS: "всё остальное" sliding (it blinked there), fast
    /// section switching, rule retargeting, kill switch and on/off. Any exception fails the run.
    static async Task Interactions(VpnController vpn, string dir)
    {
        var w = new PopoverWindow(vpn, () => { });
        w.ShowNearTray();
        await Task.Delay(800);
        vpn.SetDefaultTarget("direct");
        await Task.Delay(140);
        await Capture(w, "ui-segment-sliding", dir, settle: 0);
        await Task.Delay(600);
        await Capture(w, "ui-segment-direct", dir, settle: 0);
        vpn.SetDefaultTarget("de");
        vpn.SetKillSwitch(false);
        vpn.SetKillSwitch(true);
        w.Hide();

        var main = new MainWindow(vpn) { Width = 1100, Height = 690, Position = new PixelPoint(40, 40), WindowStartupLocation = WindowStartupLocation.Manual };
        main.Show();
        for (var round = 0; round < 3; round++)
            foreach (var (id, _, _) in MainWindow.Sections)
            {
                main.Go(id);
                await Task.Delay(60);
            }
        main.Go("rules");
        vpn.Update(p => p.DomainRules[0].Target = "ru");
        vpn.Update(p => p.AppRules[0].Enabled = false);
        vpn.SetDefaultTarget("ru");
        await Capture(main, "ui-rules-retargeted", dir);
        vpn.Toggle();
        await Task.Delay(300);
        vpn.Toggle();
        await Task.Delay(1800);
        await Capture(main, "ui-reconnected", dir);
        main.CloseForQuit();
        vpn.Update(p => { p.DomainRules[0].Target = "de"; p.AppRules[0].Enabled = true; });
        vpn.SetDefaultTarget("de");
        Console.WriteLine("✓ взаимодействия без исключений");
    }

    static async Task Popover(VpnController vpn, string name, string dir)
    {
        var w = new PopoverWindow(vpn, () => { });
        w.Position = new PixelPoint(40, 40);
        w.ShowNearTray();
        await Capture(w, name, dir);
        w.Hide();
    }

    static async Task Window(VpnController vpn, string section, double width, double height, string name, string dir)
    {
        var w = new MainWindow(vpn) { Width = width, Height = height, Position = new PixelPoint(40, 40), WindowStartupLocation = WindowStartupLocation.Manual };
        w.Go(section);
        w.Show();
        await Capture(w, name, dir);
        w.CloseForQuit();
    }

    static async Task Sheet(VpnController vpn, string name, Action<MainWindow> open, string dir)
    {
        var w = new MainWindow(vpn) { Width = 1100, Height = 760, Position = new PixelPoint(40, 40), WindowStartupLocation = WindowStartupLocation.Manual };
        w.Go("rules");
        w.Show();
        await Task.Delay(300);
        open(w);
        await Capture(w, name, dir);
        w.CloseForQuit();
    }

    static async Task Capture(TopLevel w, string name, string dir, int settle = 1400)
    {
        await Task.Delay(settle);
        var visual = ElementComposition.GetElementVisual(w);
        if (visual == null)
        {
            Console.WriteLine($"✗ {name}: нет композиции");
            return;
        }
        var bmp = await visual.Compositor.CreateCompositionVisualSnapshot(visual, w.RenderScaling);
        bmp.Save(Path.Combine(dir, $"{name}.png"));
        Console.WriteLine($"✓ {name}.png {(int)w.Bounds.Width}×{(int)w.Bounds.Height}");
    }

    /// CPU with the tray menu shown vs hidden: catches animations that keep running when nobody sees them.
    static async Task CpuTest(VpnController vpn)
    {
        var w = new PopoverWindow(vpn, () => { });
        w.ShowNearTray();
        await Task.Delay(1500);
        Report("меню открыто", await Measure());
        w.Hide();
        await Task.Delay(1500);
        Report("меню закрыто", await Measure());

        // The main window hidden in the tray while state keeps changing (the demo ticks every second).
        var main = new MainWindow(vpn);
        main.Show();
        await Task.Delay(1500);
        Report("окно открыто (маршруты)", await Measure());
        main.Hide();
        await Task.Delay(1500);
        Report("окно в трее, данные меняются", await Measure());
        main.CloseForQuit();
    }

    static async Task<double> Measure(double seconds = 4)
    {
        var p = Process.GetCurrentProcess();
        var start = p.TotalProcessorTime;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        p.Refresh();
        return (p.TotalProcessorTime - start).TotalSeconds / seconds * 100;
    }

    static void Report(string phase, double pct) => Console.WriteLine($"CPU {phase}: {pct:0.0}%");
}
