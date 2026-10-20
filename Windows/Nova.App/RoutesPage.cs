using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nova.Core;

namespace Nova.App;

/// Live map: sources (programs / domains) → servers, with particles flowing along the routes.
public sealed class RoutesPage : Page
{
    public sealed record Source(string Id, string Name, string Subtitle, Bitmap? Icon, string Target, int Count);
    public sealed record Node(string Id, string Title, string Subtitle, Color Color, int Count, bool IsDefault, bool Down);

    readonly LiveMetrics _metrics;
    readonly FlowMap _map = new();

    public RoutesPage(VpnController vpn) : base(vpn)
    {
        _metrics = new LiveMetrics(vpn);
    }

    protected override string Signature() => Sig(
        Vpn.Status, Vpn.HasDownServer, string.Join(",", Vpn.OtherVpns), ProfileSig,
        string.Join(";", Vpn.Health.OrderBy(h => h.Key).Select(h => $"{h.Key}:{h.Value}")),
        string.Join(";", Vpn.Connections.Select(c => c.Id + string.Join("/", c.Chains))));

    protected override void Render()
    {
        _metrics.Refresh(BlockedCount());
        if (_metrics.Parent is Panel old) old.Children.Remove(_metrics);
        if (_map.Parent is Panel oldMap) oldMap.Children.Remove(_map);

        Control? live = Vpn.Status == CoreStatus.On
            ? Ui.H(8, new PulseDot(Look.Mint).Center(), Ui.Text("Live", 13, FontWeight.Bold, Look.Brush(Look.Mint)))
            : null;
        var header = Ui.PageHeader("Маршруты", "Куда сейчас идёт трафик — в реальном времени",
            live == null ? null : new Border { Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(16), Background = Look.Brush(Look.Mint, 0.1), Child = live });

        var stack = new DockPanel { LastChildFill = true };
        void Top(Control c, double gap = 20)
        {
            DockPanel.SetDock(c, Dock.Top);
            c.Margin = new Thickness(0, 0, 0, gap);
            stack.Children.Add(c);
        }
        Top(header);
        Top(_metrics);
        if (Vpn.OtherVpns.Count > 0)
            Top(Ui.Banner($"Включён другой VPN: {string.Join(", ", Vpn.OtherVpns)}. Он конкурирует с Nova за маршруты — выключи его в его приложении или в Параметрах Windows → Сеть и Интернет → VPN.", Look.Amber));
        if (Vpn.Status == CoreStatus.On)
        {
            if (ConnectionInfo.ProcessLookupBroken(Vpn.Connections))
                Top(Ui.Banner("Ядро не видит, каким программам принадлежат соединения — правила по приложениям сейчас не срабатывают. Домены и «всё остальное» работают. Подробности в логах.", Look.Amber));
            var (sources, nodes) = Model();
            _map.Set(sources, nodes);
            stack.Children.Add(_map);
        }
        else
        {
            stack.Children.Add(EmptyState());
        }
        Child = stack;
    }

    int BlockedCount() =>
        Vpn.Profile.Settings.KillSwitch ? Vpn.Profile.Servers.Count(s => Vpn.Health.GetValueOrDefault(s.Id)?.IsDown == true) : 0;

    Control EmptyState()
    {
        var on = new NovaButton("Включить", Vpn.Connect, prominent: true) { HorizontalAlignment = HorizontalAlignment.Center };
        on.IsEnabled = Vpn.Status is CoreStatus.Off or CoreStatus.Error;
        var v = Ui.V(14,
            Icons.Make(Icons.Network, 44, Look.Brush(Look.Violet), 1.4).Center(),
            Ui.Display("Включи VPN, чтобы увидеть живую карту", 17).Center(),
            Ui.Text("Здесь появятся программы и сайты и то, через какой сервер они идут.", 14, fg: Look.Brush(Look.Muted)).Center(),
            on);
        v.VerticalAlignment = VerticalAlignment.Center;
        v.HorizontalAlignment = HorizontalAlignment.Center;
        return v;
    }

    (List<Source>, List<Node>) Model()
    {
        var ids = Vpn.Profile.Servers.Select(s => s.Id).ToList();
        var groups = new Dictionary<string, (string Name, string Sub, Bitmap? Icon, Dictionary<string, int> Targets, int Count)>();
        var counts = new Dictionary<string, int>();
        foreach (var c in Vpn.Connections)
        {
            var target = ConnectionInfo.Target(c.Chains, ids);
            counts[target] = counts.GetValueOrDefault(target) + 1;
            string key, name, sub;
            Bitmap? icon = null;
            if (Naming.ProgramPath(c.Metadata.ProcessPath) is { } path)
            {
                key = path.ToLowerInvariant();
                name = OperatingSystem.IsWindows() ? WinIntegration.DescriptionOf(path) : Naming.DisplayName(path);
                sub = "программа";
                icon = WinIntegration.IconFor(path);
            }
            else
            {
                key = name = !string.IsNullOrEmpty(c.Metadata.Host) ? c.Metadata.Host : c.Metadata.DestinationIp ?? "?";
                sub = "домен";
            }
            var g = groups.TryGetValue(key, out var existing) ? existing : (Name: name, Sub: sub, Icon: icon, Targets: new Dictionary<string, int>(), Count: 0);
            g.Targets[target] = g.Targets.GetValueOrDefault(target) + 1;
            g.Count++;
            groups[key] = g;
        }
        int Order(string t) => ids.IndexOf(t) is var i and >= 0 ? i : ids.Count;
        // Busiest 7, then grouped by destination (fewer crossing lines) and by name,
        // so cards keep their places between polls instead of jumping around.
        var sources = groups.Select(kv => new Source(kv.Key, kv.Value.Name, $"{kv.Value.Sub} · {kv.Value.Count}", kv.Value.Icon,
                                                     kv.Value.Targets.MaxBy(t => t.Value).Key, kv.Value.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(7)
            .OrderBy(s => Order(s.Target)).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var def = Vpn.Profile.Settings.DefaultTarget;
        var nodes = Vpn.Profile.Servers.Select(s =>
        {
            var h = Vpn.Health.GetValueOrDefault(s.Id);
            var delay = h?.DelayMs is { } ms ? $" · {ms} мс" : "";
            return new Node(s.Id, s.Code, s.Name + delay, Look.Hex(s.ColorHex), counts.GetValueOrDefault(s.Id), def == s.Id, h?.IsDown == true);
        }).ToList();
        nodes.Add(new Node(Target.Direct, "DIRECT", "Напрямую", Look.DirectGray, counts.GetValueOrDefault(Target.Direct), def == Target.Direct, false));
        return (sources, nodes);
    }
}

/// Metric tiles; re-render on traffic updates without touching the rest of the page.
public sealed class LiveMetrics : Grid
{
    readonly VpnController _vpn;
    readonly (TextBlock Value, TextBlock Unit)[] _tiles = new (TextBlock, TextBlock)[4];
    int _blocked;

    public LiveMetrics(VpnController vpn)
    {
        _vpn = vpn;
        ColumnDefinitions = ColumnDefinitions.Parse("*,14,*,14,*,14,*");
        string[] titles = ["Соединений", "Загрузка", "Отдача", "Заблокировано kill switch"];
        for (var i = 0; i < 4; i++)
        {
            var value = Ui.Display("0", 24);
            var unit = Ui.Text("", 12, fg: Look.Brush(Look.Muted));
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            unit.Margin = new Thickness(0, 0, 0, 4);
            _tiles[i] = (value, unit);
            var tile = new Border
            {
                Padding = new Thickness(16, 14),
                CornerRadius = new CornerRadius(16),
                Background = Look.Brush(Look.Panel),
                BorderBrush = Look.Brush(Look.White, 0.07),
                BorderThickness = new Thickness(1),
                Child = Ui.V(4, Ui.Text(titles[i], 12, fg: Look.Brush(Look.Muted)).Ellipsis(), Ui.H(4, value, unit)),
            };
            SetColumn(tile, i * 2);
            Children.Add(tile);
        }
        vpn.Stats.Changed += () => Refresh(_blocked);
        vpn.ConnectionsChanged += () => Refresh(_blocked);
    }

    public void Refresh(int blocked)
    {
        _blocked = blocked;
        var (d, du) = Look.FormatSpeed(_vpn.Stats.Down);
        var (u, uu) = Look.FormatSpeed(_vpn.Stats.Up);
        Set(0, _vpn.Connections.Count.ToString(), "");
        Set(1, d, du);
        Set(2, u, uu);
        Set(3, blocked.ToString(), string.Join(" ", Look.Plural(blocked, "сервер", "сервера", "серверов").Split(' ').Skip(1)));
    }

    void Set(int i, string v, string unit)
    {
        _tiles[i].Value.Text = v;
        _tiles[i].Unit.Text = unit;
    }
}

/// Source cards on the left, server nodes on the right, animated curves between them.
public sealed class FlowMap : Panel
{
    const double SourceWidth = 230, SourceHeight = 58, NodeWidth = 240;
    readonly FlowPipes _pipes = new();
    readonly FlowLayer _flow = new();
    List<RoutesPage.Source> _sources = [];
    List<RoutesPage.Node> _nodes = [];
    readonly List<Control> _sourceCards = [];
    readonly List<Control> _nodeCards = [];
    TextBlock? _quiet;

    public FlowMap()
    {
        Children.Add(_pipes);
        Children.Add(_flow);
    }

    public void Set(List<RoutesPage.Source> sources, List<RoutesPage.Node> nodes)
    {
        _sources = sources;
        _nodes = nodes;
        foreach (var c in _sourceCards.Concat(_nodeCards)) Children.Remove(c);
        if (_quiet != null) Children.Remove(_quiet);
        _sourceCards.Clear();
        _nodeCards.Clear();
        foreach (var s in sources) { var c = SourceCard(s); _sourceCards.Add(c); Children.Add(c); }
        foreach (var n in nodes) { var c = new NodeCard(n); _nodeCards.Add(c); Children.Add(c); }
        _quiet = sources.Count == 0 ? Ui.Text("Пока тихо — соединений нет", 14, fg: Look.Brush(Look.Muted)) : null;
        if (_quiet != null) Children.Add(_quiet);
        InvalidateArrange();
    }

    static List<double> Positions(int count, double height, double item)
    {
        if (count == 0) return [];
        var gap = count > 1 ? Math.Min(30, (height - count * item) / (count - 1)) : 0;
        var total = count * item + (count - 1) * gap;
        var top = (height - total) / 2;
        return Enumerable.Range(0, count).Select(i => top + i * (item + gap) + item / 2).ToList();
    }

    double NodeHeight(double h) => Math.Max(36, Math.Min(120, (h - (_nodes.Count - 1) * 10) / Math.Max(_nodes.Count, 1)));

    protected override Size MeasureOverride(Size available)
    {
        var h = double.IsInfinity(available.Height) ? 400 : available.Height;
        foreach (var c in _sourceCards) c.Measure(new Size(SourceWidth, SourceHeight));
        foreach (var c in _nodeCards)
        {
            // One line when the map is crowded; the full card otherwise.
            ((NodeCard)c).SetCompact(NodeHeight(h) < 92);
            c.Measure(new Size(NodeWidth, NodeHeight(h)));
        }
        _quiet?.Measure(available);
        _pipes.Measure(available);
        _flow.Measure(available);
        return new Size(double.IsInfinity(available.Width) ? 800 : available.Width, h);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var w = size.Width;
        var h = size.Height;
        var srcY = Positions(_sources.Count, h, SourceHeight);
        var nodeH = NodeHeight(h);
        var nodeY = Positions(_nodes.Count, h, nodeH);
        for (var i = 0; i < _sourceCards.Count; i++) _sourceCards[i].Arrange(new Rect(0, srcY[i] - SourceHeight / 2, SourceWidth, SourceHeight));
        for (var i = 0; i < _nodeCards.Count; i++) _nodeCards[i].Arrange(new Rect(w - NodeWidth, nodeY[i] - nodeH / 2, NodeWidth, nodeH));
        _quiet?.Arrange(new Rect(SourceWidth, 0, Math.Max(w - SourceWidth - NodeWidth, 10), h).CenterRect(new Rect(_quiet.DesiredSize)));
        _pipes.Arrange(new Rect(size));
        _flow.Arrange(new Rect(size));
        var routes = new List<FlowRoute>();
        for (var i = 0; i < _sources.Count; i++)
        {
            var ni = _nodes.FindIndex(n => n.Id == _sources[i].Target);
            if (ni < 0) continue;
            routes.Add(new FlowRoute(new Point(SourceWidth, srcY[i]), new Point(w - NodeWidth, nodeY[ni]), _nodes[ni].Color, Math.Min(1 + _sources[i].Count / 3, 4)));
        }
        _pipes.Update(routes);
        _flow.Update(routes);
        return size;
    }

    static Control SourceCard(RoutesPage.Source s)
    {
        Control icon = s.Icon is { } bmp
            ? new Image { Source = bmp, Width = 34, Height = 34 }
            : new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Look.Brush(Look.White, 0.06),
                Child = Icons.Make(Icons.Globe, 17, Look.Brush(Look.Soft)).Center(),
            };
        return new Border
        {
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(14),
            Background = Look.Brush(Look.Panel),
            BorderBrush = Look.Brush(Look.White, 0.08),
            BorderThickness = new Thickness(1),
            Child = Ui.Cols("Auto,*", icon,
                            Ui.V(1, Ui.Text(s.Name, 14, FontWeight.Bold).Ellipsis(), Ui.Text(s.Subtitle, 12, fg: Look.Brush(Look.Muted)).Ellipsis())
                                .Margin(12, 0, 0, 0)),
        };
    }
}

public sealed class NodeCard : Border
{
    readonly RoutesPage.Node _n;

    public NodeCard(RoutesPage.Node n)
    {
        _n = n;
        CornerRadius = new CornerRadius(18);
        Background = Ui.Gradient(n.Color.WithAlpha(0.85), n.Color.WithAlpha(0.25), diagonal: true);
        BoxShadow = Ui.Glow(n.Color, n.Count > 0 ? 0.4 : 0.12, 22);
    }

    bool? _compact;

    public void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        Child = new Panel { Children = { new BreathingRim(18, _n.Count > 0), compact ? Compact() : Full() } };
    }

    Control Compact()
    {
        var text = Ui.V(0, Ui.Text(_n.Subtitle, 11, FontWeight.Bold, Brushes.White).Ellipsis(),
                        Ui.Text(_n.Down ? "недоступен" : Look.Plural(_n.Count, "соединение", "соединения", "соединений"), 10, fg: Look.Brush(Look.White, 0.75)).Ellipsis())
            .Margin(10, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Cols("Auto,*", Ui.Display(_n.Title, _n.Title.Length > 3 ? 14 : 17, FontWeight.ExtraBold, Brushes.White), text).Margin(12, 0, 12, 0);
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    Control Full()
    {
        Control? badge = _n.Down
            ? new Border { Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(10), Background = Look.Brush(Look.Danger, 0.3), Child = Ui.Text("недоступен", 11, FontWeight.Bold, Brushes.White) }
            : _n.IsDefault
                ? new Border { Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(10), Background = Look.Brush(Look.White, 0.14), Child = Ui.Text("по умолчанию", 11, FontWeight.Bold, Brushes.White) }
                : null;
        var top = Ui.Cols("*,Auto", Ui.Display(_n.Title, _n.Title.Length > 3 ? 24 : 32, FontWeight.ExtraBold, Brushes.White).Top(), badge?.Top());
        var bottom = Ui.V(0, Ui.Text(_n.Subtitle, 14, FontWeight.Bold, Brushes.White).Ellipsis(),
                          Ui.Text(Look.Plural(_n.Count, "соединение", "соединения", "соединений"), 12, fg: Look.Brush(Look.White, 0.7)));
        bottom.VerticalAlignment = VerticalAlignment.Bottom;
        return new Panel { Margin = new Thickness(18, 14), Children = { top.Top(), bottom } };
    }
}
