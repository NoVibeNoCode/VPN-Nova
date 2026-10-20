using Avalonia;
using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Nova.Core;

namespace Nova.App;

/// The tray menu: star button, speed, servers, "всё остальное", kill switch (PopoverView.swift).
public sealed class PopoverWindow : Window
{
    readonly VpnController _vpn;
    readonly Action _openMain;
    readonly StackPanel _body = new() { Spacing = 12 };
    readonly OrbButton _orb;
    readonly SpeedsRow _speeds;
    readonly TextBlock _headline = Ui.Display("", 21);
    readonly TextBlock _subline = Ui.Mono("", 12, Look.Brush(Look.Muted));
    readonly DispatcherTimer _clock;
    DateTime _hiddenAt = DateTime.MinValue;

    public PopoverWindow(VpnController vpn, Action openMain)
    {
        _vpn = vpn;
        _openMain = openMain;
        Title = "VPN Nova";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;

        _orb = new OrbButton(vpn.Toggle);
        _speeds = new SpeedsRow(vpn);
        _headline.HorizontalAlignment = HorizontalAlignment.Center;
        _subline.HorizontalAlignment = HorizontalAlignment.Center;
        _subline.TextTrimming = TextTrimming.CharacterEllipsis;

        Content = new Border
        {
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            BorderBrush = Look.Brush(Look.White, 0.1),
            BorderThickness = new Thickness(1),
            Background = Look.Brush(Look.Panel, 0.98),
            Child = new Panel
            {
                Children =
                {
                    Backdrop(),
                    // Scrolls when the screen is shorter than the menu (1080p at 150% leaves ~670 pt).
                    new ScrollViewer
                    {
                        Content = new Border { Padding = new Thickness(16), Child = _body },
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    },
                },
            },
        };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateSubline();
        vpn.Changed += () => { if (IsVisible) Rebuild(); };
        Deactivated += (_, _) => HideNow();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HideNow(); };
    }

    /// True right after an auto-hide: the tray click that caused it must not reopen the menu.
    public bool JustHidden => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(350);

    void HideNow()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Alt+F4 just hides; the app lives in the tray.
        e.Cancel = true;
        HideNow();
        base.OnClosing(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (IsVisible)
        {
            _vpn.RefreshOtherVpns();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
        EffectLayer.Gate(this, IsVisible);
    }

    public void ShowNearTray()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } scr) MaxHeight = scr.WorkingArea.Height / scr.Scaling - 24;
        Rebuild();
        Opacity = 0;
        Show();
        Activate();
        Dispatcher.UIThread.Post(() =>
        {
            Place();
            Opacity = 1;
        }, DispatcherPriority.Loaded);
    }

    /// Next to the tray: above a bottom taskbar, below a top one, beside a side one.
    void Place()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var wa = screen.WorkingArea;
        var b = screen.Bounds;
        var s = screen.Scaling;
        var w = (int)(Bounds.Width * s);
        var h = (int)(Bounds.Height * s);
        var m = (int)(12 * s);
        var x = wa.Right - w - m;
        var y = wa.Bottom - h - m;
        if (wa.Y > b.Y) y = wa.Y + m;
        if (wa.X > b.X) x = wa.X + m;
        Position = new PixelPoint(Math.Max(wa.X, x), Math.Max(wa.Y, y));
        MaxHeight = wa.Height / s - 24;
    }

    static Control Backdrop()
    {
        Ellipse Blob(Color c, double opacity, double size) => new()
        {
            Width = size,
            Height = size,
            Fill = new RadialGradientBrush { GradientStops = { new GradientStop(c.WithAlpha(opacity), 0), new GradientStop(Colors.Transparent, 1) } },
        };
        var a = Blob(Look.VioletDeep, 0.35, 420);
        var b = Blob(Look.Coral, 0.16, 380);
        Canvas.SetLeft(a, -170);
        Canvas.SetTop(a, -210);
        Canvas.SetLeft(b, 160);
        Canvas.SetTop(b, 260);
        return new Canvas { Children = { a, b }, IsHitTestVisible = false };
    }

    void Rebuild()
    {
        Detach(_orb);
        Detach(_speeds);
        Detach(_headline);
        Detach(_subline);
        _body.Children.Clear();

        _body.Children.Add(Ui.Cols("*,Auto", Ui.Display("VPN Nova", 15), new StatusPill(_vpn)));

        _orb.Apply(OrbStateFor(_vpn), _vpn.Status is not (CoreStatus.NotInstalled or CoreStatus.NeedsUpdate));
        _headline.Text = Headline();
        UpdateSubline();
        _body.Children.Add(Ui.V(6, _orb.Center(), _headline, _subline).Margin(0, 4, 0, 0));

        if (_vpn.Status is CoreStatus.NotInstalled or CoreStatus.NeedsUpdate)
        {
            _body.Children.Add(SetupCard(_vpn, _vpn.Status == CoreStatus.NeedsUpdate));
        }
        else
        {
            _speeds.Refresh();
            _body.Children.Add(_speeds);
            _body.Children.Add(Servers());
            _body.Children.Add(DefaultRoute());
        }
        if (_vpn.OtherVpns.Count > 0)
        {
            _body.Children.Add(Ui.Box(Ui.Cols("Auto,*", Icons.Make(Icons.Alert, 15, Look.Brush(Look.Amber)).Margin(0, 1, 8, 0).Top(),
                                              Ui.Text($"Включён другой VPN: {string.Join(", ", _vpn.OtherVpns)}. Выключи его, иначе трафик может пойти мимо Nova.", 12, FontWeight.Medium).Wrap()),
                                      Look.Amber, 0.12, Look.Amber, 0.35, 12, new Thickness(10)));
        }
        if (_vpn.LastError is { } err)
            _body.Children.Add(Ui.Text(err, 12, FontWeight.Medium, Look.Brush(Look.Danger)).Wrap());
        _body.Children.Add(Footer());
    }

    static void Detach(Control c)
    {
        if (c.Parent is Panel p) p.Children.Remove(c);
        else if (c.Parent is Decorator d) d.Child = null;
    }

    public static OrbState OrbStateFor(VpnController vpn) => vpn.Status switch
    {
        CoreStatus.On => vpn.HasDownServer ? OrbState.Warning : OrbState.On,
        CoreStatus.Connecting => OrbState.Connecting,
        CoreStatus.Error => OrbState.Error,
        _ => OrbState.Off,
    };

    string Headline() => _vpn.Status switch
    {
        CoreStatus.On => _vpn.HasDownServer ? "Сервер недоступен" : "Защищено",
        CoreStatus.Connecting => _vpn.Applying ? "Применяю…" : "Подключаюсь…",
        CoreStatus.Error => "Ошибка",
        CoreStatus.NotInstalled => "Нужна установка",
        CoreStatus.NeedsUpdate => "Нужно обновить",
        _ => "Выключено",
    };

    void UpdateSubline()
    {
        _subline.Text = _vpn.Status switch
        {
            CoreStatus.On when _vpn.ConnectedSince is { } since => $"в сети {(DateTime.Now - since):hh\\:mm\\:ss}",
            CoreStatus.On => "в сети",
            CoreStatus.Connecting => "запускаю ядро sing-box",
            CoreStatus.Error => _vpn.StatusMessage ?? "ошибка",
            CoreStatus.NotInstalled or CoreStatus.NeedsUpdate => "один раз, с правами администратора",
            _ => "нажми, чтобы включить",
        };
        ToolTip.SetTip(_subline, _vpn.Status == CoreStatus.Error ? _subline.Text : null);
    }

    Control Servers()
    {
        if (_vpn.Profile.Servers.Count == 0)
        {
            var add = new Pressable
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(14),
                Child = new Panel
                {
                    Children =
                    {
                        new MarchingBorder(14, Look.Violet.WithAlpha(0.5)),
                        Ui.Cols("Auto,*", Icons.Make(Icons.PlusCircle, 18, Look.Brush(Look.Violet)).Margin(0, 0, 10, 0),
                                Ui.V(1, Ui.Text("Добавь первый сервер", 13, FontWeight.Bold),
                                     Ui.Text("Окно → Серверы → вставь ссылку vless://…", 11, fg: Look.Brush(Look.Muted)))),
                    },
                },
            };
            add.Click += _openMain;
            return add;
        }
        var rows = Ui.V(6, _vpn.Profile.Servers.Select(s => (Control)ServerRow(s)).ToArray());
        // Up to three rows inline; more scroll, so the menu always fits the screen.
        Control list = _vpn.Profile.Servers.Count > 3 ? new ScrollViewer { Content = rows, Height = 3 * 48 + 2 * 6 } : rows;
        return Ui.V(6, Ui.Text("Серверы", 12, FontWeight.Bold, Look.Brush(Look.Muted)), list);
    }

    Control ServerRow(Server s)
    {
        var color = Look.Hex(s.ColorHex);
        var h = _vpn.Health.GetValueOrDefault(s.Id);
        Control? status = null;
        if (_vpn.Status == CoreStatus.On)
        {
            var down = h?.IsDown == true;
            var c = down ? Look.Danger : Look.Mint;
            status = Ui.H(6, new PulseDot(c).Center(),
                          Ui.Mono(down ? (h!.FallbackActive ? "напрямую" : "недоступен") : h?.DelayMs is { } ms ? $"{ms} мс" : "…", 12, Look.Brush(c)));
        }
        return new Border
        {
            Padding = new Thickness(12, 7),
            CornerRadius = new CornerRadius(14),
            Background = Ui.Gradient(color.WithAlpha(0.16), color.WithAlpha(0.02)),
            BorderBrush = Look.Brush(color, 0.25),
            BorderThickness = new Thickness(1),
            Child = Ui.Cols("Auto,*,Auto",
                new Border
                {
                    Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Look.Brush(color),
                    Child = Ui.Display(s.Code, 12, FontWeight.ExtraBold, Brushes.White).Center(),
                },
                Ui.V(1, Ui.Text(s.Name, 14, FontWeight.Bold).Ellipsis(), Ui.Text(s.ProtocolName.ToUpperInvariant(), 11, fg: Look.Brush(Look.Muted))).Margin(12, 0, 8, 0),
                status),
        };
    }

    Control DefaultRoute()
    {
        var p = _vpn.Profile;
        var options = p.Servers.Select(s => (s.Id, s.Code, Look.Hex(s.ColorHex))).Append((Target.Direct, "DIRECT", Look.DirectGray)).ToList();
        Control picker = options.Count <= 4
            ? new RouteSegment(options, p.Settings.DefaultTarget, _vpn.SetDefaultTarget)
            // Too many for a segmented control: a dropdown chip instead.
            : Ui.Cols("Auto,*", new TargetMenu(p, p.Settings.DefaultTarget, _vpn.SetDefaultTarget),
                      Ui.Text(p.Server(p.Settings.DefaultTarget)?.Name ?? "напрямую", 12, fg: Look.Brush(Look.Muted)).Ellipsis().Margin(10, 0, 0, 0));
        return Ui.V(6, Ui.Text("Всё остальное идёт через", 12, FontWeight.Bold, Look.Brush(Look.Muted)), picker);
    }

    Control Footer()
    {
        var routes = new NovaButton("Маршруты", _openMain, icon: Icons.Routes);
        var kill = new Border
        {
            Padding = new Thickness(10, 0),
            Height = 36,
            CornerRadius = new CornerRadius(11),
            BorderBrush = Look.Brush(Look.White, 0.1),
            BorderThickness = new Thickness(1),
            Child = Ui.H(8, Ui.Text("Kill switch", 12, FontWeight.Bold, Look.Brush(Look.Soft)), new NovaToggle(_vpn.Profile.Settings.KillSwitch, _vpn.SetKillSwitch)),
        };
        var quit = new NovaButton(Icons.Make(Icons.Power, 15, Look.Brush(Look.Text)), _vpn.Quit).Tip("Выключить VPN и выйти");
        AutomationProperties.SetName(quit, "Выйти");
        return Ui.Cols("*,Auto,Auto", routes, kill.Margin(10, 0, 10, 0), quit);
    }

    public static Control SetupCard(VpnController vpn, bool update) => Ui.Box(
        Ui.V(10,
             Ui.Text(update ? "Приложение обновилось — обнови фоновую службу."
                            : "VPN Nova запускает ядро sing-box как фоновую службу Windows. Для установки Windows попросит разрешение администратора — один раз.",
                     13, fg: Look.Brush(Look.Soft)).Wrap(),
             new NovaButton(update ? "Обновить службу" : "Установить службу", vpn.Install, prominent: true) { HorizontalAlignment = HorizontalAlignment.Left }),
        Look.White, 0.04, Look.Violet, 0.3, 14, new Thickness(14));
}

public sealed class StatusPill : Border
{
    public StatusPill(VpnController vpn)
    {
        var (text, color) = vpn.Status switch
        {
            CoreStatus.On => vpn.HasDownServer ? ("Сбой", Look.Amber) : ("Активен", Look.Mint),
            CoreStatus.Connecting => ("Старт", Look.Amber),
            CoreStatus.Error => ("Ошибка", Look.Danger),
            _ => ("Выкл", Look.Muted),
        };
        Padding = new Thickness(10, 5);
        CornerRadius = new CornerRadius(12);
        Background = Look.Brush(color, 0.12);
        Child = Ui.H(7, new PulseDot(color).Center(), Ui.Text(text, 12, FontWeight.Bold, Look.Brush(color)));
    }
}

/// The "nova" power button: rings, rotating halo and sparks when connected.
public sealed class OrbButton : Pressable
{
    readonly OrbEffects _effects = new();
    readonly Border _core;
    readonly Control _glyph = Icons.Make(Icons.Power, 34, Look.Brush(Look.Text), 2.2);
    OrbState? _state;

    public OrbButton(Action onClick)
    {
        Width = 156;
        Height = 156;
        CornerRadius = new CornerRadius(78);
        _core = new Border
        {
            Width = 108,
            Height = 108,
            CornerRadius = new CornerRadius(54),
            BorderThickness = new Thickness(1),
            Child = _glyph.Center(),
            RenderTransformOrigin = RelativePoint.Center,
            Transitions =
            [
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(400), Easing = new BackEaseOut() },
                new BoxShadowsTransition { Property = BoxShadowProperty, Duration = TimeSpan.FromMilliseconds(600) },
            ],
        };
        Child = new Panel { Children = { _effects, _core.Center() } };
        Click += onClick;
    }

    public void Apply(OrbState s, bool enabled)
    {
        IsEnabled = enabled;
        AutomationProperties.SetName(this, s == OrbState.Off ? "Включить VPN" : "Выключить VPN");
        if (s == _state) return;
        _state = s;
        _effects.Apply(s);
        var lit = s is OrbState.On or OrbState.Warning or OrbState.Connecting;
        var accent = s switch { OrbState.Warning => Look.Amber, OrbState.Error => Look.Danger, _ => Look.Violet };
        string[] colors = s switch
        {
            OrbState.On => ["#B4A9FF", "#5B4BE6", "#1F1766"],
            OrbState.Warning => ["#FFE1A3", "#D4891C", "#4A2C05"],
            OrbState.Error => ["#FF9AA4", "#B8283A", "#3A0A12"],
            OrbState.Connecting => ["#6F64C9", "#2E2777", "#151235"],
            _ => ["#262B4A", "#0E1020"],
        };
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.34, 0.26, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.34, 0.26, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.85, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.85, RelativeUnit.Relative),
        };
        for (var i = 0; i < colors.Length; i++) brush.GradientStops.Add(new GradientStop(Color.Parse(colors[i]), (double)i / (colors.Length - 1)));
        _core.Background = brush;
        _core.BorderBrush = Look.Brush(Look.White, lit ? 0.35 : 0.1);
        _core.BoxShadow = lit ? Ui.Glow(accent, 0.6, 36) : Ui.Glow(accent, 0, 36);
    }

    protected override void OnHover(bool hover) =>
        _core.RenderTransform = TransformOperations.Parse(hover ? "scale(1.04)" : "scale(1)");

    protected override void OnPress(bool pressed) =>
        _core.RenderTransform = TransformOperations.Parse(pressed ? "scale(0.97)" : IsPointerOver ? "scale(1.04)" : "scale(1)");
}

/// Download/upload tiles; only these re-render on the per-second traffic updates.
public sealed class SpeedsRow : Grid
{
    readonly VpnController _vpn;
    readonly SpeedTile _down = new("Загрузка");
    readonly SpeedTile _up = new("Отдача");

    public SpeedsRow(VpnController vpn)
    {
        _vpn = vpn;
        ColumnDefinitions = ColumnDefinitions.Parse("*,10,*");
        SetColumn(_up, 2);
        Children.Add(_down);
        Children.Add(_up);
        vpn.Stats.Changed += Refresh;
    }

    public void Refresh()
    {
        var active = _vpn.Status == CoreStatus.On;
        _down.Update(_vpn.Stats.Down, _vpn.Stats.DownHistory, Look.ColorFor(_vpn.Profile.Settings.DefaultTarget, _vpn.Profile), active);
        _up.Update(_vpn.Stats.Up, _vpn.Stats.UpHistory, Look.Mint.WithAlpha(0.75), active);
    }
}

public sealed class SpeedTile : Border
{
    readonly TextBlock _value = Ui.Display("0", 19);
    readonly TextBlock _unit = Ui.Text("Б/с", 11, FontWeight.Medium, Look.Brush(Look.Muted));
    readonly Sparkbars _bars = new() { Height = 22 };

    public SpeedTile(string title)
    {
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(14);
        Background = Look.Brush(Look.White, 0.04);
        BorderBrush = Look.Brush(Look.White, 0.07);
        BorderThickness = new Thickness(1);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _unit.Margin = new Thickness(0, 0, 0, 3);
        Child = Ui.V(2, Ui.Text(title, 12, fg: Look.Brush(Look.Muted)), Ui.H(4, _value, _unit), _bars.Margin(0, 4, 0, 0));
    }

    public void Update(long value, List<double> history, Color color, bool active)
    {
        var (v, u) = Look.FormatSpeed(active ? value : 0);
        _value.Text = v;
        _unit.Text = u;
        _bars.Set(history, color, active);
    }
}

public sealed class Sparkbars : Control
{
    List<double> _values = [];
    Color _color;
    bool _active;

    public void Set(List<double> values, Color color, bool active)
    {
        _values = values;
        _color = color;
        _active = active;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var vals = _values.TakeLast(24).ToList();
        if (vals.Count == 0) return;
        var max = Math.Max(vals.Max(), 1);
        const double gap = 2;
        var w = (Bounds.Width - gap * (vals.Count - 1)) / vals.Count;
        var brush = _active ? new SolidColorBrush(_color) : new SolidColorBrush(Colors.White, 0.1);
        for (var i = 0; i < vals.Count; i++)
        {
            var h = _active ? Math.Max(3, Bounds.Height * vals[i] / max) : 3;
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(i * (w + gap), Bounds.Height - h, w, h), 1.5));
        }
    }
}
