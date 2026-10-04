import Foundation

/// Minimal client for sing-box's Clash-compatible API.
public final class ClashAPI {
    public struct Traffic: Decodable {
        public let up: Int
        public let down: Int
    }

    public struct Connection: Decodable, Identifiable {
        public struct Metadata: Decodable {
            public let network: String?
            public let host: String?
            public let destinationIP: String?
            public let destinationPort: String?
            public let processPath: String?
        }
        public let id: String
        public let metadata: Metadata
        public let upload: Int
        public let download: Int
        public let chains: [String]
        public let rule: String?
    }

    public struct Connections: Decodable {
        public let downloadTotal: Int
        public let uploadTotal: Int
        public let connections: [Connection]?
    }

    public enum APIError: Error { case status(Int), unreachable }

    let base: URL
    let secret: String
    let session: URLSession

    public init(port: Int, secret: String) {
        base = URL(string: "http://127.0.0.1:\(port)")!
        self.secret = secret
        let cfg = URLSessionConfiguration.ephemeral
        cfg.connectionProxyDictionary = [:]
        cfg.timeoutIntervalForRequest = 8
        session = URLSession(configuration: cfg)
    }

    func request(_ path: String, method: String = "GET", query: [URLQueryItem] = [], body: Data? = nil) -> URLRequest {
        var comps = URLComponents(url: base, resolvingAgainstBaseURL: false)!
        comps.percentEncodedPath = path
        if !query.isEmpty { comps.queryItems = query }
        var req = URLRequest(url: comps.url!)
        req.httpMethod = method
        req.setValue("Bearer \(secret)", forHTTPHeaderField: "Authorization")
        if let body {
            req.httpBody = body
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        return req
    }

    static func escape(_ name: String) -> String {
        name.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(CharacterSet(charactersIn: "/"))) ?? name
    }

    @discardableResult
    func send(_ req: URLRequest) async throws -> Data {
        do {
            let (data, resp) = try await session.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard (200..<300).contains(code) else { throw APIError.status(code) }
            return data
        } catch let e as APIError {
            throw e
        } catch {
            throw APIError.unreachable
        }
    }

    public func isAlive() async -> Bool {
        (try? await send(request("/version"))) != nil
    }

    /// Delay in ms through `outbound`, nil if the server didn't answer.
    public func delay(_ outbound: String, timeoutMs: Int = 5000,
                      url: String = "https://www.gstatic.com/generate_204") async -> Int? {
        let req = request("/proxies/\(Self.escape(outbound))/delay",
                          query: [URLQueryItem(name: "timeout", value: String(timeoutMs)), URLQueryItem(name: "url", value: url)])
        guard let data = try? await send(req),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let d = obj["delay"] as? Int, d > 0 else { return nil }
        return d
    }

    public func selected(_ selector: String) async -> String? {
        guard let data = try? await send(request("/proxies/\(Self.escape(selector))")),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return obj["now"] as? String
    }

    public func select(_ selector: String, _ outbound: String) async throws {
        let body = try JSONSerialization.data(withJSONObject: ["name": outbound])
        try await send(request("/proxies/\(Self.escape(selector))", method: "PUT", body: body))
    }

    public func connections() async throws -> Connections {
        try JSONDecoder().decode(Connections.self, from: try await send(request("/connections")))
    }

    public func closeAllConnections() async {
        _ = try? await send(request("/connections", method: "DELETE"))
    }

    /// Per-second traffic stream. Ends when the core stops.
    public func traffic() -> AsyncThrowingStream<Traffic, Error> {
        let req = request("/traffic")
        let session = self.session
        return AsyncThrowingStream { cont in
            let task = Task {
                do {
                    let (bytes, _) = try await session.bytes(for: req)
                    for try await line in bytes.lines {
                        if let t = try? JSONDecoder().decode(Traffic.self, from: Data(line.utf8)) { cont.yield(t) }
                    }
                    cont.finish()
                } catch {
                    cont.finish(throwing: error)
                }
            }
            cont.onTermination = { _ in task.cancel() }
        }
    }
}
