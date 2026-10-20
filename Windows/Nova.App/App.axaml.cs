using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Nova.App;

public partial class App : Application
{
    public static VpnController Vpn { get; private set; } = null!;
    TrayIcon? _tray;
    PopoverWindow? _popover;
    MainWindow? _main;
    string _trayKey = "";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var demo = Program.Has("--demo") || Program.Has("--snapshot");
        Vpn = demo ? new VpnController(demo: true) : new VpnController();

        if (Program.Arg("--snapshot") is { } dir)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await Snapshot.Run(dir);
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            });
            base.OnFrameworkInitializationCompleted();
            return;
        }

        CreateTray();
        Vpn.Changed += UpdateTray;
        Vpn.Stats.Changed += UpdateTray;
        SingleInstance.ShowRequested += () => ShowMain();

        if (Program.Has("--demo"))
        {
            ShowMain(Program.Arg("--demo") is { } s && !s.StartsWith("--") ? s : null);
            ShowPopover();
        }
        // Started at login: tray only. Started by hand: the window, so the app is easy to find
        // (Windows tucks new tray icons into the overflow area).
        else if (!Program.Has("--background"))
        {
            ShowMain();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMain(string? section = null)
    {
        _popover?.Hide();
        if (_main == null)
        {
            _main = new MainWindow(Vpn);
            _main.Closed += (_, _) => _main = null;
        }
        if (section != null) _main.Go(section);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void TogglePopover()
    {
        _popover ??= new PopoverWindow(Vpn, () => ShowMain("routes"));
        if (_popover.IsVisible) _popover.Hide();
        else if (!_popover.JustHidden) ShowPopover();
    }

    void ShowPopover()
    {
        _popover ??= new PopoverWindow(Vpn, () => ShowMain("routes"));
        _popover.ShowNearTray();
    }

    void CreateTray()
    {
        var open = new NativeMenuItem("Открыть VPN Nova");
        open.Click += (_, _) => ShowMain();
        var toggle = new NativeMenuItem("Включить");
        toggle.Click += (_, _) => Vpn.Toggle();
        var quit = new NativeMenuItem("Выключить VPN и выйти");
        quit.Click += (_, _) => Vpn.Quit();
        _tray = new TrayIcon
        {
            ToolTipText = "VPN Nova",
            Menu = new NativeMenu { Items = { open, toggle, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => TogglePopover();
        TrayIcon.SetIcons(this, [_tray]);
        UpdateTray();
    }

    /// Tray icon by state (colored, unlike the macOS template icon): off — muted outline, on — mint check,
    /// server down — amber, core error — red. The tooltip carries the speed when that setting is on.
    void UpdateTray()
    {
        if (_tray == null) return;
        var (icon, color, text) = Vpn.Status switch
        {
            CoreStatus.On when Vpn.HasDownServer => (Icons.ShieldAlert, Look.Amber, "сервер недоступен"),
            CoreStatus.On => (Icons.ShieldCheck, Look.Mint, "защищено"),
            CoreStatus.Connecting => (Icons.ShieldHalf, Look.Violet, "подключаюсь…"),
            CoreStatus.Error => (Icons.ShieldX, Look.Danger, "ошибка"),
            CoreStatus.NotInstalled or CoreStatus.NeedsUpdate => (Icons.Shield, Look.Amber, "нужна установка службы"),
            _ => (Icons.Shield, WinIntegration.TaskbarIsLight() ? Color.Parse("#5A5F7A") : Look.Soft, "выключено"),
        };
        var key = icon + color;
        if (key != _trayKey)
        {
            _trayKey = key;
            _tray.Icon = new WindowIcon(RenderIcon(icon, color));
        }
        var tip = $"VPN Nova — {text}";
        if (Vpn.Profile.Settings.ShowSpeedInMenuBar && Vpn.Status == CoreStatus.On)
        {
            var (v, u) = Look.FormatSpeed(Vpn.Stats.Down);
            tip += $"\n↓ {v} {u}";
        }
        _tray.ToolTipText = tip;
        if (_tray.Menu?.Items[1] is NativeMenuItem toggleItem) toggleItem.Header = Vpn.IsActive ? "Выключить VPN" : "Включить VPN";
    }

    static Bitmap RenderIcon(string data, Color color)
    {
        const int px = 64;
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Icons.Geometry(data),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = data == Icons.ShieldCheck ? new SolidColorBrush(color, 0.25) : null,
        };
        var box = new Viewbox { Width = px, Height = px, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
        box.Measure(new Size(px, px));
        box.Arrange(new Rect(0, 0, px, px));
        var bmp = new RenderTargetBitmap(new PixelSize(px, px));
        bmp.Render(box);
        return bmp;
    }
}
