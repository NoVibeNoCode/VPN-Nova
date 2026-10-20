using Avalonia;
using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Nova.Core;

namespace Nova.App;

/// Small builders so views read like the SwiftUI originals.
public static class Ui
{
    public static TextBlock Text(string text, double size, FontWeight weight = FontWeight.Normal, IBrush? fg = null, FontFamily? font = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        FontFamily = font ?? Look.BodyFont,
        Foreground = fg ?? Look.Brush(Look.Text),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Display(string text, double size, FontWeight weight = FontWeight.Bold, IBrush? fg = null) =>
        Text(text, size, weight, fg, Look.DisplayFont);

    public static TextBlock Mono(string text, double size, IBrush? fg = null) => Text(text, size, FontWeight.Medium, fg, Look.MonoFont);

    public static TextBlock Wrap(this TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    public static TextBlock Ellipsis(this TextBlock t)
    {
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.TextWrapping = TextWrapping.NoWrap;
        return t;
    }

    public static StackPanel H(double spacing, params Control?[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) if (c != null) s.Children.Add(c);
        return s;
    }

    public static StackPanel V(double spacing, params Control?[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) if (c != null) s.Children.Add(c);
        return s;
    }

    /// A one-row grid: `Cols("Auto,*,Auto", a, b, c)`; null children leave their column empty.
    public static Grid Cols(string defs, params Control?[] children)
    {
        var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(defs) };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is not { } c) continue;
            Grid.SetColumn(c, i);
            g.Children.Add(c);
        }
        return g;
    }

    public static Grid Rows(string defs, params Control?[] children)
    {
        var g = new Grid { RowDefinitions = RowDefinitions.Parse(defs) };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is not { } c) continue;
            Grid.SetRow(c, i);
            g.Children.Add(c);
        }
        return g;
    }

    public static Border Card(Control content, double padding = 16, double radius = 16) => new()
    {
        Child = content,
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(radius),
        Background = Look.Brush(Look.Panel),
        BorderBrush = Look.LineBrush,
        BorderThickness = new Thickness(1),
    };

    public static Border Box(Control content, Color fill, double fillOpacity, Color stroke, double strokeOpacity, double radius, Thickness padding) => new()
    {
        Child = content,
        Padding = padding,
        CornerRadius = new CornerRadius(radius),
        Background = Look.Brush(fill, fillOpacity),
        BorderBrush = Look.Brush(stroke, strokeOpacity),
        BorderThickness = new Thickness(1),
    };

    public static IBrush Gradient(Color a, Color b, bool diagonal = false) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = diagonal ? new RelativePoint(1, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    public static BoxShadows Glow(Color c, double opacity, double blur, double y = 0) =>
        new(new BoxShadow { Color = c.WithAlpha(opacity), Blur = blur, OffsetY = y });

    /// Icon + text, like SwiftUI's Label.
    public static Control Label(string icon, string text, double size = 13) =>
        H(7, Icons.Make(icon, size + 1, Look.Brush(Look.Text)), Text(text, size, FontWeight.Bold));

    public static T Tip<T>(this T c, string tip) where T : Control
    {
        ToolTip.SetTip(c, tip);
        return c;
    }

    public static Border Banner(string text, Color color) => new()
    {
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(14),
        Background = Look.Brush(color, 0.1),
        BorderBrush = Look.Brush(color, 0.35),
        BorderThickness = new Thickness(1),
        Child = Cols("Auto,*", Icons.Make(Icons.Alert, 16, Look.Brush(color)).Margin(0, 1, 10, 0).Top(),
                     Text(text, 13, FontWeight.Medium).Wrap()),
    };

    public static T Margin<T>(this T c, double l, double t, double r, double b) where T : Control
    {
        c.Margin = new Thickness(l, t, r, b);
        return c;
    }

    public static T Top<T>(this T c) where T : Control
    {
        c.VerticalAlignment = VerticalAlignment.Top;
        return c;
    }

    public static T Right<T>(this T c) where T : Control
    {
        c.HorizontalAlignment = HorizontalAlignment.Right;
        return c;
    }

    public static T Center<T>(this T c) where T : Control
    {
        c.HorizontalAlignment = HorizontalAlignment.Center;
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }

    public static TextBox TextField(string placeholder, string text = "") => new()
    {
        Text = text,
        Watermark = placeholder,
        Classes = { "nova" },
    };

    /// Page header used by every section.
    public static Control PageHeader(string title, string subtitle, Control? trailing = null) =>
        Cols("*,Auto",
             V(6, Display(title, 28, FontWeight.ExtraBold), Text(subtitle, 14, fg: Look.Brush(Look.Muted)).Ellipsis()),
             trailing?.Right());

    public static Control StepNumber(int n, bool filled) => new Border
    {
        Width = 28,
        Height = 28,
        CornerRadius = new CornerRadius(14),
        Background = filled ? Look.Brush(Look.VioletDeep) : Look.Brush(Look.White, 0.1),
        Child = Display(n.ToString(), 12).Center(),
    };
}

/// Pressable surface: the app's button. Hover/press visuals are ours, not the Fluent theme's.
public class Pressable : Border
{
    public event Action? Click;
    bool _pressed;

    public Pressable()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        Background = Brushes.Transparent;
        RenderTransformOrigin = RelativePoint.Center;
        Transitions =
        [
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() },
        ];
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        OnHover(true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pressed = false;
        OnHover(false);
        OnPress(false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        OnPress(true);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        OnPress(false);
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) Fire();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space)
        {
            Fire();
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty) Opacity = IsEnabled ? 1 : 0.4;
    }

    void Fire()
    {
        if (IsEffectivelyEnabled) Click?.Invoke();
    }

    protected virtual void OnHover(bool hover) { }

    protected virtual void OnPress(bool pressed) =>
        RenderTransform = TransformOperations.Parse(pressed ? "scale(0.97)" : "scale(1)");
}

/// The standard button (NovaButtonStyle): glassy, or violet when prominent.
public sealed class NovaButton : Pressable
{
    readonly bool _prominent;
    readonly Color? _tint;

    public NovaButton(string text, Action onClick, bool prominent = false, string? icon = null, Color? tint = null)
        : this(icon == null ? Ui.Text(text, 13, FontWeight.Bold) : Ui.Label(icon, text), onClick, prominent, tint) { }

    /// `tint` paints a solid colored button (red for destructive confirmations).
    public NovaButton(Control content, Action onClick, bool prominent = false, Color? tint = null)
    {
        _prominent = prominent;
        _tint = tint;
        Child = content;
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(14, 0);
        MinHeight = 36;
        CornerRadius = new CornerRadius(11);
        BorderThickness = new Thickness(1);
        Click += onClick;
        Paint(false);
    }

    void Paint(bool hover)
    {
        if (_tint is { } t)
        {
            Background = Look.Brush(t, hover ? 1 : 0.85);
            BorderBrush = Look.Brush(t);
            BoxShadow = Ui.Glow(t, 0.35, 12, 5);
            return;
        }
        Background = _prominent ? Look.Brush(hover ? Look.Violet : Look.VioletDeep) : Look.Brush(Look.White, hover ? 0.09 : 0.05);
        BorderBrush = _prominent ? Look.Brush(Look.Violet) : Look.Brush(Look.White, 0.12);
        BoxShadow = _prominent ? Ui.Glow(Look.VioletDeep, 0.45, 12, 5) : default;
    }

    protected override void OnHover(bool hover) => Paint(hover);
}

/// Server code chip ("DE", "RU", "DIRECT").
public static class TargetChip
{
    public static Border Make(string label, Color color, bool chevron = false)
    {
        var content = Ui.H(5, Ui.Display(label, 11, FontWeight.Bold, Brushes.White),
                           chevron ? Icons.Make(Icons.ChevronDown, 10, Brushes.White, 3) : null);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Child = content,
            Padding = new Thickness(10, 0),
            MinWidth = 58,
            MinHeight = 30,
            CornerRadius = new CornerRadius(15),
            Background = Look.Brush(color),
            BoxShadow = Ui.Glow(color, 0.45, 9, 3),
        };
    }
}

/// Server chip with a dropdown of all targets (TargetMenu).
public sealed class TargetMenu : Pressable
{
    public TargetMenu(Profile profile, string selected, Action<string> onSelect)
    {
        Child = TargetChip.Make(Look.LabelFor(selected, profile), Look.ColorFor(selected, profile), chevron: true);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        CornerRadius = new CornerRadius(15);
        Click += () =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            foreach (var s in profile.Servers)
            {
                var id = s.Id;
                var item = new MenuItem { Header = $"{s.Code} — {s.Name}" };
                item.Click += (_, _) => onSelect(id);
                menu.Items.Add(item);
            }
            var direct = new MenuItem { Header = "DIRECT — напрямую" };
            direct.Click += (_, _) => onSelect(Target.Direct);
            menu.Items.Add(direct);
            menu.ShowAt(this);
        };
    }
}

/// The capsule toggle (NovaToggleStyle) with a springy knob.
public sealed class NovaToggle : Pressable
{
    readonly Border _knob;
    bool _on;
    public event Action<bool>? Toggled;

    public NovaToggle(bool on, Action<bool> onToggle)
    {
        Width = 46;
        Height = 26;
        CornerRadius = new CornerRadius(13);
        _knob = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0),
            Transitions =
            [
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(350), Easing = new BackEaseOut() },
            ],
        };
        Child = _knob;
        Transitions =
        [
            new BrushTransition { Property = BackgroundProperty, Duration = TimeSpan.FromMilliseconds(200) },
        ];
        Toggled += onToggle;
        Set(on);
        Click += () =>
        {
            Set(!_on);
            Toggled?.Invoke(_on);
        };
        AutomationProperties.SetName(this, "Переключатель");
    }

    public void Set(bool on)
    {
        _on = on;
        Background = on ? Look.Brush(Look.Mint) : Look.Brush(Color.Parse("#2A2E48"));
        _knob.RenderTransform = TransformOperations.Parse(on ? "translateX(20px)" : "translateX(0px)");
    }

    protected override void OnPress(bool pressed) { }
}

/// Segmented control with a springy sliding indicator (RouteSegment).
public sealed class RouteSegment : Border
{
    public RouteSegment(IReadOnlyList<(string Id, string Label, Color Color)> options, string selected, Action<string> onSelect)
    {
        var index = Math.Max(0, options.ToList().FindIndex(o => o.Id == selected));
        var color = options.Count > index ? options[index].Color : Look.DirectGray;
        Padding = new Thickness(4);
        CornerRadius = new CornerRadius(14);
        Background = Look.Brush(Look.White, 0.05);
        BorderBrush = Look.Brush(Look.White, 0.07);
        BorderThickness = new Thickness(1);
        Height = 44;

        var cols = string.Join(",", Enumerable.Repeat("*", Math.Max(options.Count, 1)));
        var indicator = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Look.Brush(color),
            BoxShadow = Ui.Glow(color, 0.5, 10, 4),
        };
        var labels = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(cols) };
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var item = new Pressable
            {
                Child = Ui.Text(o.Label, 13, FontWeight.Bold, Look.Brush(o.Id == selected ? Look.White : Look.Muted)).Center().Ellipsis(),
                CornerRadius = new CornerRadius(10),
            };
            item.Click += () => onSelect(o.Id);
            Grid.SetColumn(item, i);
            labels.Children.Add(item);
        }
        // The indicator lives in a same-shaped grid; a transform slides it from the previous spot.
        var track = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(cols) };
        Grid.SetColumn(indicator, index);
        track.Children.Add(indicator);
        Child = new Panel { Children = { track, labels } };

        if (LastIndex.TryGetValue(Key(options), out var prev) && prev != index)
        {
            // Start where the old indicator was (one column = its own width), then spring into place.
            // TransformOperations only takes px, so the offset waits for the first layout.
            void Slide(object? sender, SizeChangedEventArgs e)
            {
                indicator.SizeChanged -= Slide;
                indicator.RenderTransform = TransformOperations.Parse($"translateX({(prev - index) * e.NewSize.Width:0.##}px)".Replace(',', '.'));
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    indicator.Transitions =
                    [
                        // A small spring overshoot (BackEaseOut flew past the control's edge).
                        new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(420), Easing = new SplineEasing(0.3, 1.12, 0.45, 1.0) },
                    ];
                    indicator.RenderTransform = TransformOperations.Parse("translateX(0px)");
                }, Avalonia.Threading.DispatcherPriority.Render);
            }
            indicator.SizeChanged += Slide;
        }
        LastIndex[Key(options)] = index;
    }

    // Views are rebuilt on every change; remember where the indicator was so it can slide.
    static readonly Dictionary<string, int> LastIndex = [];
    static string Key(IReadOnlyList<(string Id, string Label, Color Color)> o) => string.Join("|", o.Select(x => x.Id));
}
