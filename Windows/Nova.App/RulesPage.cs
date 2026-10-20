using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nova.Core;

namespace Nova.App;

public sealed class RulesPage : Page
{
    readonly Control _pipeline = PriorityPipeline();

    public RulesPage(VpnController vpn) : base(vpn) { }

    protected override string Signature() => ProfileSig;

    protected override void Render()
    {
        if (_pipeline.Parent is Panel old) old.Children.Remove(_pipeline);
        var p = Vpn.Profile;
        var header = Ui.PageHeader("Правила", "Нажми на метку сервера, чтобы выбрать маршрут",
            Ui.H(10, new NovaButton("Домен", () => AddDomainSheet.Show(this, Vpn), icon: Icons.Plus),
                 new NovaButton("Приложение", () => AppPickerSheet.Show(this, Vpn), prominent: true, icon: Icons.Plus)));

        var domains = new List<Control>();
        if (p.DomainRules.Count == 0) domains.Add(EmptyHint("Добавь домен, зону вроде .ru или готовый набор — YouTube, Китай…"));
        domains.AddRange(p.DomainRules.Select(r => DomainRow(r)));
        var apps = new List<Control>();
        if (p.AppRules.Count == 0) apps.Add(EmptyHint("Добавь программу — весь её трафик пойдёт через выбранный сервер."));
        apps.AddRange(p.AppRules.Select(r => AppRow(r)));

        var columns = Ui.Cols("*,24,*", Column("ДОМЕНЫ", Icons.Globe, p.DomainRules.Count, domains), null,
                              Column("ПРИЛОЖЕНИЯ", Icons.Grid, p.AppRules.Count, apps));
        Child = Ui.Rows("Auto,Auto,*,Auto", header.Margin(0, 0, 0, 20), _pipeline.Margin(0, 0, 0, 20), columns, RestRow().Margin(0, 20, 0, 0));
    }

    static Control PriorityPipeline()
    {
        Control Step(int n, string title, string sub, bool highlight) => new Border
        {
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(14),
            Background = Look.Brush(Look.Panel),
            BorderBrush = highlight ? Look.Brush(Look.Violet, 0.35) : Look.Brush(Look.White, 0.08),
            BorderThickness = new Thickness(1),
            // Narrow window: the labels shrink (like minimumScaleFactor on macOS) instead of being cut mid-word.
            Child = Ui.Cols("Auto,*", Ui.StepNumber(n, highlight),
                            new Viewbox
                            {
                                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 0),
                                Child = Ui.V(1, Ui.Text(title, 13, FontWeight.Bold), Ui.Text(sub, 11, fg: Look.Brush(Look.Muted))),
                            }),
        };
        Control Chevron() => Icons.Make(Icons.ChevronRight, 12, Look.Brush(Color.Parse("#4A4F70")), 3).Center();
        var row = Ui.Cols("*,22,*,22,*,22,*",
            Step(1, "Локальная сеть", "всегда напрямую", false), Chevron(),
            Step(2, "Домены", "точнее — главнее", true), Chevron(),
            Step(3, "Приложения", "весь её трафик", true), Chevron(),
            Step(4, "Всё остальное", "по умолчанию", false));
        return new Border
        {
            Height = 62,
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Child = new Panel { Children = { row, new SweepingLight(true, Look.Violet.WithAlpha(0.35), 120, 3.2) } },
        };
    }

    static Control Column(string title, string icon, int count, List<Control> rows)
    {
        var head = Ui.H(8, Icons.Make(icon, 14, Look.Brush(Look.Muted)), Ui.Text($"{title} · {count}", 12, FontWeight.Bold, Look.Brush(Look.Muted)));
        var list = new ScrollViewer
        {
            Content = Ui.V(10, rows.ToArray()).Margin(0, 0, 0, 4),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
        };
        return Ui.Rows("Auto,*", head.Margin(0, 0, 0, 10), list);
    }

    static Control EmptyHint(string text) => new Panel
    {
        Children =
        {
            new MarchingBorder(14, Look.Violet.WithAlpha(0.4)),
            Ui.Text(text, 13, fg: Look.Brush(Look.Muted)).Wrap().Margin(16, 16, 16, 16),
        },
    };

    Control RestRow()
    {
        var target = Vpn.Profile.Settings.DefaultTarget;
        var color = Look.ColorFor(target, Vpn.Profile);
        return new Border
        {
            Height = 64,
            Padding = new Thickness(18, 0),
            CornerRadius = new CornerRadius(16),
            Background = Ui.Gradient(color.WithAlpha(0.22), color.WithAlpha(0.04)),
            BorderBrush = Look.Brush(color, 0.3),
            BorderThickness = new Thickness(1),
            Child = Ui.Cols("Auto,*,Auto", Ui.StepNumber(4, false),
                            Ui.V(2, Ui.Text("Всё остальное", 15, FontWeight.Bold), Ui.Text("трафик, который не попал ни в одно правило", 12, fg: Look.Brush(Look.Soft))).Margin(14, 0, 0, 0),
                            new TargetMenu(Vpn.Profile, target, Vpn.SetDefaultTarget)),
        };
    }

    Control RowShell(string title, string subtitle, string target, bool enabled, Control leading, Action<string> onTarget, Action onToggle, Action onDelete)
    {
        var toggle = new Pressable { Child = Icons.Make(enabled ? Icons.Pause : Icons.Play, 17, Look.Brush(Look.Muted)), Padding = new Thickness(4) }
            .Tip(enabled ? "Выключить правило" : "Включить правило");
        toggle.Click += onToggle;
        AutomationProperties.SetName(toggle, enabled ? "Выключить правило" : "Включить правило");
        var delete = new DeleteButton(onDelete);
        // (PrefixCharacterEllipsis would match the macOS middle truncation, but crashes Avalonia 11.3 on long text.)
        var subtitleText = Ui.Text(subtitle, 12, fg: Look.Brush(Look.Muted)).Ellipsis().Tip(subtitle);
        return new Border
        {
            Height = 56,
            Padding = new Thickness(12, 0, 8, 0),
            CornerRadius = new CornerRadius(14),
            Background = Look.Brush(Look.Panel),
            BorderBrush = Look.Brush(Look.White, 0.07),
            BorderThickness = new Thickness(1),
            Opacity = enabled ? 1 : 0.45,
            Child = Ui.Cols("Auto,*,Auto,Auto,Auto",
                new Border { Width = 34, Height = 34, Child = leading },
                Ui.V(1, Ui.Text(title, 14, FontWeight.Bold).Ellipsis(), subtitleText).Margin(12, 0, 6, 0),
                new TargetMenu(Vpn.Profile, target, onTarget),
                toggle.Margin(8, 0, 2, 0),
                delete),
        };
    }

    Control DomainRow(DomainRule rule)
    {
        string title, subtitle, icon;
        switch (rule.Kind)
        {
            case DomainRuleKind.Domain:
                var d = ConfigBuilder.NormalizeDomain(rule.Value);
                title = d.Contains('.') ? d : $".{d}";
                subtitle = d.Contains('.') ? "домен и поддомены" : "вся зона";
                icon = Icons.Globe;
                break;
            case DomainRuleKind.Keyword:
                (title, subtitle, icon) = (rule.Value, "ключевое слово", Icons.Type);
                break;
            case DomainRuleKind.Ip:
                (title, subtitle, icon) = (rule.Value, "IP / подсеть", Icons.Hash);
                break;
            default:
                (title, subtitle, icon) = (RuleSetPreset.TitleFor(rule.Value), $"набор · {rule.Value}", Icons.Layers);
                break;
        }
        var leading = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Look.Brush(Look.ColorFor(rule.Target, Vpn.Profile), 0.18),
            Child = Icons.Make(icon, 15, Look.Brush(Look.Soft)).Center(),
        };
        var id = rule.Id;
        void Mutate(Action<DomainRule> f) => Vpn.Update(p => { if (p.DomainRules.FirstOrDefault(r => r.Id == id) is { } r) f(r); });
        return RowShell(title, subtitle, rule.Target, rule.Enabled, leading,
                        t => Mutate(r => r.Target = t), () => Mutate(r => r.Enabled = !r.Enabled),
                        () => Vpn.Update(p => p.DomainRules.RemoveAll(r => r.Id == id)));
    }

    Control AppRow(AppRule rule)
    {
        var exists = File.Exists(rule.Path) || Directory.Exists(rule.Path);
        var subtitle = !exists && OperatingSystem.IsWindows() ? $"не найдено: {rule.Path}"
            : Naming.UsesSharedWebView(rule.Path) ? "ходит в сеть через общий WebView2 — лучше правила по доменам" : rule.Path;
        var id = rule.Id;
        void Mutate(Action<AppRule> f) => Vpn.Update(p => { if (p.AppRules.FirstOrDefault(r => r.Id == id) is { } r) f(r); });
        return RowShell(rule.Name, subtitle, rule.Target, rule.Enabled, AppIcon(rule.Path, 34),
                        t => Mutate(r => r.Target = t), () => Mutate(r => r.Enabled = !r.Enabled),
                        () => Vpn.Update(p => p.AppRules.RemoveAll(r => r.Id == id)));
    }

    public static Control AppIcon(string path, double size) =>
        WinIntegration.IconFor(path) is { } bmp
            ? new Image { Source = bmp, Width = size, Height = size }
            : new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = Look.Brush(Look.White, 0.06),
                Child = Icons.Make(Icons.App, size * 0.5, Look.Brush(Look.Soft)).Center(),
            };
}

/// The ✕ that turns red on hover.
public sealed class DeleteButton : Pressable
{
    readonly Border _box;

    public DeleteButton(Action onDelete)
    {
        _box = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8) };
        Child = _box;
        Paint(false);
        Click += onDelete;
        AutomationProperties.SetName(this, "Удалить правило");
    }

    void Paint(bool hover)
    {
        _box.Background = hover ? Look.Brush(Look.Danger, 0.15) : Brushes.Transparent;
        _box.Child = Icons.Make(Icons.X, 13, Look.Brush(hover ? Look.Danger : Color.Parse("#6B7092")), 2.6).Center();
    }

    protected override void OnHover(bool hover) => Paint(hover);
}

// MARK: - Add domain

public static class AddDomainSheet
{
    public static void Show(Control owner, VpnController vpn)
    {
        var win = MainWindow.Of(owner);
        if (win == null) return;
        var kind = DomainRuleKind.Domain;
        string? preset = null;
        var target = vpn.Profile.Servers.FirstOrDefault()?.Id ?? Target.Direct;
        var value = Ui.TextField("youtube.com или .ru");
        var error = Ui.Text("", 12, FontWeight.Medium, Look.Brush(Look.Danger));
        error.IsVisible = false;
        var kindHost = new Border();
        var presetsHost = new Border();
        var targetHost = new Border();
        var body = Ui.V(18);

        void Build()
        {
            kindHost.Child = new RouteSegment(DomainRuleKindInfo.All.Select(k => (k.ToString(), k.Title(), Look.VioletDeep)).ToList(), kind.ToString(), s =>
            {
                kind = Enum.Parse<DomainRuleKind>(s);
                value.Watermark = kind switch
                {
                    DomainRuleKind.Domain => "youtube.com или .ru",
                    DomainRuleKind.Keyword => "openai",
                    DomainRuleKind.Ip => "203.0.113.0/24",
                    _ => "geosite-youtube",
                };
                Build();
            });
            presetsHost.IsVisible = kind == DomainRuleKind.Ruleset;
            if (kind == DomainRuleKind.Ruleset)
            {
                var grid = new WrapPanel { ItemWidth = 160, ItemHeight = 44 };
                foreach (var p in RuleSetPreset.All)
                {
                    var on = preset == p.Id;
                    var b = new Pressable
                    {
                        Margin = new Thickness(0, 0, 8, 8),
                        CornerRadius = new CornerRadius(10),
                        Background = on ? Look.Brush(Look.VioletDeep) : Look.Brush(Look.White, 0.05),
                        BorderBrush = on ? Look.Brush(Look.Violet) : Look.Brush(Look.White, 0.1),
                        BorderThickness = new Thickness(1),
                        Child = Ui.Text(p.Title, 13, FontWeight.SemiBold).Center().Ellipsis(),
                    };
                    var id = p.Id;
                    b.Click += () =>
                    {
                        preset = id;
                        value.Text = id;
                        Build();
                    };
                    grid.Children.Add(b);
                }
                presetsHost.Child = Ui.V(8, grid, Ui.Text("Или свои теги через запятую, например geosite-twitch", 12, fg: Look.Brush(Look.Muted)));
            }
            var options = vpn.Profile.Servers.Select(s => (s.Id, s.Code, Look.Hex(s.ColorHex))).Append((Target.Direct, "DIRECT", Look.DirectGray)).ToList();
            targetHost.Child = Dialogs.Field("Куда направить", new RouteSegment(options, target, t => { target = t; Build(); }));
        }

        void Add()
        {
            var v = (value.Text ?? "").Trim();
            string? why = v.Length == 0 ? "Заполни поле"
                : kind == DomainRuleKind.Domain && ConfigBuilder.NormalizeDomain(v).Length == 0 ? "Это не похоже на домен"
                : kind == DomainRuleKind.Ip && !v.Contains('.') && !v.Contains(':') ? "Нужен IP-адрес или подсеть"
                : null;
            if (why != null)
            {
                error.Text = why;
                error.IsVisible = true;
                return;
            }
            vpn.Update(p => p.DomainRules.Add(new DomainRule(kind, v, target)));
            win.CloseSheet();
        }

        value.TextChanged += (_, _) => error.IsVisible = false;
        value.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Add(); };
        Build();
        body.Children.AddRange([
            Ui.Display("Новое правило по домену", 20),
            kindHost, presetsHost, value, error, targetHost,
            Ui.H(10, new NovaButton("Отмена", win.CloseSheet), new NovaButton("Добавить", Add, prominent: true)).Right(),
        ]);
        win.ShowSheet(body, 560);
        Dispatcher.UIThread.Post(() => value.Focus(), DispatcherPriority.Loaded);
    }
}

// MARK: - App picker

public static class AppPickerSheet
{
    public static void Show(Control owner, VpnController vpn)
    {
        var win = MainWindow.Of(owner);
        if (win == null) return;
        var apps = new List<WinIntegration.InstalledApp>();
        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var target = vpn.Profile.Servers.FirstOrDefault()?.Id ?? Target.Direct;
        var query = Ui.TextField("Поиск");
        var list = new StackPanel { Spacing = 6 };
        var status = Ui.Text("Ничего не выбрано", 13, fg: Look.Brush(Look.Muted));
        var targetHost = new Border();
        NovaButton? addButton = null;

        void BuildList()
        {
            list.Children.Clear();
            var existing = new HashSet<string>(vpn.Profile.AppRules.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var q = (query.Text ?? "").Trim();
            var shown = apps.Where(a => !existing.Contains(a.Path) && (q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
            if (shown.Count == 0)
                list.Children.Add(Ui.Text(apps.Count == 0 ? "Ищу программы…" : "Ничего не нашлось — выбери файл .exe вручную", 13, fg: Look.Brush(Look.Muted)).Margin(4, 8, 0, 0));
            foreach (var a in shown)
            {
                var on = picked.Contains(a.Path);
                var row = new Pressable
                {
                    Padding = new Thickness(12, 8),
                    CornerRadius = new CornerRadius(12),
                    Background = on ? Look.Brush(Look.Violet, 0.14) : Look.Brush(Look.White, 0.03),
                    Child = Ui.Cols("Auto,*,Auto,Auto",
                        RulesPage.AppIcon(a.Path, 30),
                        Ui.V(1, Ui.Text(a.Name, 14, FontWeight.SemiBold).Ellipsis(), Ui.Text(a.Path, 11, fg: Look.Brush(Look.Muted)).Ellipsis()).Margin(12, 0, 8, 0),
                        a.Running ? Ui.Text("запущено", 11, FontWeight.Bold, Look.Brush(Look.Mint)).Margin(0, 0, 10, 0) : null,
                        Icons.Make(on ? Icons.CheckCircle : Icons.CircleEmpty, 18, Look.Brush(on ? Look.Violet : Look.Muted))),
                };
                var path = a.Path;
                row.Click += () =>
                {
                    if (!picked.Remove(path)) picked.Add(path);
                    Sync();
                };
                list.Children.Add(row);
            }
        }

        void Sync()
        {
            status.Text = picked.Count == 0 ? "Ничего не выбрано" : $"Выбрано: {picked.Count}";
            if (addButton != null) addButton.IsEnabled = picked.Count > 0;
            BuildList();
        }

        void BuildTarget()
        {
            var options = vpn.Profile.Servers.Select(s => (s.Id, s.Code, Look.Hex(s.ColorHex))).Append((Target.Direct, "DIRECT", Look.DirectGray)).ToList();
            targetHost.Child = Dialogs.Field("Куда направить", new RouteSegment(options, target, t => { target = t; BuildTarget(); }));
        }

        async void ChooseFile()
        {
            var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выбери программу",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Программы") { Patterns = ["*.exe"] }],
                SuggestedStartLocation = await win.StorageProvider.TryGetFolderFromPathAsync(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            });
            foreach (var f in files)
            {
                if (f.TryGetLocalPath() is not { } path) continue;
                if (apps.All(a => !a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    apps.Insert(0, new WinIntegration.InstalledApp(WinIntegration.DescriptionOf(path), path, false));
                picked.Add(path);
            }
            Sync();
        }

        void Add()
        {
            var chosen = apps.Where(a => picked.Contains(a.Path)).ToList();
            vpn.Update(p => { foreach (var a in chosen) p.AppRules.Add(new AppRule(a.Name, a.Path, target)); });
            win.CloseSheet();
        }

        addButton = new NovaButton("Добавить", Add, prominent: true) { IsEnabled = false };
        query.TextChanged += (_, _) => BuildList();
        BuildTarget();
        BuildList();
        win.ShowSheet(Ui.V(16,
            Ui.Cols("*,Auto", Ui.Display("Добавить программы", 20), new NovaButton("Выбрать файл…", ChooseFile)),
            query,
            new ScrollViewer { Content = list, Height = 360 },
            targetHost,
            Ui.Cols("*,Auto", status, Ui.H(10, new NovaButton("Отмена", win.CloseSheet), addButton))), 600);

        // Start menu + running programs: off the UI thread so the sheet opens instantly.
        Task.Run(WinIntegration.ScanApps).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                foreach (var a in t.Result)
                    if (apps.All(x => !x.Path.Equals(a.Path, StringComparison.OrdinalIgnoreCase))) apps.Add(a);
            if (apps.Count == 0 && !OperatingSystem.IsWindows())
                apps.Add(new WinIntegration.InstalledApp("Telegram (пример)", @"C:\Users\alex\AppData\Roaming\Telegram Desktop\Telegram.exe", true));
            BuildList();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
