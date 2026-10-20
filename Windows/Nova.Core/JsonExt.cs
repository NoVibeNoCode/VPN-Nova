using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nova.Core;

/// Small accessors over System.Text.Json nodes, in the spirit of the macOS `JSONValue`.
public static class JsonExt
{
    public static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? Str(this JsonNode? node, string key) => node?[key].StringValue();

    public static string? StringValue(this JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// Integer from a number or a numeric string (vmess links carry ports both ways).
    public static int? IntValue(this JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
                if (v.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
                return null;
            case JsonValueKind.String:
                return Ints.Parse(v.GetValue<string>());
            default:
                return null;
        }
    }

    public static bool IsTrue(this JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    public static string Pretty(this JsonNode? node) => node?.ToJsonString(PrettyOptions) ?? "{}";

    public static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    /// Strings of an array node (non-strings skipped).
    public static List<string> StringList(this JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x.StringValue()).Where(s => s != null).Select(s => s!).ToList() : [];
}

public static class Ints
{
    /// Like Swift's `Int(String)`: optional sign and ASCII digits only, nothing else.
    public static int? Parse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var start = s[0] is '+' or '-' ? 1 : 0;
        if (start == s.Length) return null;
        for (var i = start; i < s.Length; i++) if (s[i] is < '0' or > '9') return null;
        return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
