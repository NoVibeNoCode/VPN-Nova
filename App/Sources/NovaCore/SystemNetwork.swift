import Foundation

/// Other VPNs on the Mac. A connected Network Extension VPN (another proxy or VPN app) grabs the
/// traffic before Nova's TUN sees it, so the app warns about it.
public enum SystemNetwork {
    /// Names of VPN services that are currently connected, e.g. `["Corp VPN"]`.
    public static func connectedVPNs() -> [String] {
        guard let out = try? run("/usr/sbin/scutil", ["--nc", "list"]) else { return [] }
        return parseConnected(out)
    }

    /// Parses `scutil --nc list`: `* (Connected)  <uuid> VPN (bundle) "Name"  [VPN:bundle]`.
    public static func parseConnected(_ output: String) -> [String] {
        output.split(separator: "\n").compactMap { line in
            guard line.contains("(Connected)") else { return nil }
            let parts = line.split(separator: "\"")
            return parts.count >= 2 ? String(parts[1]) : nil
        }
    }
}
