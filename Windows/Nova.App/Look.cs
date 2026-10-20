using System.Globalization;
using Avalonia.Media;
using Nova.Core;

namespace Nova.App;

/// Colors, fonts and small formatting helpers (same palette as the macOS app).
public static class Look
{
    public static readonly Color Bg = Color.Parse("#07080F");
    public static readonly Color BgDeep = Color.Parse("#05060C");
    public static readonly Color Panel = Color.Parse("#0F1120");
    public static readonly Color Panel2 = Color.Parse("#161A2E");
    public static readonly Color Text = Color.Parse("#EEF0FF");
    public static readonly Color Muted = Color.Parse("#8A90B2");
    public static readonly Color Soft = Color.Parse("#B8BDD9");
    public static readonly Color Violet = Color.Parse("#8B7CFF");
    public static readonly Color VioletDeep = Color.Parse("#6A5AF0");
    public static readonly Color Coral = Color.Parse("#FF7A59");
    public static readonly Color Mint = Color.Parse("#3BE8B0");
    public static readonly Color Amber = Color.Parse("#FFC14D");
    public static readonly Color Danger = Color.Parse("#FF5C6C");
    public static readonly Color DirectGray = Color.Parse("#3A3F5C");
    public static readonly Color White = Colors.White;

    public static IBrush Brush(Color c, double opacity = 1) => new SolidColorBrush(c, opacity);
    public static IBrush LineBrush => Brush(White, 0.08);

    public static readonly FontFamily DisplayFont = new("fonts:Nova#Unbounded");
    public static readonly FontFamily BodyFont = new("fonts:Nova#Manrope");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, monospace");

    public static Color Hex(string hex) => Color.TryParse(hex.Trim(), out var c) ? c : Color.Parse("#888888");

    public static Color ColorFor(string target, Profile profile) =>
        profile.Server(target) is { } s ? Hex(s.ColorHex) : DirectGray;

    public static string LabelFor(string target, Profile profile) => profile.Server(target)?.Code ?? "DIRECT";

    public static Color WithAlpha(this Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    public static (string Value, string Unit) FormatSpeed(long bytesPerSec)
    {
        double b = bytesPerSec;
        if (b >= 1_000_000) return ((b / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ','), "МБ/с");
        if (b >= 1_000) return (((long)(b / 1_000)).ToString(CultureInfo.InvariantCulture), "КБ/с");
        return (bytesPerSec.ToString(CultureInfo.InvariantCulture), "Б/с");
    }

    /// Russian plural: Plural(1, "соединение", "соединения", "соединений") → "1 соединение".
    public static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return $"{n} {one}";
        if (m10 is >= 2 and <= 4 && m100 is not (>= 12 and <= 14)) return $"{n} {few}";
        return $"{n} {many}";
    }
}
