using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Nova.Core;

namespace Nova.App;

public sealed partial class LogsPage : Page
{
    readonly TextBox _filter = Ui.TextField("Фильтр");
    readonly CheckBox _follow = new() { Content = "Следить за концом", IsChecked = true };
    readonly SelectableTextBlock _text = new() { FontFamily = Look.MonoFont, FontSize = 11.5, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    readonly ScrollViewer _scroll;
    readonly TextBlock _empty = Ui.Text("Лог пуст — ядро ещё не запускалось.", 13, fg: Look.Brush(Look.Muted));
    readonly LogTail _log = new(SystemPaths.LogFile);
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    int _shown = -1;

    public LogsPage(VpnController vpn) : base(vpn)
    {
        _filter.Width = 280;
        _scroll = new ScrollViewer { Content = _text };
        _filter.TextChanged += (_, _) => ShowLines(force: true);
        _timer.Tick += (_, _) => Poll();
        var copy = new NovaButton("Копировать", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(string.Join("\n", _log.Visible(_filter.Text ?? "")));
        }, icon: Icons.Copy);
        var open = new NovaButton("Открыть", () => WinIntegration.Open(SystemPaths.LogFile), icon: Icons.External);
        var box = new Border
        {
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Background = Look.Brush(Colors.Black, 0.35),
            BorderBrush = Look.LineBrush,
            BorderThickness = new Thickness(1),
            Child = _scroll,
        };
        Child = Ui.Rows("Auto,Auto,*,Auto",
            Ui.PageHeader("Логи", SystemPaths.LogFile).Margin(0, 0, 0, 16),
            Ui.Cols("Auto,Auto,*,Auto,Auto", _filter, _follow.Margin(10, 0, 0, 0), null, copy, open.Margin(10, 0, 0, 0)).Margin(0, 0, 0, 16),
            box,
            _empty.Margin(0, 12, 0, 0));
        if (vpn.IsDemo) _log.Demo();
    }

    protected override string Signature() => "";
    protected override void Render() { }

    public override void SetActive(bool active)
    {
        base.SetActive(active);
        if (active)
        {
            Poll();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    void Poll()
    {
        if (TopLevel.GetTopLevel(this) is Window { IsVisible: false } or Window { WindowState: WindowState.Minimized }) return;
        _log.Poll();
        ShowLines();
    }

    void ShowLines(bool force = false)
    {
        if (!force && _shown == _log.Version) return;
        _shown = _log.Version;
        var lines = _log.Visible(_filter.Text ?? "");
        _empty.IsVisible = _log.Lines.Count == 0;
        // One text block with colored runs instead of hundreds of row controls.
        var inlines = new InlineCollection();
        foreach (var line in lines)
        {
            var color = line.Contains("ERROR") || line.Contains("FATAL") ? Look.Danger : line.Contains("WARN") ? Look.Amber : Look.Soft;
            inlines.Add(new Run(line + "\n") { Foreground = Look.Brush(color) });
        }
        _text.Inlines = inlines;
        if (_follow.IsChecked == true) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}

/// Reads only the bytes appended since the last poll; keeps the last 1500 lines.
public sealed partial class LogTail(string path)
{
    public List<string> Lines { get; private set; } = [];
    public int Version { get; private set; }
    long _offset;
    string _partial = "";
    const int Keep = 1500;

    [GeneratedRegex("\u001B\\[[0-9;]*m")]
    private static partial Regex Ansi();

    public List<string> Visible(string filter) =>
        filter.Length == 0 ? Lines : Lines.Where(l => l.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

    public void Poll()
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        using (fs)
        {
            var size = fs.Length;
            if (size < _offset)
            {
                // Rotated or truncated.
                _offset = 0;
                _partial = "";
                Lines = [];
            }
            if (size <= _offset) return;
            // First read: only the tail.
            if (_offset == 0 && size > 300_000) _offset = size - 300_000;
            fs.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[size - _offset];
            var read = fs.Read(buf, 0, buf.Length);
            _offset += read;
            var text = _partial + Encoding.UTF8.GetString(buf, 0, read);
            _partial = "";
            if (!text.EndsWith('\n') && text.LastIndexOf('\n') is var nl and >= 0)
            {
                _partial = text[(nl + 1)..];
                text = text[..nl];
            }
            var fresh = Ansi().Replace(text, "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            if (fresh.Count == 0) return;
            Lines = Lines.Concat(fresh).TakeLast(Keep).ToList();
            Version++;
        }
    }

    public void Demo()
    {
        Lines =
        [
            "+0300 2026-10-19 14:02:11 INFO network: updated default interface Ethernet, index 12",
            "+0300 2026-10-19 14:02:11 INFO inbound/tun[tun-in]: started at Nova",
            "+0300 2026-10-19 14:02:11 INFO sing-box started (0.41s)",
            "+0300 2026-10-19 14:02:14 INFO [3121 0ms] inbound/tun[tun-in]: inbound connection to www.youtube.com:443",
            "+0300 2026-10-19 14:02:14 INFO [3121 0ms] outbound/vless[srv-de]: outbound connection to www.youtube.com:443",
            "+0300 2026-10-19 14:05:40 WARN [3302 5.0s] outbound/vless[srv-de]: dial tcp 203.0.113.10:443: i/o timeout",
            "+0300 2026-10-19 14:05:52 ERROR [3315 0ms] connection: open outbound connection: proxy server unreachable",
        ];
        Version = 1;
    }
}
