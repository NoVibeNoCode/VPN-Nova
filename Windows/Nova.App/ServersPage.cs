using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Nova.Core;

namespace Nova.App;

public sealed class ServersPage : Page
{
    // Kept across re-renders so typing isn't lost when health updates arrive.
    readonly TextBox _link = Ui.TextField("vless://…");
    readonly TextBlock _linkError = Ui.Text("", 12, FontWeight.Medium, Look.Brush(Look.Danger));
    readonly Control _dropZone;

    public ServersPage(VpnController vpn) : base(vpn)
    {
        _linkError.IsVisible = false;
        _linkError.TextWrapping = TextWrapping.Wrap;
        _link.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ImportLink(); };
        _link.TextChanged += (_, _) => _linkError.IsVisible = false;
        _dropZone = DropZone();
    }

    protected override string Signature() => Sig(ProfileSig, Vpn.Status,
        string.Join(";", Vpn.Health.OrderBy(h => h.Key).Select(h => $"{h.Key}:{h.Value}")),
        string.Join(";", Vpn.DelayHistory.Select(h => h.Key + string.Join(",", h.Value))));

    protected override void Render()
    {
        if (_dropZone.Parent is Panel old) old.Children.Remove(_dropZone);
        var header = Ui.PageHeader("Серверы", "Через что может идти трафик",
            new NovaButton("Добавить", () => ServerEditor.Show(this, Vpn, null), prominent: true, icon: Icons.Plus));
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,14,*") };
        var servers = Vpn.Profile.Servers;
        for (var i = 0; i < servers.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var s = servers[i];
            var card = ServerCard(s);
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2 * 2);
            card.Margin = new Thickness(0, 0, 0, 14);
            grid.Children.Add(card);
        }
        var scroll = new ScrollViewer
        {
            Content = Ui.V(0, grid, _dropZone),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
        };
        Child = Ui.Rows("Auto,*", header.Margin(0, 0, 0, 20), scroll);
    }

    void ImportLink()
    {
        try
        {
            var parsed = LinkParser.Parse(_link.Text ?? "");
            var used = Vpn.Profile.Servers.Select(s => s.ColorHex).ToHashSet();
            var color = Server.Palette.FirstOrDefault(c => !used.Contains(c)) ?? Server.Palette[0];
            var server = new Server(parsed.Name, Naming.SuggestedCode(parsed.Name), color, parsed.Outbound);
            _linkError.IsVisible = false;
            void Add()
            {
                Vpn.Update(p => p.Servers.Add(server));
                _link.Text = "";
            }
            if (server.SkipsCertificateCheck)
            {
                Dialogs.Confirm(this, "Сертификат сервера не проверяется",
                    "В ссылке включён allowInsecure. Кто угодно на пути к серверу сможет подменить его и читать трафик. Безопаснее попросить у владельца сервера ссылку с нормальным сертификатом или с Reality.",
                    "Добавить всё равно", Add);
                return;
            }
            Add();
        }
        catch (LinkParser.ParseException e)
        {
            _linkError.Text = e.Message;
            _linkError.IsVisible = true;
        }
    }

    Control DropZone()
    {
        var import = new NovaButton("Импорт", ImportLink, prominent: true);
        _link.TextChanged += (_, _) => import.IsEnabled = !string.IsNullOrWhiteSpace(_link.Text);
        import.IsEnabled = false;
        return new Panel
        {
            Children =
            {
                new MarchingBorder(18, Look.Violet.WithAlpha(0.6)),
                Ui.V(12,
                    Ui.Cols("Auto,*", Icons.Make(Icons.Link, 20, Look.Brush(Look.Violet)).Margin(0, 0, 12, 0),
                            Ui.V(2, Ui.Text("Вставь ссылку сервера", 14, FontWeight.Bold),
                                 Ui.Text("vless://, vmess://, trojan://, ss://, hy2://, tuic://", 12, fg: Look.Brush(Look.Muted)))),
                    Ui.Cols("*,Auto", _link, import.Margin(10, 0, 0, 0)),
                    _linkError).Margin(20, 20, 20, 20),
            },
        };
    }

    Control ServerCard(Server s)
    {
        var color = Look.Hex(s.ColorHex);
        var active = Vpn.Status == CoreStatus.On;
        var h = Vpn.Health.GetValueOrDefault(s.Id);
        var edit = new Pressable
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Background = Look.Brush(Look.White, 0.12),
            Child = Icons.Make(Icons.Pencil, 15, Brushes.White).Center(),
        };
        edit.Click += () => ServerEditor.Show(this, Vpn, s);
        AutomationProperties.SetName(edit, "Изменить сервер");

        var tags = new WrapPanel();
        foreach (var t in Tags(s))
            tags.Children.Add(new Border { Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 4), CornerRadius = new CornerRadius(10), Background = Look.Brush(Look.White, 0.12), Child = Ui.Text(t, 11, FontWeight.Bold, Brushes.White) });
        if (s.SkipsCertificateCheck)
            tags.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 4), CornerRadius = new CornerRadius(10), Background = Look.Brush(Look.Danger, 0.85),
                Child = Ui.H(5, Icons.Make(Icons.ShieldAlert, 12, Brushes.White), Ui.Text("сертификат не проверяется", 11, FontWeight.Bold, Brushes.White)),
            }.Tip("allowInsecure: соединение можно перехватить"));

        Control delay = active && h?.IsDown == true
            ? Ui.Display(h.FallbackActive ? "напрямую" : "недоступен", 16, FontWeight.Bold, Look.Brush(Look.Amber)).Right()
            : Ui.H(3, Ui.Display(active ? h?.DelayMs?.ToString() ?? "…" : "—", 24, FontWeight.Bold, Brushes.White),
                   Ui.Text("мс", 12, fg: Look.Brush(Look.White, 0.7)).Margin(0, 0, 0, 3).Bottom()).Right();

        return new Border
        {
            Padding = new Thickness(20, 18),
            CornerRadius = new CornerRadius(20),
            Background = Ui.Gradient(color.WithAlpha(0.75), color.WithAlpha(0.18), diagonal: true),
            BorderBrush = Look.Brush(Look.White, 0.22),
            BorderThickness = new Thickness(1),
            BoxShadow = Ui.Glow(color, 0.3, 24, 10),
            Child = Ui.V(12,
                Ui.Cols("Auto,*,Auto",
                    Ui.Display(s.Code, 36, FontWeight.ExtraBold, Brushes.White).Top(),
                    Ui.V(2, Ui.Text(s.Name, 16, FontWeight.Bold, Brushes.White).Ellipsis(),
                         Ui.Mono($"{Mask(s.Host)} : {s.Port?.ToString() ?? "—"}", 12, Look.Brush(Look.White, 0.7))).Margin(14, 4, 8, 0).Top(),
                    edit.Top()),
                tags,
                Ui.Cols("*,Auto", new LatencyLine(s.Id, Vpn.DelayHistory.GetValueOrDefault(s.Id) ?? []) { Height = 46 }.Margin(0, 0, 16, 0),
                        Ui.V(0, delay, Ui.Text("задержка", 12, fg: Look.Brush(Look.White, 0.7)).Right()).Bottom())),
        };
    }

    static List<string> Tags(Server s)
    {
        var t = new List<string> { s.ProtocolName.ToUpperInvariant() };
        if (s.Outbound["tls"]?["reality"]?["enabled"].IsTrue() == true) t.Add("Reality");
        else if (s.Outbound["tls"]?["enabled"].IsTrue() == true) t.Add("TLS");
        if (s.Outbound["flow"].StringValue()?.Contains("vision") == true) t.Add("Vision");
        if (s.Outbound["transport"]?["type"].StringValue() is { } tr) t.Add(tr.ToUpperInvariant());
        return t;
    }

    static string Mask(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => int.TryParse(p, out _)) ? $"{parts[0]}.{parts[1]}.•••.{parts[3]}" : host;
    }
}

public static class ControlExt
{
    public static T Bottom<T>(this T c) where T : Control
    {
        c.VerticalAlignment = VerticalAlignment.Bottom;
        return c;
    }
}

/// Latency sparkline that draws itself in on first show.
public sealed class LatencyLine : Control
{
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<LatencyLine, double>(nameof(Progress));
    static readonly HashSet<string> Drawn = [];
    readonly List<int> _values;

    static LatencyLine() => AffectsRender<LatencyLine>(ProgressProperty);

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public LatencyLine(string serverId, List<int> values)
    {
        _values = values;
        Progress = 1;
        AttachedToVisualTree += (_, _) =>
        {
            // Only a server's first appearance animates; re-renders on health updates don't redraw from zero.
            if (!Drawn.Add(serverId)) return;
            _ = new Animation
            {
                Duration = TimeSpan.FromSeconds(1.6),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ProgressProperty, 0.0) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ProgressProperty, 1.0) } },
                },
            }.RunAsync(this);
        };
    }

    public override void Render(DrawingContext ctx)
    {
        var vals = _values.Count == 0 ? [0, 0] : _values.Select(v => v == 0 ? (_values.Where(x => x > 0).DefaultIfEmpty(100).Max()) : v).ToList();
        if (vals.Count == 1) vals.Add(vals[0]);
        double max = Math.Max(vals.Max(), 1), min = vals.Min();
        var span = Math.Max(max - min, 20);
        var pts = vals.Select((v, i) => new Point(Bounds.Width * i / Math.Max(vals.Count - 1, 1), Bounds.Height * (0.15 + 0.7 * (v - min) / span))).ToList();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            // Trim to Progress along x.
            var limit = Bounds.Width * Progress;
            g.BeginFigure(pts[0], false);
            for (var i = 1; i < pts.Count; i++)
            {
                if (pts[i].X <= limit) { g.LineTo(pts[i]); continue; }
                var prev = pts[i - 1];
                var f = (limit - prev.X) / Math.Max(pts[i].X - prev.X, 0.001);
                g.LineTo(new Point(limit, prev.Y + (pts[i].Y - prev.Y) * f));
                break;
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Colors.White, 0.85), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
    }
}

public static class ServerEditor
{
    public static void Show(Control owner, VpnController vpn, Server? original)
    {
        var win = MainWindow.Of(owner);
        if (win == null) return;
        var name = Ui.TextField("Германия", original?.Name ?? "");
        var code = Ui.TextField("DE", original?.Code ?? "");
        code.MaxLength = 6;
        code.Width = 90;
        var used = vpn.Profile.Servers.Select(s => s.ColorHex).ToHashSet();
        var color = original?.ColorHex ?? Server.Palette.FirstOrDefault(c => !used.Contains(c)) ?? Server.Palette[0];
        var link = Ui.TextField("vless://…");
        var json = new TextBox
        {
            Classes = { "nova", "code" },
            Height = 220,
            Text = original?.Outbound.Pretty() ?? new JsonObject { ["type"] = "vless", ["server"] = "", ["server_port"] = 443, ["uuid"] = "" }.Pretty(),
        };
        var error = Ui.Text("", 12, FontWeight.Medium, Look.Brush(Look.Danger)).Wrap();
        error.IsVisible = false;
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        void Fail(string m)
        {
            error.Text = m;
            error.IsVisible = true;
        }

        void BuildColors()
        {
            colors.Children.Clear();
            foreach (var hex in Server.Palette)
            {
                var b = new Pressable
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Look.Brush(Look.Hex(hex)),
                    BorderBrush = Brushes.White, BorderThickness = new Thickness(color == hex ? 2.5 : 0),
                };
                var h = hex;
                b.Click += () => { color = h; BuildColors(); };
                AutomationProperties.SetName(b, $"Цвет {hex}");
                colors.Children.Add(b);
            }
        }

        void ParseLink()
        {
            try
            {
                var p = LinkParser.Parse(link.Text ?? "");
                json.Text = p.Outbound.Pretty();
                if (string.IsNullOrEmpty(name.Text)) name.Text = p.Name;
                if (string.IsNullOrEmpty(code.Text)) code.Text = Naming.SuggestedCode(p.Name);
                error.IsVisible = false;
            }
            catch (LinkParser.ParseException e) { Fail(e.Message); }
        }

        void Save(bool insecureAccepted)
        {
            var n = (name.Text ?? "").Trim();
            var c = (code.Text ?? "").Trim();
            if (n.Length == 0) { Fail("Укажи название"); return; }
            if (c.Length == 0) { Fail("Укажи метку, например DE"); return; }
            JsonObject obj;
            try
            {
                if (JsonNode.Parse(json.Text ?? "") is not JsonObject o || string.IsNullOrEmpty(o.Str("type")))
                {
                    Fail("В JSON нужен объект с полем type");
                    return;
                }
                obj = o;
            }
            catch (JsonException e)
            {
                Fail($"JSON не разбирается: {e.Message}");
                return;
            }
            obj.Remove("tag");
            var server = original?.Clone() ?? new Server(n, c, color, obj);
            server.Name = n;
            server.Code = c.ToUpper();
            server.ColorHex = color;
            server.Outbound = obj;
            if (server.SkipsCertificateCheck && !insecureAccepted && original?.SkipsCertificateCheck != true)
            {
                // Asked on top of the editor; "Отмена" goes back to it.
                Dialogs.Confirm(owner, "Сертификат сервера не проверяется",
                    "В tls включён insecure. Кто угодно на пути к серверу сможет подменить его и читать трафик.",
                    "Сохранить всё равно", () => Commit(server));
                return;
            }
            Commit(server);
        }

        void Commit(Server server)
        {
            vpn.Update(p =>
            {
                var i = p.Servers.FindIndex(x => x.Id == server.Id);
                if (i >= 0) p.Servers[i] = server; else p.Servers.Add(server);
            });
            win.CloseSheet();
        }

        void Delete()
        {
            if (original == null) return;
            Dialogs.Confirm(owner, "Удалить сервер?", "Правила, которые вели на этот сервер, переключатся на DIRECT.", "Удалить", () =>
            {
                vpn.Update(p =>
                {
                    p.Servers.RemoveAll(x => x.Id == original.Id);
                    foreach (var r in p.DomainRules.Where(r => r.Target == original.Id)) r.Target = Target.Direct;
                    foreach (var r in p.AppRules.Where(r => r.Target == original.Id)) r.Target = Target.Direct;
                    if (p.Settings.DefaultTarget == original.Id) p.Settings.DefaultTarget = Target.Direct;
                });
                win.CloseSheet();
            });
        }

        link.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ParseLink(); };
        BuildColors();
        win.ShowSheet(Ui.V(16,
            Ui.Display(original == null ? "Новый сервер" : $"Сервер {original.Code}", 20),
            Ui.Cols("*,12,Auto", Dialogs.Field("Название", name), null, Dialogs.Field("Метка", code)),
            Dialogs.Field("Цвет", colors),
            Dialogs.Field("Ссылка (заполнит JSON)", Ui.Cols("*,Auto", link, new NovaButton("Разобрать", ParseLink).Margin(8, 0, 0, 0))),
            Dialogs.Field("Outbound sing-box (JSON, без tag)", json),
            error,
            Ui.Cols("Auto,*,Auto",
                original != null ? new NovaButton(Ui.Text("Удалить", 13, FontWeight.Bold, Look.Brush(Look.Danger)), Delete) : null,
                null,
                Ui.H(10, new NovaButton("Отмена", win.CloseSheet), new NovaButton("Сохранить", () => Save(false), prominent: true)))), 620);
    }
}
