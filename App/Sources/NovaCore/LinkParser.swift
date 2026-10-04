import Foundation

/// Parses share links (`vless://`, `vmess://`, `trojan://`, `ss://`, `hy2://`, `tuic://`)
/// into sing-box outbound objects.
public enum LinkParser {
    public struct Parsed {
        public var name: String
        public var outbound: JSONValue
    }

    public enum ParseError: LocalizedError {
        case unsupportedScheme(String)
        case malformed(String)
        case unsupportedTransport(String)

        public var errorDescription: String? {
            switch self {
            case .unsupportedScheme(let s): return "Схема «\(s)» не поддерживается. Добавь сервер через JSON."
            case .malformed(let m): return "Не получилось разобрать ссылку: \(m)"
            case .unsupportedTransport(let t): return "Транспорт «\(t)» не поддерживается sing-box."
            }
        }
    }

    public static func parse(_ raw: String) throws -> Parsed {
        let link = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let schemeEnd = link.range(of: "://") else { throw ParseError.malformed("нет схемы") }
        let scheme = link[..<schemeEnd.lowerBound].lowercased()
        switch scheme {
        case "vless": return try parseVLESSLike(link, type: "vless")
        case "trojan": return try parseVLESSLike(link, type: "trojan")
        case "vmess": return try parseVMess(link)
        case "ss": return try parseShadowsocks(link)
        case "hysteria2", "hy2": return try parseHysteria2(link)
        case "tuic": return try parseTUIC(link)
        default: throw ParseError.unsupportedScheme(scheme)
        }
    }

    // MARK: - Helpers

    struct LinkParts {
        var user: String
        var host: String
        var port: Int
        var query: [String: String]
        var fragment: String
    }

    static func split(_ link: String) throws -> LinkParts {
        // URLComponents chokes on some userinfo characters, so split by hand.
        guard let schemeEnd = link.range(of: "://") else { throw ParseError.malformed("нет схемы") }
        var rest = String(link[schemeEnd.upperBound...])
        var fragment = ""
        if let hash = rest.firstIndex(of: "#") {
            fragment = String(rest[rest.index(after: hash)...]).removingPercentEncoding ?? ""
            rest = String(rest[..<hash])
        }
        var query: [String: String] = [:]
        if let q = rest.firstIndex(of: "?") {
            for pair in rest[rest.index(after: q)...].split(separator: "&") {
                let kv = pair.split(separator: "=", maxSplits: 1).map(String.init)
                let key = kv[0].removingPercentEncoding ?? kv[0]
                let value = kv.count > 1 ? (kv[1].replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? kv[1]) : ""
                query[key] = value
            }
            rest = String(rest[..<q])
        }
        if rest.hasSuffix("/") { rest.removeLast() }
        var user = ""
        if let at = rest.lastIndex(of: "@") {
            user = String(rest[..<at]).removingPercentEncoding ?? String(rest[..<at])
            rest = String(rest[rest.index(after: at)...])
        }
        let (host, port) = try hostPort(rest)
        return LinkParts(user: user, host: host, port: port, query: query, fragment: fragment)
    }

    static func hostPort(_ s: String) throws -> (String, Int) {
        if s.hasPrefix("["), let close = s.firstIndex(of: "]") {
            let host = String(s[s.index(after: s.startIndex)..<close])
            let after = s[s.index(after: close)...]
            guard after.hasPrefix(":"), let port = Int(after.dropFirst()) else { throw ParseError.malformed("нет порта") }
            return (host, port)
        }
        guard let colon = s.lastIndex(of: ":"), let port = Int(s[s.index(after: colon)...]) else {
            throw ParseError.malformed("нет порта")
        }
        return (String(s[..<colon]), port)
    }

    static func base64Decode(_ s: String) -> String? {
        var t = s.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        while t.count % 4 != 0 { t += "=" }
        guard let d = Data(base64Encoded: t) else { return nil }
        return String(data: d, encoding: .utf8)
    }

    static func tls(serverName: String?, insecure: Bool = false, alpn: String? = nil,
                    fingerprint: String? = nil, reality: (key: String, shortID: String)? = nil) -> JSONValue {
        var t: [String: JSONValue] = ["enabled": true]
        if let sn = serverName, !sn.isEmpty { t["server_name"] = .string(sn) }
        if insecure { t["insecure"] = true }
        if let a = alpn, !a.isEmpty {
            t["alpn"] = .array(a.split(separator: ",").map { .string(String($0)) })
        }
        let fp = (fingerprint?.isEmpty == false) ? fingerprint! : (reality != nil ? "chrome" : nil)
        if let fp { t["utls"] = ["enabled": true, "fingerprint": .string(fp)] }
        if let r = reality {
            t["reality"] = ["enabled": true, "public_key": .string(r.key), "short_id": .string(r.shortID)]
        }
        return .object(t)
    }

    static func transport(type: String?, host: String?, path: String?, serviceName: String?) throws -> JSONValue? {
        switch (type ?? "tcp").lowercased() {
        case "", "tcp", "raw", "none":
            return nil
        case "ws", "websocket":
            var t: [String: JSONValue] = ["type": "ws"]
            if let p = path, !p.isEmpty { t["path"] = .string(p) }
            if let h = host, !h.isEmpty { t["headers"] = ["Host": .string(h)] }
            return .object(t)
        case "grpc":
            var t: [String: JSONValue] = ["type": "grpc"]
            if let s = serviceName, !s.isEmpty { t["service_name"] = .string(s) }
            return .object(t)
        case "http", "h2":
            var t: [String: JSONValue] = ["type": "http"]
            if let h = host, !h.isEmpty { t["host"] = .array(h.split(separator: ",").map { .string(String($0)) }) }
            if let p = path, !p.isEmpty { t["path"] = .string(p) }
            return .object(t)
        case "httpupgrade":
            var t: [String: JSONValue] = ["type": "httpupgrade"]
            if let h = host, !h.isEmpty { t["host"] = .string(h) }
            if let p = path, !p.isEmpty { t["path"] = .string(p) }
            return .object(t)
        case let other:
            throw ParseError.unsupportedTransport(other)
        }
    }

    // MARK: - Schemes

    static func parseVLESSLike(_ link: String, type: String) throws -> Parsed {
        let p = try split(link)
        guard !p.user.isEmpty else { throw ParseError.malformed(type == "vless" ? "нет UUID" : "нет пароля") }
        let q = p.query
        var ob: [String: JSONValue] = ["type": .string(type), "server": .string(p.host), "server_port": .int(p.port)]
        if type == "vless" {
            ob["uuid"] = .string(p.user)
            if let flow = q["flow"], !flow.isEmpty { ob["flow"] = .string(flow) }
            ob["packet_encoding"] = "xudp"
        } else {
            ob["password"] = .string(p.user)
        }
        let security = (q["security"] ?? (type == "trojan" ? "tls" : "none")).lowercased()
        let sni = q["sni"] ?? q["peer"] ?? q["host"]
        let insecure = q["allowInsecure"] == "1" || q["insecure"] == "1"
        switch security {
        case "reality":
            guard let pbk = q["pbk"], !pbk.isEmpty else { throw ParseError.malformed("нет pbk для Reality") }
            ob["tls"] = tls(serverName: sni, fingerprint: q["fp"], reality: (pbk, q["sid"] ?? ""))
        case "tls", "xtls":
            ob["tls"] = tls(serverName: sni, insecure: insecure, alpn: q["alpn"], fingerprint: q["fp"])
        default:
            break
        }
        if let tr = try transport(type: q["type"], host: q["host"], path: q["path"], serviceName: q["serviceName"]) {
            ob["transport"] = tr
        }
        return Parsed(name: p.fragment.isEmpty ? p.host : p.fragment, outbound: .object(ob))
    }

    static func parseVMess(_ link: String) throws -> Parsed {
        let body = String(link.dropFirst("vmess://".count))
        guard let json = base64Decode(body), let v = try? JSONValue.parse(json) else {
            throw ParseError.malformed("ожидался base64 JSON")
        }
        func str(_ k: String) -> String? {
            switch v[k] {
            case .string(let s): return s
            case .int(let i): return String(i)
            default: return nil
            }
        }
        guard let host = str("add"), let port = v["port"]?.intValue, let id = str("id") else {
            throw ParseError.malformed("нет адреса, порта или id")
        }
        var ob: [String: JSONValue] = [
            "type": "vmess", "server": .string(host), "server_port": .int(port), "uuid": .string(id),
            "security": .string(str("scy") ?? "auto"), "alter_id": .int(v["aid"]?.intValue ?? 0),
        ]
        if (str("tls") ?? "").lowercased() == "tls" {
            ob["tls"] = tls(serverName: str("sni") ?? str("host"), alpn: str("alpn"), fingerprint: str("fp"))
        }
        if let tr = try transport(type: str("net"), host: str("host"), path: str("path"), serviceName: str("path")) {
            ob["transport"] = tr
        }
        return Parsed(name: str("ps") ?? host, outbound: .object(ob))
    }

    static func parseShadowsocks(_ link: String) throws -> Parsed {
        var body = String(link.dropFirst("ss://".count))
        var name = ""
        if let hash = body.firstIndex(of: "#") {
            name = String(body[body.index(after: hash)...]).removingPercentEncoding ?? ""
            body = String(body[..<hash])
        }
        var plugin: String?
        if let q = body.firstIndex(of: "?") {
            for pair in body[body.index(after: q)...].split(separator: "&") {
                let kv = pair.split(separator: "=", maxSplits: 1).map(String.init)
                if kv[0] == "plugin", kv.count > 1 { plugin = kv[1].removingPercentEncoding ?? kv[1] }
            }
            body = String(body[..<q])
        }
        if body.hasSuffix("/") { body.removeLast() }

        var method = "", password = "", hostPortPart = ""
        if let at = body.lastIndex(of: "@") {
            // SIP002: userinfo is base64(method:password) or percent-encoded plain text.
            let userinfo = String(body[..<at])
            hostPortPart = String(body[body.index(after: at)...])
            let decoded = base64Decode(userinfo).flatMap { $0.contains(":") ? $0 : nil }
                ?? userinfo.removingPercentEncoding ?? userinfo
            guard let colon = decoded.firstIndex(of: ":") else { throw ParseError.malformed("нет метода шифрования") }
            method = String(decoded[..<colon])
            password = String(decoded[decoded.index(after: colon)...])
        } else {
            // Legacy: base64(method:password@host:port)
            guard let decoded = base64Decode(body), let at = decoded.lastIndex(of: "@"),
                  let colon = decoded[..<at].firstIndex(of: ":") else { throw ParseError.malformed("ожидался base64") }
            method = String(decoded[..<colon])
            password = String(decoded[decoded.index(after: colon)..<at])
            hostPortPart = String(decoded[decoded.index(after: at)...])
        }
        let (host, port) = try hostPort(hostPortPart)
        var ob: [String: JSONValue] = [
            "type": "shadowsocks", "server": .string(host), "server_port": .int(port),
            "method": .string(method), "password": .string(password),
        ]
        // SIP003: `plugin=obfs-local;obfs=http;obfs-host=x` → plugin + plugin_opts.
        if let plugin, !plugin.isEmpty {
            let parts = plugin.split(separator: ";", maxSplits: 1).map(String.init)
            let name = parts[0] == "simple-obfs" ? "obfs-local" : parts[0]
            guard ["obfs-local", "v2ray-plugin"].contains(name) else {
                throw ParseError.malformed("плагин «\(parts[0])» не поддерживается sing-box")
            }
            ob["plugin"] = .string(name)
            if parts.count > 1 { ob["plugin_opts"] = .string(parts[1]) }
        }
        return Parsed(name: name.isEmpty ? host : name, outbound: .object(ob))
    }

    static func parseHysteria2(_ link: String) throws -> Parsed {
        // Port is optional (443) and may be a hopping list: `host:443,20000-30000`.
        var hopping: [String] = []
        var normalized = link
        if let schemeEnd = link.range(of: "://") {
            let rest = link[schemeEnd.upperBound...]
            let authorityEnd = rest.firstIndex(where: { "/?#".contains($0) }) ?? rest.endIndex
            let authority = String(rest[..<authorityEnd])
            let at = authority.lastIndex(of: "@").map { authority.index(after: $0) } ?? authority.startIndex
            var hostPart = String(authority[at...])
            let closing = hostPart.lastIndex(of: "]")
            if let colon = hostPart.lastIndex(of: ":"), closing.map({ colon > $0 }) ?? !hostPart.hasPrefix("[") {
                let spec = String(hostPart[hostPart.index(after: colon)...])
                if Int(spec) == nil {
                    var first: Int?
                    for item in spec.split(separator: ",") {
                        let r = item.split(separator: "-").compactMap { Int($0) }
                        if r.count == 1 { first = first ?? r[0] } else if r.count == 2 { hopping.append("\(r[0]):\(r[1])"); first = first ?? r[0] }
                        else { throw ParseError.malformed("порты «\(spec)»") }
                    }
                    hostPart = String(hostPart[..<colon]) + ":\(first ?? 443)"
                }
            } else {
                hostPart += ":443"
            }
            normalized = String(link[..<schemeEnd.upperBound]) + authority[..<at] + hostPart + rest[authorityEnd...]
        }
        let p = try split(normalized)
        let q = p.query
        var ob: [String: JSONValue] = [
            "type": "hysteria2", "server": .string(p.host), "server_port": .int(p.port), "password": .string(p.user),
        ]
        if !hopping.isEmpty { ob["server_ports"] = .array(hopping.map { .string($0) }) }
        if let obfs = q["obfs"], !obfs.isEmpty {
            ob["obfs"] = ["type": .string(obfs), "password": .string(q["obfs-password"] ?? "")]
        }
        ob["tls"] = tls(serverName: q["sni"] ?? p.host, insecure: q["insecure"] == "1", alpn: q["alpn"])
        return Parsed(name: p.fragment.isEmpty ? p.host : p.fragment, outbound: .object(ob))
    }

    static func parseTUIC(_ link: String) throws -> Parsed {
        let p = try split(link)
        let q = p.query
        let creds = p.user.split(separator: ":", maxSplits: 1).map(String.init)
        guard creds.count == 2 else { throw ParseError.malformed("ожидалось uuid:пароль") }
        var ob: [String: JSONValue] = [
            "type": "tuic", "server": .string(p.host), "server_port": .int(p.port),
            "uuid": .string(creds[0]), "password": .string(creds[1]),
        ]
        if let cc = q["congestion_control"], !cc.isEmpty { ob["congestion_control"] = .string(cc) }
        ob["tls"] = tls(serverName: q["sni"] ?? p.host, insecure: q["allow_insecure"] == "1" || q["insecure"] == "1",
                        alpn: q["alpn"] ?? "h3")
        return Parsed(name: p.fragment.isEmpty ? p.host : p.fragment, outbound: .object(ob))
    }
}
