using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Nova.App;

/// Line icons on a 24×24 grid, drawn as strokes (stand-ins for the SF Symbols of the macOS app).
public static class Icons
{
    const string Circle = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z";
    const string ShieldPath = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z";

    public const string Power = "M12 2v10 M18.36 6.64a9 9 0 1 1-12.73 0";
    public const string Shield = ShieldPath;
    public const string ShieldCheck = ShieldPath + " M9 12l2 2 4-4";
    public const string ShieldAlert = ShieldPath + " M12 8v4 M12 16h.01";
    public const string ShieldX = ShieldPath + " M9.5 9.5l5 5 M14.5 9.5l-5 5";
    public const string ShieldHalf = ShieldPath + " M12 2v20";
    public const string Routes = "M6 3v12 M18 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M18 9a9 9 0 0 1-9 9";
    public const string List = "M8 6h13 M8 12h13 M8 18h13 M3 6h.01 M3 12h.01 M3 18h.01";
    public const string Server = "M4 3h16a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M4 14h16a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-5a1 1 0 0 1 1-1z M7 6.5h.01 M7 17.5h.01";
    public const string Gear = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z";
    public const string Doc = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M16 13H8 M16 17H8 M10 9H8";
    public const string Plus = "M12 5v14 M5 12h14";
    public const string PlusCircle = Circle + " M12 8v8 M8 12h8";
    public const string Globe = Circle + " M2 12h20 M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z";
    public const string Grid = "M3 3h7v7H3z M14 3h7v7h-7z M14 14h7v7h-7z M3 14h7v7H3z";
    public const string ChevronRight = "M9 18l6-6-6-6";
    public const string ChevronDown = "M6 9l6 6 6-6";
    public const string Pause = Circle + " M10 15V9 M14 15V9";
    public const string Play = Circle + " M10 8l6 4-6 4V8z";
    public const string X = "M18 6L6 18 M6 6l12 12";
    public const string Type = "M4 7V4h16v3 M9 20h6 M12 4v16";
    public const string Hash = "M4 9h16 M4 15h16 M10 3L8 21 M16 3l-2 18";
    public const string Layers = "M12 2L2 7l10 5 10-5-10-5z M2 17l10 5 10-5 M2 12l10 5 10-5";
    public const string Link = "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71";
    public const string Pencil = "M17 3a2.83 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z";
    public const string Trash = "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2";
    public const string Folder = "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z";
    public const string Refresh = "M23 4v6h-6 M1 20v-6h6 M3.51 9a9 9 0 0 1 14.85-3.36L23 10 M1 14l5.64 5.36A9 9 0 0 0 20.49 15";
    public const string Copy = "M20 9h-9a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2v-9a2 2 0 0 0-2-2z M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1";
    public const string External = "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6 M15 3h6v6 M10 14L21 3";
    public const string Alert = "M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z M12 9v4 M12 17h.01";
    public const string CircleEmpty = Circle;
    public const string CheckCircle = Circle + " M8.5 12l2.5 2.5 4.5-5";
    public const string Network = "M18 8a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M6 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M18 22a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M8.59 13.51l6.83 3.98 M15.41 6.51l-6.82 3.98";
    public const string App = "M4 4h16v16H4z M4 9h16 M7 6.5h.01 M10 6.5h.01";

    static readonly Dictionary<string, Geometry> Cache = [];

    public static Geometry Geometry(string data)
    {
        if (!Cache.TryGetValue(data, out var g)) Cache[data] = g = StreamGeometry.Parse(data);
        return g;
    }

    /// An icon control: stroked path in a size×size box.
    public static Control Make(string data, double size, IBrush brush, double stroke = 2)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry(data),
            Stroke = brush,
            StrokeThickness = stroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            Width = 24,
            Height = 24,
        };
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } }, IsHitTestVisible = false };
    }
}
