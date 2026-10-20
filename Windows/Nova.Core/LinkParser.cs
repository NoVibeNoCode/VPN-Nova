using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nova.Core;

/// Parses share links (`vless://`, `vmess://`, `trojan://`, `ss://`, `hy2://`, `tuic://`)
/// into sing-box outbound objects.
public static class LinkParser
{
    public sealed record Parsed(string Name, JsonObject Outbound);

    public sealed class ParseException(string message) : Exception(message)
    {
        public static ParseException UnsupportedScheme(string s) => new($"Схема «{s}» не поддерживается. Добавь сервер через JSON.");
        public static ParseException Malformed(string m) => new($"Не получилось разобрать ссылку: {m}");
        public static ParseException UnsupportedTransport(string t) => new($"Транспорт «{t}» не поддерживается sing-box.");
    }

    public static Parsed Parse(string raw)
    {
        var link = raw.Trim();
        var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) throw ParseException.Malformed("нет схемы");
        var scheme = link[..schemeEnd].ToLowerInvariant();
        return scheme switch
        {
            "vless" => ParseVlessLike(link, "vless"),
            "trojan" => ParseVlessLike(link, "trojan"),
            "vmess" => ParseVMess(link),
            "ss" => ParseShadowsocks(link),
            "hysteria2" or "hy2" => ParseHysteria2(link),
            "tuic" => ParseTuic(link),
            _ => throw ParseException.UnsupportedScheme(scheme),
        };
    }

    // MARK: - Helpers

    sealed record LinkParts(string User, string Host, int Port, Dictionary<string, string> Query, string Fragment);

    /// Percent-decoding that, like Swift's `removingPercentEncoding`, gives null on broken escapes.
    static string? PercentDecode(string s)
    {
        if (!s.Contains('%')) return s;
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '%')
            {
                if (i + 2 >= s.Length || !Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2])) return null;
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
        }
        try { return StrictUtf8.GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return null; }
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static LinkParts Split(string link)
    {
        // Uri chokes on some userinfo characters, so split by hand.
        var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) throw ParseException.Malformed("нет схемы");
        var rest = link[(schemeEnd + 3)..];
        var fragment = "";
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            fragment = PercentDecode(rest[(hash + 1)..]) ?? "";
            rest = rest[..hash];
        }
        var query = new Dictionary<string, string>();
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var k = eq >= 0 ? pair[..eq] : pair;
                var v = eq >= 0 ? pair[(eq + 1)..] : "";
                var key = PercentDecode(k) ?? k;
                query[key] = eq >= 0 ? PercentDecode(v.Replace('+', ' ')) ?? v : "";
            }
            rest = rest[..q];
        }
        if (rest.EndsWith('/')) rest = rest[..^1];
        var user = "";
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            user = PercentDecode(rest[..at]) ?? rest[..at];
            rest = rest[(at + 1)..];
        }
        var (host, port) = HostPort(rest);
        return new LinkParts(user, host, port, query, fragment);
    }

    static (string, int) HostPort(string s)
    {
        if (s.StartsWith('[') && s.IndexOf(']') is var close and > 0)
        {
            var host = s[1..close];
            var after = s[(close + 1)..];
            if (!after.StartsWith(':') || Ints.Parse(after[1..]) is not { } p6) throw ParseException.Malformed("нет порта");
            return (host, p6);
        }
        var colon = s.LastIndexOf(':');
        if (colon < 0 || Ints.Parse(s[(colon + 1)..]) is not { } port) throw ParseException.Malformed("нет порта");
        return (s[..colon], port);
    }

    internal static string? Base64Decode(string s)
    {
        var t = s.Trim().Replace('-', '+').Replace('_', '/');
        while (t.Length % 4 != 0) t += "=";
        if (t.Any(char.IsWhiteSpace)) return null;
        try { return StrictUtf8.GetString(Convert.FromBase64String(t)); }
        catch (Exception e) when (e is FormatException or DecoderFallbackException) { return null; }
    }

    static JsonObject Tls(string? serverName, bool insecure = false, string? alpn = null,
                          string? fingerprint = null, (string Key, string ShortId)? reality = null)
    {
        var t = new JsonObject { ["enabled"] = true };
        if (!string.IsNullOrEmpty(serverName)) t["server_name"] = serverName;
        if (insecure) t["insecure"] = true;
        if (!string.IsNullOrEmpty(alpn)) t["alpn"] = JsonExt.Strings(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var fp = !string.IsNullOrEmpty(fingerprint) ? fingerprint : reality != null ? "chrome" : null;
        if (fp != null) t["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
        if (reality is { } r)
            t["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = r.Key, ["short_id"] = r.ShortId };
        return t;
    }

    static JsonObject? Transport(string? type, string? host, string? path, string? serviceName)
    {
        switch ((type ?? "tcp").ToLowerInvariant())
        {
            case "" or "tcp" or "raw" or "none":
                return null;
            case "ws" or "websocket":
            {
                var t = new JsonObject { ["type"] = "ws" };
                if (!string.IsNullOrEmpty(path)) t["path"] = path;
                if (!string.IsNullOrEmpty(host)) t["headers"] = new JsonObject { ["Host"] = host };
                return t;
            }
            case "grpc":
            {
                var t = new JsonObject { ["type"] = "grpc" };
                if (!string.IsNullOrEmpty(serviceName)) t["service_name"] = serviceName;
                return t;
            }
            case "http" or "h2":
            {
                var t = new JsonObject { ["type"] = "http" };
                if (!string.IsNullOrEmpty(host)) t["host"] = JsonExt.Strings(host.Split(',', StringSplitOptions.RemoveEmptyEntries));
                if (!string.IsNullOrEmpty(path)) t["path"] = path;
                return t;
            }
            case "httpupgrade":
            {
                var t = new JsonObject { ["type"] = "httpupgrade" };
                if (!string.IsNullOrEmpty(host)) t["host"] = host;
                if (!string.IsNullOrEmpty(path)) t["path"] = path;
                return t;
            }
            case var other:
                throw ParseException.UnsupportedTransport(other);
        }
    }

    static string? Get(this Dictionary<string, string> q, string k) => q.TryGetValue(k, out var v) ? v : null;

    // MARK: - Schemes

    static Parsed ParseVlessLike(string link, string type)
    {
        var p = Split(link);
        if (p.User.Length == 0) throw ParseException.Malformed(type == "vless" ? "нет UUID" : "нет пароля");
        var q = p.Query;
        var ob = new JsonObject { ["type"] = type, ["server"] = p.Host, ["server_port"] = p.Port };
        if (type == "vless")
        {
            ob["uuid"] = p.User;
            if (q.Get("flow") is { Length: > 0 } flow) ob["flow"] = flow;
            ob["packet_encoding"] = "xudp";
        }
        else
        {
            ob["password"] = p.User;
        }
        var security = (q.Get("security") ?? (type == "trojan" ? "tls" : "none")).ToLowerInvariant();
        var sni = q.Get("sni") ?? q.Get("peer") ?? q.Get("host");
        var insecure = q.Get("allowInsecure") == "1" || q.Get("insecure") == "1";
        switch (security)
        {
            case "reality":
                if (q.Get("pbk") is not { Length: > 0 } pbk) throw ParseException.Malformed("нет pbk для Reality");
                ob["tls"] = Tls(sni, fingerprint: q.Get("fp"), reality: (pbk, q.Get("sid") ?? ""));
                break;
            case "tls" or "xtls":
                ob["tls"] = Tls(sni, insecure, q.Get("alpn"), q.Get("fp"));
                break;
        }
        if (Transport(q.Get("type"), q.Get("host"), q.Get("path"), q.Get("serviceName")) is { } tr) ob["transport"] = tr;
        return new Parsed(p.Fragment.Length == 0 ? p.Host : p.Fragment, ob);
    }

    static Parsed ParseVMess(string link)
    {
        var body = link["vmess://".Length..];
        JsonObject? v = null;
        if (Base64Decode(body) is { } json)
        {
            try { v = JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { }
        }
        if (v == null) throw ParseException.Malformed("ожидался base64 JSON");
        string? Str(string k) => v[k] switch
        {
            JsonValue x when x.GetValueKind() == JsonValueKind.String => x.GetValue<string>(),
            JsonValue x when x.GetValueKind() == JsonValueKind.Number && x.TryGetValue<long>(out var n) => n.ToString(),
            _ => null,
        };
        if (Str("add") is not { } host || v["port"].IntValue() is not { } port || Str("id") is not { } id)
            throw ParseException.Malformed("нет адреса, порта или id");
        var ob = new JsonObject
        {
            ["type"] = "vmess", ["server"] = host, ["server_port"] = port, ["uuid"] = id,
            ["security"] = Str("scy") ?? "auto", ["alter_id"] = v["aid"].IntValue() ?? 0,
        };
        if ((Str("tls") ?? "").Equals("tls", StringComparison.OrdinalIgnoreCase))
            ob["tls"] = Tls(Str("sni") ?? Str("host"), alpn: Str("alpn"), fingerprint: Str("fp"));
        if (Transport(Str("net"), Str("host"), Str("path"), Str("path")) is { } tr) ob["transport"] = tr;
        return new Parsed(Str("ps") ?? host, ob);
    }

    static Parsed ParseShadowsocks(string link)
    {
        var body = link["ss://".Length..];
        var name = "";
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            name = PercentDecode(body[(hash + 1)..]) ?? "";
            body = body[..hash];
        }
        string? plugin = null;
        var q = body.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in body[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == "plugin") plugin = PercentDecode(pair[(eq + 1)..]) ?? pair[(eq + 1)..];
            }
            body = body[..q];
        }
        if (body.EndsWith('/')) body = body[..^1];

        string method, password, hostPortPart;
        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: userinfo is base64(method:password) or percent-encoded plain text.
            var userinfo = body[..at];
            hostPortPart = body[(at + 1)..];
            var b64 = Base64Decode(userinfo);
            var decoded = b64 != null && b64.Contains(':') ? b64 : PercentDecode(userinfo) ?? userinfo;
            var colon = decoded.IndexOf(':');
            if (colon < 0) throw ParseException.Malformed("нет метода шифрования");
            method = decoded[..colon];
            password = decoded[(colon + 1)..];
        }
        else
        {
            // Legacy: base64(method:password@host:port)
            var decoded = Base64Decode(body);
            var at2 = decoded?.LastIndexOf('@') ?? -1;
            var colon = at2 > 0 ? decoded![..at2].IndexOf(':') : -1;
            if (decoded == null || at2 < 0 || colon < 0) throw ParseException.Malformed("ожидался base64");
            method = decoded[..colon];
            password = decoded[(colon + 1)..at2];
            hostPortPart = decoded[(at2 + 1)..];
        }
        var (host, port) = HostPort(hostPortPart);
        var ob = new JsonObject
        {
            ["type"] = "shadowsocks", ["server"] = host, ["server_port"] = port,
            ["method"] = method, ["password"] = password,
        };
        // SIP003: `plugin=obfs-local;obfs=http;obfs-host=x` → plugin + plugin_opts.
        if (!string.IsNullOrEmpty(plugin))
        {
            var semi = plugin.IndexOf(';');
            var first = semi >= 0 ? plugin[..semi] : plugin;
            var pluginName = first == "simple-obfs" ? "obfs-local" : first;
            if (pluginName is not ("obfs-local" or "v2ray-plugin"))
                throw ParseException.Malformed($"плагин «{first}» не поддерживается sing-box");
            ob["plugin"] = pluginName;
            if (semi >= 0) ob["plugin_opts"] = plugin[(semi + 1)..];
        }
        return new Parsed(name.Length == 0 ? host : name, ob);
    }

    static Parsed ParseHysteria2(string link)
    {
        // Port is optional (443) and may be a hopping list: `host:443,20000-30000`.
        var hopping = new List<string>();
        var normalized = link;
        var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var rest = link[(schemeEnd + 3)..];
            var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
            if (authorityEnd < 0) authorityEnd = rest.Length;
            var authority = rest[..authorityEnd];
            var at = authority.LastIndexOf('@') is var a and >= 0 ? a + 1 : 0;
            var hostPart = authority[at..];
            var closing = hostPart.LastIndexOf(']');
            var colon = hostPart.LastIndexOf(':');
            if (colon >= 0 && (closing >= 0 ? colon > closing : !hostPart.StartsWith('[')))
            {
                var spec = hostPart[(colon + 1)..];
                if (Ints.Parse(spec) == null)
                {
                    int? first = null;
                    foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var r = item.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Ints.Parse).Where(x => x != null).Select(x => x!.Value).ToList();
                        if (r.Count == 1) first ??= r[0];
                        else if (r.Count == 2) { hopping.Add($"{r[0]}:{r[1]}"); first ??= r[0]; }
                        else throw ParseException.Malformed($"порты «{spec}»");
                    }
                    hostPart = hostPart[..colon] + $":{first ?? 443}";
                }
            }
            else
            {
                hostPart += ":443";
            }
            normalized = link[..(schemeEnd + 3)] + authority[..at] + hostPart + rest[authorityEnd..];
        }
        var p = Split(normalized);
        var q = p.Query;
        var ob = new JsonObject
        {
            ["type"] = "hysteria2", ["server"] = p.Host, ["server_port"] = p.Port, ["password"] = p.User,
        };
        if (hopping.Count > 0) ob["server_ports"] = JsonExt.Strings(hopping);
        if (q.Get("obfs") is { Length: > 0 } obfs)
            ob["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = q.Get("obfs-password") ?? "" };
        ob["tls"] = Tls(q.Get("sni") ?? p.Host, q.Get("insecure") == "1", q.Get("alpn"));
        return new Parsed(p.Fragment.Length == 0 ? p.Host : p.Fragment, ob);
    }

    static Parsed ParseTuic(string link)
    {
        var p = Split(link);
        var q = p.Query;
        var colon = p.User.IndexOf(':');
        if (colon <= 0 || colon == p.User.Length - 1) throw ParseException.Malformed("ожидалось uuid:пароль");
        var ob = new JsonObject
        {
            ["type"] = "tuic", ["server"] = p.Host, ["server_port"] = p.Port,
            ["uuid"] = p.User[..colon], ["password"] = p.User[(colon + 1)..],
        };
        if (q.Get("congestion_control") is { Length: > 0 } cc) ob["congestion_control"] = cc;
        ob["tls"] = Tls(q.Get("sni") ?? p.Host, q.Get("allow_insecure") == "1" || q.Get("insecure") == "1", q.Get("alpn") ?? "h3");
        return new Parsed(p.Fragment.Length == 0 ? p.Host : p.Fragment, ob);
    }
}
