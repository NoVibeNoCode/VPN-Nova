using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nova.Core;

namespace Nova.App;

public sealed class SettingsPage : Page
{
    public SettingsPage(VpnController vpn) : base(vpn) { }

    protected override string Signature() => Sig(ProfileSig, Vpn.Status, ServiceCore.IsInstalled, ServiceCore.InstalledVersion);

    protected override void Render()
    {
        var s = Vpn.Profile.Settings;
        var options = new StackPanel();
        void Option(string title, string hint, bool value, Action<bool> set, bool last = false)
        {
            options.Children.Add(new Border
            {
                Padding = new Thickness(0, 14),
                BorderBrush = last ? null : Look.LineBrush,
                BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
                Child = Ui.Cols("*,Auto", Ui.V(2, Ui.Text(title, 14, FontWeight.Bold), Ui.Text(hint, 12, fg: Look.Brush(Look.Muted)).Wrap()).Margin(0, 0, 14, 0),
                                new NovaToggle(value, set)),
            });
        }
        Option("Запускать при входе в Windows", "Приложение само запустится в трее после перезагрузки", s.LaunchAtLogin, Vpn.SetLaunchAtLogin);
        Option("Подключаться при запуске", "VPN включится сразу, как откроется приложение", s.ConnectOnLaunch, v => Vpn.Update(p => p.Settings.ConnectOnLaunch = v, restart: false));
        Option("Скорость в подсказке трея", "Наведи на значок в трее: ↓ 4,2 МБ/с", s.ShowSpeedInMenuBar, v => Vpn.Update(p => p.Settings.ShowSpeedInMenuBar = v, restart: false));
        Option("Обновлять наборы правил", "Раз в неделю: YouTube, Китай и другие", s.AutoUpdateRuleSets, v => Vpn.Update(p => p.Settings.AutoUpdateRuleSets = v));
        Option("Подробный лог", "Записывать каждое соединение: сайты и программы. Для отладки, потом лучше выключить", s.VerboseLog, v => Vpn.Update(p => p.Settings.VerboseLog = v), last: true);

        var content = Ui.V(18,
            Ui.PageHeader("Настройки", "Как ведёт себя VPN Nova"),
            new KillSwitchCard(s.KillSwitch, Vpn.SetKillSwitch),
            new Border
            {
                Padding = new Thickness(18, 0),
                CornerRadius = new CornerRadius(18),
                Background = Look.Brush(Look.Panel),
                BorderBrush = Look.Brush(Look.White, 0.07),
                BorderThickness = new Thickness(1),
                Child = options,
            },
            Ui.Card(Ui.V(6, Ui.Text("DNS", 14, FontWeight.Bold),
                         Ui.Text("Сайты, которые идут через сервер, резолвит сам сервер: программа получает служебный адрес, а на сервер уходит имя сайта. Подмена DNS у провайдера не мешает, падение одного сервера не ломает DNS для остальных. Локальные имена и сайты из правил DIRECT резолвит системный DNS. Запросы мимо VPN через другие сетевые адаптеры Windows перекрыты.", 12, fg: Look.Brush(Look.Muted)).Wrap()), 18, 18),
            CoreCard(),
            new SelectableTextBlock
            {
                Text = $"VPN Nova {VpnController.AppVersion} · служба {VpnController.ServiceVersion} · ядро {VpnController.CoreVersion}",
                FontFamily = Look.MonoFont, FontSize = 11, Foreground = Look.Brush(Look.Muted),
            });
        content.MaxWidth = 820;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden };
    }

    Control CoreCard()
    {
        var installed = Vpn.IsDemo || ServiceCore.IsInstalled;
        var running = Vpn.Status == CoreStatus.On;
        var buttons = Ui.H(10);
        if (installed)
        {
            buttons.Children.Add(new NovaButton("Переустановить", Vpn.Install, icon: Icons.Refresh));
            buttons.Children.Add(new NovaButton("Удалить", () => Dialogs.Confirm(this, "Удалить фоновую службу?",
                "Служба, ядро и логи будут удалены. Профиль с серверами и правилами останется.", "Удалить", Vpn.Uninstall), icon: Icons.Trash));
        }
        else
        {
            buttons.Children.Add(new NovaButton("Установить", Vpn.Install, prominent: true));
        }
        buttons.Children.Add(new NovaButton("Профиль", () => WinIntegration.RevealInExplorer(ProfileStore.ProfilePath), icon: Icons.Folder)
            .Tip("Показать profile.json в Проводнике"));
        return Ui.Card(Ui.Cols("Auto,*,Auto",
            new PulseDot(running ? Look.Mint : installed ? Look.Muted : Look.Amber).Center(),
            Ui.V(2, Ui.Text($"Ядро sing-box {VpnController.CoreVersion}", 14, FontWeight.Bold),
                 Ui.Text(installed ? (running ? "работает как служба Windows · TUN (Wintun)" : "служба установлена, ядро остановлено") : "служба не установлена", 12, fg: Look.Brush(Look.Muted))).Margin(14, 0, 14, 0),
            buttons), 18, 18);
    }
}

public sealed class KillSwitchCard : Border
{
    public KillSwitchCard(bool on, Action<bool> set)
    {
        Padding = new Thickness(22);
        CornerRadius = new CornerRadius(22);
        Background = on ? Ui.Gradient(Look.Mint.WithAlpha(0.16), Look.Mint.WithAlpha(0.02), diagonal: true) : Look.Brush(Look.Panel);
        BorderBrush = on ? Look.Brush(Look.Mint, 0.35) : Look.Brush(Look.White, 0.08);
        BorderThickness = new Thickness(1);
        var shield = new Border
        {
            Width = 84,
            Height = 84,
            CornerRadius = new CornerRadius(24),
            ClipToBounds = true,
            Background = Look.Brush(on ? Color.Parse("#0E3B2E") : Color.Parse("#1A1D30")),
            BoxShadow = on ? Ui.Glow(Look.Mint, 0.35, 20) : default,
            Child = new Panel
            {
                Children =
                {
                    new SweepingLight(false, Look.Mint.WithAlpha(0.45), 22, 2.2, on),
                    Icons.Make(on ? Icons.ShieldCheck : Icons.Shield, 38, Look.Brush(on ? Look.Mint : Color.Parse("#6B7092")), 1.6).Center(),
                },
            },
        };
        Child = Ui.Cols("Auto,*,Auto", shield,
            Ui.V(4, Ui.Display("Kill switch", 18),
                 Ui.Text(on ? "Если сервер упал, его трафик блокируется. Ничего не утечёт напрямую."
                            : "Если сервер упал, его трафик временно пойдёт напрямую.", 13, fg: Look.Brush(Look.Soft)).Wrap()).Margin(18, 0, 18, 0).Center(),
            new NovaToggle(on, set));
    }
}
