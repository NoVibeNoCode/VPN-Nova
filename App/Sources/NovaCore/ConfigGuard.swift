import Foundation

/// Root-side allowlist for configs handed to the core. sing-box runs as root, so a config is
/// effectively code: `external_ui`, `log.output`, `*_path` options and extra inbounds would let
/// any process running as the user read or write files as root. The guard accepts exactly the
/// shape `ConfigBuilder` produces and rejects everything else.
public enum ConfigGuard {
    public static func validate(_ data: Data, ruleSetDirectory: String) -> [String] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return ["конфиг не JSON-объект"]
        }
        var errors: [String] = []
        func fail(_ m: String) { errors.append(m) }

        // Anything that names a file is off limits everywhere, except the rule-set paths checked below.
        func scanPaths(_ v: Any, _ at: String) {
            if let d = v as? [String: Any] {
                for (k, sub) in d {
                    let here = at.isEmpty ? k : "\(at).\(k)"
                    if k.hasSuffix("_path") || k == "external_ui" || k == "external_ui_download_url" || k == "output" {
                        fail("запрещённый ключ \(here)")
                    }
                    scanPaths(sub, here)
                }
            } else if let a = v as? [Any] {
                for (i, sub) in a.enumerated() { scanPaths(sub, "\(at)[\(i)]") }
            }
        }
        scanPaths(root, "")

        let allowedTop: Set = ["log", "dns", "inbounds", "outbounds", "route", "experimental"]
        for k in root.keys where !allowedTop.contains(k) { fail("неизвестный раздел \(k)") }

        if let log = root["log"] as? [String: Any] {
            for k in log.keys where !["level", "timestamp", "disabled"].contains(k) { fail("log.\(k)") }
        }

        // Exactly one TUN inbound, nothing listening on the network.
        let inbounds = root["inbounds"] as? [[String: Any]] ?? []
        if inbounds.count != 1 || inbounds.first?["type"] as? String != "tun" {
            fail("нужен ровно один tun-инбаунд")
        }
        let tunKeys: Set = ["type", "tag", "address", "auto_route", "strict_route", "stack", "mtu"]
        for ib in inbounds { for k in ib.keys where !tunKeys.contains(k) { fail("inbound.\(k)") } }

        if let exp = root["experimental"] as? [String: Any] {
            for k in exp.keys where !["clash_api", "cache_file"].contains(k) { fail("experimental.\(k)") }
            if let api = exp["clash_api"] as? [String: Any] {
                for k in api.keys where !["external_controller", "secret"].contains(k) { fail("clash_api.\(k)") }
                let ctl = api["external_controller"] as? String ?? ""
                if !ctl.hasPrefix("127.0.0.1:") || Int(ctl.dropFirst("127.0.0.1:".count)) == nil {
                    fail("clash_api только на 127.0.0.1")
                }
                if (api["secret"] as? String ?? "").count < 16 { fail("clash_api без секрета") }
            }
            if let cache = exp["cache_file"] as? [String: Any] {
                for k in cache.keys where !["enabled", "store_fakeip"].contains(k) { fail("cache_file.\(k)") }
            }
        }

        if let dns = root["dns"] as? [String: Any] {
            for k in dns.keys where !["servers", "rules", "final", "strategy"].contains(k) { fail("dns.\(k)") }
            for s in dns["servers"] as? [[String: Any]] ?? [] {
                if !["local", "fakeip"].contains(s["type"] as? String ?? "") { fail("dns-сервер \(s["type"] ?? "?")") }
                for k in s.keys where !["type", "tag", "inet4_range", "inet6_range"].contains(k) { fail("dns.server.\(k)") }
            }
        }

        let okTypes: Set = ["vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "wireguard",
                            "anytls", "shadowtls", "http", "socks", "direct", "selector"]
        for ob in root["outbounds"] as? [[String: Any]] ?? [] {
            if !okTypes.contains(ob["type"] as? String ?? "") { fail("outbound \(ob["type"] ?? "?")") }
        }

        // Rule sets: local files only from Nova's own folder (no symlinks out), or https downloads.
        let base = URL(fileURLWithPath: ruleSetDirectory).resolvingSymlinksInPath().path + "/"
        for rs in (root["route"] as? [String: Any])?["rule_set"] as? [[String: Any]] ?? [] {
            switch rs["type"] as? String {
            case "local":
                let p = rs["path"] as? String ?? ""
                var st = stat()
                let real = URL(fileURLWithPath: p).resolvingSymlinksInPath().path
                if !real.hasPrefix(base) || lstat(p, &st) != 0 || (st.st_mode & S_IFMT) != S_IFREG {
                    fail("набор правил вне папки Nova: \(URL(fileURLWithPath: p).lastPathComponent)")
                }
                for k in rs.keys where !["type", "tag", "format", "path"].contains(k) { fail("rule_set.\(k)") }
            case "remote":
                if !(rs["url"] as? String ?? "").hasPrefix("https://") { fail("rule_set только https") }
                for k in rs.keys where !["type", "tag", "format", "url", "download_detour", "update_interval"].contains(k) {
                    fail("rule_set.\(k)")
                }
            default:
                fail("rule_set неизвестного типа")
            }
        }
        return errors
    }
}
