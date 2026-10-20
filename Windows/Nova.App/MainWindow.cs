using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Nova.Core;

namespace Nova.App;

/// A section of the main window. Built on first visit, then kept alive; `Refresh` re-renders only
/// when what the page shows has changed (see `Signature`).
public abstract class Page : Border
{
    protected readonly VpnController Vpn;
    string? _sig;
    public bool Active { get; private set; }

    protected Page(VpnController vpn)
    {
        Vpn = vpn;
        Padding = new Thickness(36, 44, 36, 28);
    }

    protected abstract string Signature();
    protected abstract void Render();

    public void Refresh(bool force = false)
    {
        var sig = Signature();
        if (!force && sig == _sig) return;
        _sig = sig;
        Render();
        EffectLayer.Gate(this, Active);
    }

    public virtual void SetActive(bool active)
    {
        Active = active;
        IsVisible = active;
        if (active) Refresh();
        EffectLayer.Gate(this, active);
    }

    protected static string Sig(params object?[] parts) => string.Join("\u001f", parts.Select(p => p?.ToString() ?? ""));
    protected string ProfileSig => Vpn.Profile.ToJson();
}

public sealed class MainWindow : Window
{
    public static readonly (string Id, string Title, string Icon)[] Sections =
    [
        ("routes", "Маршруты", Icons.Routes),
        ("rules", "Правила", Icons.List),
        ("servers", "Серверы", Icons.Server),
        ("settings", "Настройки", Icons.Gear),
        ("logs", "Логи", Icons.Doc),
    ];

    readonly VpnController _vpn;
    readonly Dictionary<string, Page> _pages = [];
    readonly Panel _pageHost = new() { ClipToBounds = true };
    readonly StackPanel _nav = new() { Spacing = 4 };
    readonly Border _sidebarStatus = new();
    readonly Panel _overlay = new() { IsVisible = false };
    string _section = "routes";

    public MainWindow(VpnController vpn)
    {
        _vpn = vpn;
        Title = "VPN Nova";
        Width = 1120;
        Height = 700;
        MinWidth = 980;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Look.Brush(Look.Bg);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = 36;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Nova/Assets/Nova.png"))); } catch (Exception) { }

        var sidebar = new Border
        {
            Width = 220,
            Padding = new Thickness(14, 40, 14, 14),
            Background = Look.Brush(Look.White, 0.015),
            Child = Ui.Rows("Auto,Auto,*,Auto",
                new TextBlock
                {
                    Inlines = [new Avalonia.Controls.Documents.Run("Nova"), new Avalonia.Controls.Documents.Run(".") { Foreground = Look.Brush(Look.Violet) }],
                    FontFamily = Look.DisplayFont, FontSize = 18, FontWeight = FontWeight.ExtraBold, Foreground = Look.Brush(Look.Text),
                    Margin = new Thickness(12, 0, 12, 18),
                },
                _nav, null, _sidebarStatus),
        };
        var divider = new Border { Width = 1, Background = Look.LineBrush };
        var layout = Ui.Cols("Auto,Auto,*", sidebar, divider, _pageHost);

        // Title-bar strip: drag to move, double-click to maximize (the system draws the caption buttons).
        var drag = new Border { Height = 36, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };
        drag.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };

        Content = new Panel { Children = { layout, drag, _overlay } };
        BuildNav();
        Go(_section);
        vpn.Changed += OnChanged;
        vpn.ConnectionsChanged += () =>
        {
            if (!Shown) _stale = true;
            else if (_pages.TryGetValue("routes", out var r) && r.Active) r.Refresh();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _overlay.IsVisible) CloseSheet(); };
        Opened += (_, _) => FitScreen();
    }

    /// Small laptop screens with scaling (1366×768 at 125% leaves ~575 pt): never open taller than the screen.
    void FitScreen()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } scr) return;
        var h = scr.WorkingArea.Height / scr.Scaling;
        var w = scr.WorkingArea.Width / scr.Scaling;
        MinHeight = Math.Min(600, h);
        MinWidth = Math.Min(980, w);
        if (Height > h) Height = h;
        if (Width > w) Width = w;
    }

    bool Shown => IsVisible && WindowState != WindowState.Minimized;
    bool _stale;

    /// A window sitting in the tray doesn't rebuild (new effects would start animating unseen);
    /// it catches up once when shown again.
    void OnChanged()
    {
        if (!Shown)
        {
            _stale = true;
            return;
        }
        BuildNav();
        foreach (var p in _pages.Values) if (p.Active) p.Refresh();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing the window keeps the app (and the VPN) running in the tray.
        if (e.CloseReason is WindowCloseReason.WindowClosing && !_quitting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    bool _quitting;
    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
        {
            var visible = Shown;
            if (visible && _stale)
            {
                _stale = false;
                BuildNav();
                foreach (var p in _pages.Values) if (p.Active) p.Refresh();
            }
            foreach (var (id, p) in _pages) EffectLayer.Gate(p, visible && id == _section);
            EffectLayer.Gate(_sidebarStatus, visible);
        }
    }

    public void Go(string section)
    {
        if (Sections.All(s => s.Id != section)) section = "routes";
        _section = section;
        if (!_pages.ContainsKey(section))
        {
            Page page = section switch
            {
                "rules" => new RulesPage(_vpn),
                "servers" => new ServersPage(_vpn),
                "settings" => new SettingsPage(_vpn),
                "logs" => new LogsPage(_vpn),
                _ => new RoutesPage(_vpn),
            };
            _pages[section] = page;
            _pageHost.Children.Add(page);
        }
        foreach (var (id, p) in _pages) p.SetActive(id == section);
        BuildNav();
    }

    void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var (id, title, icon) in Sections)
        {
            var on = id == _section;
            var fg = Look.Brush(on ? Look.Text : Look.Muted);
            var item = new Pressable
            {
                Height = 40,
                Padding = new Thickness(12, 0),
                CornerRadius = new CornerRadius(10),
                Background = on ? Look.Brush(Look.Violet, 0.14) : Brushes.Transparent,
                Child = Ui.H(10, Icons.Make(icon, 17, fg), Ui.Text(title, 14, FontWeight.SemiBold, fg)),
            };
            item.Click += () => Go(id);
            _nav.Children.Add(item);
        }

        var vpn = _vpn;
        var isOn = vpn.Status == CoreStatus.On;
        var c = isOn ? Look.Mint : Look.White;
        var power = new Pressable { Child = Icons.Make(Icons.Power, 15, Look.Brush(isOn ? Look.Mint : Look.Muted), 2.4), Padding = new Thickness(4) };
        power.Click += vpn.Toggle;
        AutomationProperties.SetName(power, isOn ? "Выключить" : "Включить");
        _sidebarStatus.Padding = new Thickness(12);
        _sidebarStatus.CornerRadius = new CornerRadius(14);
        _sidebarStatus.Background = Look.Brush(c, 0.07);
        _sidebarStatus.BorderBrush = Look.Brush(c, 0.18);
        _sidebarStatus.BorderThickness = new Thickness(1);
        _sidebarStatus.Child = Ui.Cols("Auto,*,Auto",
            new PulseDot(isOn ? (vpn.HasDownServer ? Look.Amber : Look.Mint) : Look.Muted).Center(),
            Ui.V(1, Ui.Text(isOn ? "Активен" : vpn.Status == CoreStatus.Connecting ? "Подключаюсь" : "Выключен", 13, FontWeight.Bold),
                 Ui.Text($"остальное → {Look.LabelFor(vpn.Profile.Settings.DefaultTarget, vpn.Profile)}", 12, fg: Look.Brush(Look.Muted))).Margin(10, 0, 0, 0),
            power);
    }

    // MARK: - Sheets (modal panels over the window, like macOS sheets)

    public void ShowSheet(Control sheet, double width)
    {
        var panel = new Border
        {
            Width = width,
            MaxHeight = Math.Max(300, Bounds.Height - 60),
            Padding = new Thickness(28),
            CornerRadius = new CornerRadius(18),
            Background = Look.Brush(Look.Panel),
            BorderBrush = Look.Brush(Look.White, 0.1),
            BorderThickness = new Thickness(1),
            BoxShadow = Ui.Glow(Colors.Black, 0.6, 40, 16),
            Child = sheet,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Sheets stack: a confirmation can open on top of an editor and close back to it.
        _overlay.Children.Add(new Border { Background = Look.Brush(Colors.Black, _overlay.Children.Count == 0 ? 0.55 : 0.35) });
        _overlay.Children.Add(panel);
        _overlay.IsVisible = true;
        foreach (var p in _pages.Values) p.IsHitTestVisible = false;
    }

    /// Closes the topmost sheet.
    public void CloseSheet()
    {
        var n = _overlay.Children.Count;
        if (n >= 2) _overlay.Children.RemoveRange(n - 2, 2);
        if (_overlay.Children.Count > 0) return;
        _overlay.IsVisible = false;
        foreach (var p in _pages.Values) p.IsHitTestVisible = true;
    }

    public static MainWindow? Of(Control c) => TopLevel.GetTopLevel(c) as MainWindow;
}

public static class Dialogs
{
    /// A yes/no question as a sheet; `destructive` paints the confirm button red.
    public static void Confirm(Control owner, string title, string message, string confirm, Action onConfirm, bool destructive = true)
    {
        var win = MainWindow.Of(owner);
        if (win == null) return;
        var ok = new NovaButton(confirm, () => { win.CloseSheet(); onConfirm(); }, prominent: !destructive, tint: destructive ? Look.Danger : null);
        win.ShowSheet(Ui.V(16,
            Ui.Display(title, 18),
            Ui.Text(message, 13, fg: Look.Brush(Look.Soft)).Wrap(),
            Ui.H(10, new NovaButton("Отмена", win.CloseSheet), ok).Right()), 460);
    }

    public static Control Field(string title, Control content) =>
        Ui.V(6, Ui.Text(title, 12, FontWeight.Bold, Look.Brush(Look.Muted)), content);
}
